using System;
using System.Collections.Generic;
using System.IO;

namespace ImageSelector
{
    /// <summary>
    /// 面包屑上的一段：显示文本、真实完整路径、以及是否允许点击。
    /// </summary>
    public sealed class PathSegment
    {
        public string Label { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool Clickable { get; set; }
    }

    /// <summary>
    /// 目录路径 ↔ 面包屑分段的换算。
    ///
    /// 独立成类的两个原因：
    /// 1. 原先用 Path.Combine("", "C:") 逐级拼接会得到 "C:Users" 这种驱动器相对路径，
    ///    只有当进程当前目录恰好是 C:\ 时才碰巧解析成功，属于潜在 bug，这里改为从盘符根
    ///    (C:\) 或 UNC 根 (\\server\share) 开始拼接。
    /// 2. 纯字符串逻辑，不依赖 WPF，可以独立验证。
    /// </summary>
    public static class PathBreadcrumb
    {
        private static readonly char[] Separators =
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        };

        /// <summary>
        /// 把目录路径拆成面包屑段。
        /// 盘符段（C:）与 UNC 主机段（\\server）标记为不可点击，仅作展示。
        /// </summary>
        public static List<PathSegment> Split(string directory)
        {
            var segments = new List<PathSegment>();
            if (string.IsNullOrEmpty(directory)) return segments;

            string[] parts = directory.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return segments;

            bool isUnc = directory.StartsWith(@"\\", StringComparison.Ordinal);
            string current;
            int startIndex;

            if (isUnc)
            {
                // \\server\share\... ：主机名不可点，共享名才是第一个可用的根
                segments.Add(new PathSegment
                {
                    Label = @"\\" + parts[0],
                    FullPath = @"\\" + parts[0],
                    Clickable = false
                });
                if (parts.Length < 2) return segments;

                current = @"\\" + parts[0] + @"\" + parts[1];
                segments.Add(new PathSegment { Label = parts[1], FullPath = current, Clickable = true });
                startIndex = 2;
            }
            else if (IsDriveLetter(parts[0]))
            {
                // C:\... ：盘符段不可点，避免误触把整个盘扫一遍
                current = parts[0] + Path.DirectorySeparatorChar;
                segments.Add(new PathSegment { Label = parts[0], FullPath = current, Clickable = false });
                startIndex = 1;
            }
            else
            {
                // 相对路径兜底
                current = parts[0];
                segments.Add(new PathSegment { Label = parts[0], FullPath = current, Clickable = true });
                startIndex = 1;
            }

            for (int i = startIndex; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                segments.Add(new PathSegment { Label = parts[i], FullPath = current, Clickable = true });
            }

            return segments;
        }

        /// <summary>
        /// 去掉尾部分隔符，用于把 "C:\" 与 "C:"、"a\b\" 与 "a\b" 视为同一路径。
        /// </summary>
        public static string NormalizeForCompare(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsDriveLetter(string segment)
        {
            return segment.Length == 2 && segment[1] == ':';
        }
    }
}
