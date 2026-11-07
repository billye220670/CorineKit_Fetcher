using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Path = System.IO.Path;

namespace ImageSelector
{
    /// <summary>
    /// 媒体种类。GIF 单独分一类是因为它虽然走图片管线，但需要逐帧推进才有动画。
    /// </summary>
    public enum MediaKind
    {
        None,
        Image,
        Gif,
        Video
    }

    /// <summary>
    /// 支持格式的唯一来源。
    ///
    /// 之前扩展名清单在 GetImageFiles 和 ContainsImages 里各硬编码了一份，而且不一致
    /// （.tiff 只在后者），导致右键同级目录菜单会列出含 tiff 的文件夹、点进去却提示
    /// "没有找到任何图片文件"。任何格式增删都只改这里。
    ///
    /// 清单以本机实测为准：png/jpg/jpeg/bmp/tif/tiff/gif 由 WPF 自带的 WIC 解码器直接支持；
    /// webp 走 WIC 的 "Microsoft Webp Decoder"，即商店扩展 Microsoft.WebpImageExtension，
    /// 未安装该扩展的机器上会解码失败，由 DisplayMedia 兜住并跳过该文件。
    /// </summary>
    public static class MediaFormats
    {
        public const string GifExtension = ".gif";

        public static readonly string[] ImageExtensions =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"
        };

        /// <summary>
        /// 视频格式。能否真正播放取决于系统 Media Foundation 解码器，
        /// mp4(H.264) 与 mkv 已实测可用，其余随系统编解码器而定，
        /// 播放失败由 MediaElement 的 MediaFailed 事件兜住。
        /// </summary>
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".mpg", ".mpeg", ".3gp"
        };

        /// <summary>目录扫描用的完整白名单。</summary>
        public static readonly string[] AllExtensions =
            ImageExtensions.Concat(new[] { GifExtension }).Concat(VideoExtensions).ToArray();

        private static readonly HashSet<string> imageSet =
            new HashSet<string>(ImageExtensions, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> videoSet =
            new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> allSet =
            new HashSet<string>(AllExtensions, StringComparer.OrdinalIgnoreCase);

        /// <summary>取小写扩展名（含点），路径异常时返回空串而不是抛出。</summary>
        public static string ExtensionOf(string path)
        {
            try
            {
                return (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        public static bool IsSupported(string path)
        {
            return allSet.Contains(ExtensionOf(path));
        }

        public static MediaKind Classify(string path)
        {
            string ext = ExtensionOf(path);
            if (ext.Length == 0) return MediaKind.None;
            if (string.Equals(ext, GifExtension, StringComparison.OrdinalIgnoreCase)) return MediaKind.Gif;
            if (imageSet.Contains(ext)) return MediaKind.Image;
            if (videoSet.Contains(ext)) return MediaKind.Video;
            return MediaKind.None;
        }
    }
}
