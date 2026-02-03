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
        
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageViewer",
            "settings.xml");

        private static string BackupPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageViewer",
            "settings_backup.xml");

        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        File.Copy(SettingsPath, BackupPath, true);
                    }
                    catch { }
                }

                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream stream = new FileStream(SettingsPath, FileMode.Create))
                {
                    serializer.Serialize(stream, this);
                }
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
                if (File.Exists(BackupPath))
                {
                    return LoadFromFile(BackupPath);
                }
                return new AppSettings();
            }

            var result = LoadFromFile(SettingsPath);
            if (result == null && File.Exists(BackupPath))
            {
                return LoadFromFile(BackupPath);
            }
            return result ?? new AppSettings();
        }

        private static AppSettings LoadFromFile(string path)
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream stream = new FileStream(path, FileMode.Open))
                {
                    return (AppSettings)serializer.Deserialize(stream);
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

