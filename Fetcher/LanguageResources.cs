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
            {"PathButtonTooltip", new Dictionary<Language, string>
                {
                    {Language.Chinese, "单击切换路径，Ctrl+单击打开文件夹"},
                    {Language.English, "Click to switch path, Ctrl+Click to open folder"},
                    {Language.Japanese, "クリックでパスを切り替え、Ctrl+クリックでフォルダを開く"}
                }
            },
            {"FileButtonTooltip", new Dictionary<Language, string>
                {
                    {Language.Chinese, "单击以默认应用打开"},
                    {Language.English, "Click to open with default application"},
                    {Language.Japanese, "クリックでデフォルトアプリケーションで開く"}
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
