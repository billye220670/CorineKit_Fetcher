using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;

namespace ImageSelector
{
    /// <summary>
    /// 把视频文件解析成 WPF MediaElement 真能打开的路径。
    ///
    /// 为什么需要这一层：MediaElement 走的是 Media Foundation 的老管线，它按扩展名查
    /// "字节流处理器"，而 Windows 内置的那张表里有 .mkv 却没有 .webm。实测结论：
    ///   - 同一份 VP8/VP9 webm 字节，改名成 .mkv 后 MediaElement 正常播放（含音轨）；
    ///   - 系统解码器并不缺：WinRT MediaPlayer（现代管线）直接就能播 .webm；
    ///   - HKCU 注册表注册字节流处理器无效；
    ///   - MFCreateMediaExtensionActivate + MFRegisterLocalByteStreamHandler 进程内注册
    ///     返回 S_OK 但依然无效（用自定义扩展名做对照实验确认老管线不查这张表）。
    /// 所以在不引入第三方播放器的前提下，唯一可行的办法是给 MediaElement 一个 .mkv 名字。
    ///
    /// 实现用 NTFS 硬链接：零拷贝、只是一次元数据操作，切换视频没有可感知的延迟。
    /// 硬链接不能跨卷（实测 ERROR_NOT_SAME_DEVICE），所以缓存目录落在源文件所在的卷上；
    /// 实在建不了硬链接时退化为复制一份。
    /// </summary>
    internal sealed class VideoSourceResolver : IDisposable
    {
        /// <summary>Media Foundation 老管线认不出、需要换名投喂的扩展名。</summary>
        private static readonly string[] needsAlias = { ".webm" };

        /// <summary>投喂给 MediaElement 的替身扩展名，必须在 MF 内置表里。</summary>
        private const string AliasExtension = ".mkv";

        /// <summary>非系统卷上的替身目录名（系统卷走 %LOCALAPPDATA%，见 GetAliasDirectory）。</summary>
        private const string AliasDirectoryName = ".corinekit_media";

        private const int ERROR_NOT_SAME_DEVICE = 17;

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private string? currentAlias;

        /// <summary>最近一次解析失败的原因，成功时为 null。</summary>
        public string? LastError { get; private set; }

        /// <summary>当前是否正在占用一个替身文件（需要在离开视频时释放）。</summary>
        public bool HasAlias => currentAlias != null;

