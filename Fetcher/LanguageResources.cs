using System.Collections.Generic;

namespace ImageSelector
{
    public enum Language
    {
        Chinese,
        English,
        Japanese
    }
    
    public static class LanguageResources
    {
        public static Dictionary<string, Dictionary<Language, string>> Resources = new Dictionary<string, Dictionary<Language, string>>
        {
            {"WindowTitle", new Dictionary<Language, string>
                {
                    {Language.Chinese, "抽选者Beta"},
                    {Language.English, "Fetcher Beta"},
                    {Language.Japanese, "抽選者Beta"}
                }
            },
            {"HintText", new Dictionary<Language, string>
                {
                    {Language.Chinese, "将文件夹拖放到此处打开"},
                    {Language.English, "Drag & drop folder here to open"},
                    {Language.Japanese, "フォルダをここにドラッグ＆ドロップして開く"}
                }
            },
            {"RestoreSession", new Dictionary<Language, string>
                {
                    {Language.Chinese, "恢复上次会话"},
                    {Language.English, "Restore last session"},
                    {Language.Japanese, "前回の会話を復元する"}
                }
            },
            {"Settings", new Dictionary<Language, string>
                {
                    {Language.Chinese, "设置"},
                    {Language.English, "Settings"},
                    {Language.Japanese, "設定"}
                }
            },
            {"CacheSize", new Dictionary<Language, string>
                {
                    {Language.Chinese, "缓存页数:"},
                    {Language.English, "Cache size:"},
                    {Language.Japanese, "キャッシュページ数:"}
                }
            },
            {"BackgroundColor", new Dictionary<Language, string>
                {
                    {Language.Chinese, "背景颜色:"},
                    {Language.English, "Background color:"},
                    {Language.Japanese, "背景色:"}
                }
            },
            {"Language", new Dictionary<Language, string>
                {
                    {Language.Chinese, "语言:"},
                    {Language.English, "Language:"},
                    {Language.Japanese, "言語:"}
                }
            },
            {"Chinese", new Dictionary<Language, string>
                {
                    {Language.Chinese, "中文"},
                    {Language.English, "Chinese"},
                    {Language.Japanese, "中国語"}
                }
            },
            {"English", new Dictionary<Language, string>
                {
                    {Language.Chinese, "英文"},
                    {Language.English, "English"},
                    {Language.Japanese, "英語"}
                }
            },
            {"Japanese", new Dictionary<Language, string>
                {
                    {Language.Chinese, "日文"},
                    {Language.English, "Japanese"},
                    {Language.Japanese, "日本語"}
                }
            },
            {"WheelRandomZone", new Dictionary<Language, string>
                {
                    {Language.Chinese, "滚轮随机区:"},
                    {Language.English, "Random wheel zone:"},
                    {Language.Japanese, "ホイールランダム域:"}
                }
            },
            {"WheelZoneBreadcrumb", new Dictionary<Language, string>
                {
                    {Language.Chinese, "悬停面包屑时"},
                    {Language.English, "On breadcrumb"},
                    {Language.Japanese, "パスバー上"}
                }
            },
            {"WheelZoneElsewhere", new Dictionary<Language, string>
                {
                    {Language.Chinese, "悬停其他区域时"},
                    {Language.English, "Anywhere else"},
                    {Language.Japanese, "その他の領域"}
                }
            },
            {"PathButtonTooltip", new Dictionary<Language, string>
                {
                    {Language.Chinese, "左键切换路径，中键在资源管理器中打开，右键查看同级目录"},
                    {Language.English, "Left-click to switch path, middle-click to open in Explorer, right-click for sibling folders"},
                    {Language.Japanese, "左クリックでパス切替、中クリックでエクスプローラーを開く、右クリックで同階層を表示"}
                }
            },
            {"FileButtonTooltip", new Dictionary<Language, string>
                {
                    {Language.Chinese, "单击以默认应用打开"},
                    {Language.English, "Click to open with default application"},
                    {Language.Japanese, "クリックでデフォルトアプリケーションで開く"}
                }
            },
            {"Notice", new Dictionary<Language, string>
                {
                    {Language.Chinese, "提示"},
                    {Language.English, "Notice"},
                    {Language.Japanese, "お知らせ"}
                }
            },
            {"NoMediaFound", new Dictionary<Language, string>
                {
                    {Language.Chinese, "没有找到任何受支持的图片或视频文件。"},
                    {Language.English, "No supported image or video files were found."},
                    {Language.Japanese, "サポートされている画像・動画ファイルが見つかりませんでした。"}
                }
            },
            // webp 不是 WPF 自带解码器，靠商店扩展 Microsoft.WebpImageExtension 提供的
            // WIC 编解码器；没装的机器上每张 webp 都会解码失败，所以要点明去哪儿装。
            {"WebpCodecMissing", new Dictionary<Language, string>
                {
                    {Language.Chinese, "系统缺少 WebP 解码器，已跳过 .webp 文件。请在 Microsoft Store 安装“WebP 图像扩展”(Microsoft.WebpImageExtension)。"},
                    {Language.English, "This system has no WebP decoder, so .webp files were skipped. Install \"WebP Image Extensions\" (Microsoft.WebpImageExtension) from the Microsoft Store."},
                    {Language.Japanese, "WebP デコーダーがないため .webp ファイルをスキップしました。Microsoft Store から「WebP 画像拡張機能」(Microsoft.WebpImageExtension) をインストールしてください。"}
                }
            },
            {"VideoCodecMissing", new Dictionary<Language, string>
                {
                    {Language.Chinese, "系统缺少该视频的解码器，已跳过。webm 需要“Web 媒体扩展”，VP9 / AV1 还需要对应的视频扩展。"},
                    {Language.English, "This system lacks the decoder for that video, so it was skipped. webm needs \"Web Media Extensions\"; VP9 / AV1 also need their matching video extensions."},
                    {Language.Japanese, "この動画のデコーダーがないためスキップしました。webm には「Web メディア拡張機能」、VP9 / AV1 には対応する動画拡張機能が必要です。"}
                }
            },
            {"TooManyMediaFailures", new Dictionary<Language, string>
                {
                    {Language.Chinese, "连续多个文件都无法显示，已停止切换。这个文件夹可能整体缺少解码器。"},
                    {Language.English, "Too many files in a row failed to display, so switching has stopped. This folder may be missing codecs entirely."},
                    {Language.Japanese, "連続して複数のファイルを表示できなかったため切り替えを停止しました。このフォルダーはコーデックが不足している可能性があります。"}
                }
            }
        };
        
        public static string GetText(string key, Language language)
        {
            if (Resources.ContainsKey(key) && Resources[key].ContainsKey(language))
            {
                return Resources[key][language];
            }
            return key;
        }
    }
}
