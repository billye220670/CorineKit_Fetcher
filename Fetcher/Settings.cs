using System;
using System.IO;
using System.Windows.Media;
using System.Xml.Serialization;

namespace ImageSelector
{
    [Serializable]
    public class AppSettings
    {
        // 在现有属性中添加这个
        public string LastPath { get; set; } = "";

        public int ColorPreset { get; set; } = 0;
        public int CacheSize { get; set; } = 5;
        
        // 添加语言设置属性
        public int LanguageSetting { get; set; } = 2; // 默认为日语 (索引2)

        // 滚轮的随机区：0 = 悬停面包屑时随机（其余区域顺序），1 = 悬停其余区域时随机（面包屑顺序）
        public int WheelRandomZone { get; set; } = 0;

        // 获取当前语言
        public Language CurrentLanguage => (Language)LanguageSetting;

        // 背景颜色
        public byte BackgroundColorR { get; set; } = 240;
        public byte BackgroundColorG { get; set; } = 240;
        public byte BackgroundColorB { get; set; } = 240;
        // 设置颜色预设
        public void SetColorPreset(int preset)
        {
            ColorPreset = preset;
            Save();
        }
        
        // 设置语言
        public void SetLanguage(int languageIndex)
        {
            LanguageSetting = languageIndex;
            Save();
        }

        // 设置滚轮的随机区
        public void SetWheelRandomZone(int zone)
        {
            WheelRandomZone = zone;
            Save();
        }
        
        //获取当前预设背景颜色
        public Color GetBackgroundColorFromPresetID()
        {
            switch (ColorPreset)
            {
                case 0: // 默认白色
                    return Color.FromRgb(240, 240, 240);
                case 1: // 深色
                    return Color.FromRgb(20, 20, 20);
                case 2: // 深紫色
                    return (Color)ColorConverter.ConvertFromString("#28232F");
                case 3: // 深蓝色
                    return (Color)ColorConverter.ConvertFromString("#161724");
                case 4: // 军绿色
                    return (Color)ColorConverter.ConvertFromString("#26241B");
                case 5: // 深棕色
                    return (Color)ColorConverter.ConvertFromString("#1F1B1B");
                default:
                    return Color.FromRgb(240, 240, 240); // 默认白色
            }
            
            
        }
        public Color GetButtonColorFromPresetID()
        {
            switch (ColorPreset)
            {
                case 0: // 背景为白色时
                    return Color.FromRgb(237, 237, 237);
                case 1: // 背景为深色时
                    return Color.FromRgb(68, 68, 68);
                case 2: // 背景为深色时
                    return Color.FromRgb(68, 68, 68);
                case 3: // 背景为深色时
                    return Color.FromRgb(68, 68, 68);
                case 4: // 背景为深色时
                    return Color.FromRgb(68, 68, 68);
                case 5: // 背景为深色时
                    return Color.FromRgb(68, 68, 68);
                default:
                    return Color.FromRgb(237, 237, 237); // 默认白色
            }
            
            
        }
        public Color GetForegroundColorFromPresetID()
        {
            switch (ColorPreset)
            {
                case 0: // 背景为白色时
                    return Colors.Black;
                case 1: // 背景为深色时
                    return Colors.White;
                case 2: // 背景为深色时
                    return Colors.White;
                case 3: // 背景为深色时
                    return Colors.White;
                case 4: // 背景为深色时
                    return Colors.White;
                case 5: // 背景为深色时
                    return Colors.White;
                default:
                    return Colors.White; // 默认白色
            }
            
            
        }
        // 从设置中获取颜色
        public Color GetBackgroundColor()
        {
            return Color.FromRgb(BackgroundColorR, BackgroundColorG, BackgroundColorB);
        }
        
        // 设置颜色
        public void SetBackgroundColor(Color color)
        {
            BackgroundColorR = color.R;
            BackgroundColorG = color.G;
            BackgroundColorB = color.B;
        }
        
        private static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CorineKitFetcher");

        private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.xml");

        private static string BackupPath => Path.Combine(SettingsDirectory, "settings_backup.xml");

        // 旧版本把设置存在 %APPDATA%\ImageViewer，那是和 CorineKit_Gazer 共用的目录，
        // 两边的根元素都叫 <AppSettings>。XmlSerializer 对不认识的元素静默忽略、对缺失的
        // 元素填类初始值，所以 Gazer 存一次盘，Fetcher 的主题色 / 语言 / 上次路径就全变回
        // 默认，而且不抛异常——只在反序列化失败时才回退备份的逻辑因此永远不会触发。
        // 现在搬到独立目录，旧目录只做一次只读迁移，不写也不删（那还是 Gazer 的文件）。
        private static string LegacyDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageViewer");

        // 只有 Fetcher 才有的元素名，用来辨认旧文件到底是谁写的
        private static readonly string[] OwnerMarkers =
        {
            "<LastPath>", "<ColorPreset>", "<CacheSize>", "<LanguageSetting>", "<WheelRandomZone>"
        };

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);

                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        File.Copy(SettingsPath, BackupPath, true);
                    }
                    catch { }
                }

                // 先写临时文件再整份替换。FileMode.Create 会立刻把目标截断成 0 字节，
                // 进程在这中间被结束就会留下半个 XML。
                string tempPath = SettingsPath + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, this);
                }
                File.Move(tempPath, SettingsPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsPath))
            {
                return MigrateFromLegacyDirectory() ?? new AppSettings();
            }

            AppSettings? result = LoadFromFile(SettingsPath);
            if (result == null && File.Exists(BackupPath))
            {
                result = LoadFromFile(BackupPath);
            }
            return result ?? new AppSettings();
        }

        private static AppSettings? MigrateFromLegacyDirectory()
        {
            string[] legacyPaths =
            {
                Path.Combine(LegacyDirectory, "settings.xml"),
                Path.Combine(LegacyDirectory, "settings_backup.xml")
            };

            foreach (string legacyPath in legacyPaths)
            {
                if (!LooksLikeFetcherSettings(legacyPath)) continue;

                AppSettings? migrated = LoadFromFile(legacyPath);
                if (migrated == null) continue;

                // 立刻落盘：旧目录仍然归 Gazer 写，等到关窗再存就可能已经被它覆盖掉了。
                migrated.Save();
                return migrated;
            }

            return null;
        }

        private static bool LooksLikeFetcherSettings(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;

                string content = File.ReadAllText(path);
                foreach (string marker in OwnerMarkers)
                {
                    if (content.Contains(marker)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error inspecting legacy settings {path}: {ex.Message}");
                return false;
            }
        }

        private static AppSettings? LoadFromFile(string path)
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream stream = new FileStream(path, FileMode.Open))
                {
                    return serializer.Deserialize(stream) as AppSettings;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading settings from {path}: {ex.Message}");
                return null;
            }
        }
    }
}