        public static bool NeedsAlias(string path)
        {
            string ext = MediaFormats.ExtensionOf(path);
            foreach (string candidate in needsAlias)
            {
                if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// 返回可供 MediaElement 打开的路径；失败返回 null 并填充 <see cref="LastError"/>。
        /// 不需要替身的格式直接原样返回，不产生任何磁盘写入。
        /// </summary>
        public string? Resolve(string mediaPath)
        {
            LastError = null;
            if (!NeedsAlias(mediaPath)) return mediaPath;

            // 换文件前先释放上一个替身，磁盘占用因此恒定在"最多一份"
            Release();

            try
            {
                string alias = BuildAliasPath(mediaPath);
                if (File.Exists(alias))
                {
                    currentAlias = alias;
                    return alias;
                }

                string? directory = Path.GetDirectoryName(alias);
                if (!string.IsNullOrEmpty(directory)) CreateAliasDirectory(directory);

                if (TryCreateHardLink(alias, mediaPath) || TryCopy(alias, mediaPath))
                {
                    currentAlias = alias;
                    return alias;
                }

                LastError = "无法为该 webm 建立 .mkv 替身：" + LastError;
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        private bool TryCreateHardLink(string alias, string source)
        {
            if (CreateHardLink(alias, source, IntPtr.Zero)) return true;

            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_SAME_DEVICE)
            {
                LastError = "替身目录与源文件不在同一个卷，硬链接不可用";
            }
            else
            {
                LastError = $"CreateHardLink 失败 (Win32 错误 {error})";
            }
            return false;
        }

        private bool TryCopy(string alias, string source)
        {
            try
            {
                File.Copy(source, alias, true);
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"复制替身失败：{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 替身目录。必须和源文件同卷，否则硬链接必然失败（实测 ERROR_NOT_SAME_DEVICE）。
        /// 源文件在系统盘时用 %LOCALAPPDATA%，那是保证可写的；其他卷退到卷根的隐藏目录
        /// ——数据卷的根目录普通用户一般可写，而 C:\ 根目录默认要提权，所以不能一律放卷根。
        /// </summary>
        private static string GetAliasDirectory(string mediaPath)
        {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(mediaPath)) ?? string.Empty;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localRoot = Path.GetPathRoot(localAppData) ?? string.Empty;

            if (string.Equals(sourceRoot, localRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(localAppData, "CorineKitFetcher", "media");
            }
            return Path.Combine(sourceRoot, AliasDirectoryName);
        }

        /// <summary>
        /// 建替身目录。落在卷根的那种（<see cref="AliasDirectoryName"/>）会顺手打上隐藏属性，
        /// 免得用户在资源管理器里看到一个来路不明的文件夹；%LOCALAPPDATA% 下则不隐藏。
        /// 属性设置失败无所谓，只影响观感。
        /// </summary>
        private static void CreateAliasDirectory(string directory)
        {
            Directory.CreateDirectory(directory);

            if (!directory.EndsWith(AliasDirectoryName, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error hiding alias directory {directory}: {ex.Message}");
            }
        }

        /// <summary>
        /// 替身路径。文件名由"路径 + 长度 + 修改时间"哈希得出：内容变了就不会复用旧替身，
        /// 同一个文件重复浏览也不会反复重建。
        /// </summary>
        private static string BuildAliasPath(string mediaPath)
        {
            var info = new FileInfo(mediaPath);
            string identity = $"{mediaPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

            string hash;
            using (var sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var builder = new StringBuilder(16);
                for (int i = 0; i < 8; i++) builder.Append(digest[i].ToString("x2"));
                hash = builder.ToString();
            }

            return Path.Combine(GetAliasDirectory(mediaPath), hash + AliasExtension);
        }

        private static bool IsInsideAliasDirectory(string path)
        {
            return GetAliasDirectories().Contains(Path.GetDirectoryName(path) ?? string.Empty,
                                                  StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>可能存放替身的所有目录，用于清理和删除前的安全校验。</summary>
        private static IEnumerable<string> GetAliasDirectories()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "CorineKitFetcher", "media");

            foreach (string drive in Directory.GetLogicalDrives())
            {
                yield return Path.Combine(drive, AliasDirectoryName);
            }
        }

        /// <summary>
        /// 释放当前替身。删的是我们自己建的链接/副本：删硬链接只是减少引用计数，
        /// 不会动到源文件。为稳妥起见只删落在替身目录里的路径。
        /// </summary>
        public void Release()
        {
            string? alias = currentAlias;
            currentAlias = null;
            if (alias == null) return;

            try
            {
                if (!IsInsideAliasDirectory(alias)) return;
                if (File.Exists(alias)) File.Delete(alias);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error releasing media alias {alias}: {ex.Message}");
            }
        }

        /// <summary>
        /// 清掉替身目录里的残留（异常退出留下的）。启动时调用一次。
        /// </summary>
        public static void PurgeAliases()
        {
            foreach (string cacheDir in GetAliasDirectories())
            {
                try
                {
                    if (!Directory.Exists(cacheDir)) continue;

                    foreach (string file in Directory.GetFiles(cacheDir, "*" + AliasExtension))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex) { Console.WriteLine($"Error purging {file}: {ex.Message}"); }
                    }

                    if (cacheDir.EndsWith(AliasDirectoryName, StringComparison.OrdinalIgnoreCase)
                        && Directory.GetFiles(cacheDir).Length == 0)
                    {
                        Directory.Delete(cacheDir);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error purging media alias directory {cacheDir}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Release();
        }
    }
}
