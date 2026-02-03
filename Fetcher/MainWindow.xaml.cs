using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace ImageSelector
{
    public partial class MainWindow : Window
    {
        private Ellipse[] dots;
        private bool loaded = false;
        private DateTime lastClickTime;
        private const int doubleClickInterval = 300; // 双击的时间间隔
        private List<string> imageFiles = new List<string>();
        private int currentIndex = -1; // 当前显示的图片索引
        private int cacheSize = 5; // 缓存大小
        private List<string> viewedImages = new List<string>(); // 记录查看过的图片
        private int viewedIndex = -1; // 当前查看的图片在缓存中的索引
        private string currentDirectory; // 当前目录路径
        private string currentImagePath; // 当前图片路径
        // 应用设置
        public AppSettings settings;
        
        public MainWindow()
        {
            InitializeComponent();
            settings = AppSettings.Load();
            SetupInitialUI(); // 设置初始UI
            RestoreSettings();
            updateCacheSize(settings.CacheSize);
            CacheSizeSlider.Value = settings.CacheSize;
            
            // 初始化界面语言
            UpdateUILanguage(settings.CurrentLanguage);
            
            // 根据当前语言设置选中相应的选项
            if (LanguageComboBox != null && LanguageComboBox.Items.Count > settings.LanguageSetting)
            {
                LanguageComboBox.SelectedIndex = settings.LanguageSetting;
            }
            
            // 窗口关闭时保存设置
            Closing += (s, e) => {
                settings.Save();
            };
            loaded = true;
        }

        private void RestoreSettings()
        {
            HintTextBlock.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            SettingsPanel.Background = new SolidColorBrush(settings.GetBackgroundColorFromPresetID()*4);
            SettingTittle.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            CacheSizeText.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            BackgroundColor.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            LanguageLabel.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            this.Background = new SolidColorBrush(settings.GetBackgroundColorFromPresetID()); //恢复上次会话的背景色
            
        }

        private void SetupInitialUI()
        {
            // 检查 lastPath 是否有效，并在 UI 中显示相应的按钮
            if (!string.IsNullOrEmpty(settings.LastPath))
            {
                // 显示恢复上次会话路径按钮
                Button restoreButton = new Button
                {
                    Content = LanguageResources.GetText("RestoreSession", settings.CurrentLanguage),
                    Margin = new Thickness(24),
                    Padding = new Thickness(12),
                    Focusable = false
                };
                restoreButton.Background = new SolidColorBrush(settings.GetButtonColorFromPresetID());
                restoreButton.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
                restoreButton.Click += RestoreButton_Click; // 绑定点击事件

                // 将按钮添加到 StackPanel 中
                HintArea.Children.Add(restoreButton); // 确保将其添加到 UI 中的合适位置
            }
        }
        //-----------------------------恢复上次会话路径-------------------------
        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            LoadImagesFromDirectory(settings.LastPath); // 恢复上次会话路径
        }
        private void LoadSettings()
        {
            // 在窗口加载时，将缓存页数的输入框设置为常量 cacheSize 的值
            CacheSizeSlider.Value = cacheSize;
            CacheSizeText.Text = "缓存页数:"+cacheSize.ToString()+"张";
        }
        private void CacheSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            cacheSize = (int)CacheSizeSlider.Value;
            
            // 添加空检查，确保settings和CurrentLanguage都可用
            if (settings != null)
            {
                // 根据当前语言设置显示对应的文本
                string cacheSizeText = LanguageResources.GetText("CacheSize", settings.CurrentLanguage);
                CacheSizeText.Text = $"{cacheSizeText}{cacheSize}"; // 使用当前语言的缓存大小文本
                
                if (loaded)
                {
                    settings.CacheSize = cacheSize;
                    updateCacheSize(settings.CacheSize);
                    settings.Save();
                }
            }
            else
            {
                // 如果settings还未初始化，使用默认文本
                CacheSizeText.Text = $"Cache size: {cacheSize}";
            }
        }

        private void updateCacheSize(int size)
        {
             cacheSize = size;
             viewedImages.Clear(); // 清空缓存
             viewedIndex = -1; // 重新开始索引
             InitializeIndicators();
        }
        
        
        
        //---------------------------鼠标拖拽图片-------------------------
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                // 开始拖动操作
                DataObject data = new DataObject(DataFormats.FileDrop, new[] { currentImagePath });
                DragDrop.DoDragDrop(this, data, DragDropEffects.Copy); // 拖动效果为复制
            }
        }
        //---------------------------拖放图片进入APP-------------------------
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] filePaths = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (string filePath in filePaths)
                {
                    if (Directory.Exists(filePath))
                    {
                        LoadImagesFromDirectory(filePath); // 从新目录加载图像
                    }
                }
            }
        }
        private void UpdateHintVisibility()
        {
            // 根据是否有有效路径来控制提示的可见性
            HintTextBlock.Visibility = Visibility.Collapsed;
        }

        private void LoadImagesFromDirectory(string directory)
        {
            
            currentDirectory = directory; // 更新当前目录
            settings.LastPath = directory; //更新设置中的LastPath
            settings.Save();
            imageFiles = GetImageFiles(directory);
            if (imageFiles.Any())
            {
                UpdateHintVisibility(); // 更新提示信息的可见性
                UpdatePathButtons(directory, null); //根据当前目录更新路径按钮，并且不显示文件名
                LoadRandomImage(); // 初始化显示随机图片
            }
            else
            {
                MessageBox.Show("没有找到任何图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private List<string> GetImageFiles(string directory)
        {
            List<string> files = new List<string>();
            try
            {
                string[] supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
                files.AddRange(Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                                          .Where(file => supportedExtensions.Contains(Path.GetExtension(file).ToLower())));
            }
            catch (Exception ex)
            {
                MessageBox.Show("发生错误: " + ex.Message);
            }
            return files;
        }

        private void LoadRandomImage()  
        {
            if (!imageFiles.Any()) return;
            Random rand = new Random();
            currentIndex = rand.Next(imageFiles.Count);
            DisplayImage(imageFiles[currentIndex]);
        }

        private void DisplayImage(string imagePath)
        {
            currentImagePath = imagePath; // 更新当前图片路径
            UpdatePathButtons(Path.GetDirectoryName(imagePath), Path.GetFileName(imagePath)); // 更新路径按钮

            // 显示图片
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath);
            bitmap.EndInit();
            RandomImage.Source = bitmap;

            // 计算适合窗口的缩放比例
            ScaleImageToFit(bitmap);
    
            if (!viewedImages.Contains(imagePath)) // 仅在缓存中不包含当前图片时添加
            {
                CacheImage(imagePath);
            }

            UpdateIndicator();
            if (IndicatorPanel.Visibility != Visibility.Visible)
            {
                IndicatorPanel.Visibility = Visibility.Visible;
            }
        }

        private void ScaleImageToFit(BitmapImage bitmap)
        {
            // 获取窗口的当前尺寸
            double windowWidth = this.ActualWidth;
            double windowHeight = this.ActualHeight;

            // 获取图片的原始尺寸
            double imageWidth = bitmap.Width;
            double imageHeight = bitmap.Height;

            // 计算宽高比例
            double widthScale = windowWidth / imageWidth;
            double heightScale = windowHeight / imageHeight;
            double scale = Math.Min(widthScale, heightScale); // 选择最小的缩放比例以保持纵横比

            // 设置 Image 控件的尺寸
            RandomImage.Width = imageWidth * scale; 
            RandomImage.Height = imageHeight * scale;
        }
        // 双击图片：使用系统默认方式打开图片
        private void RandomImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed) // 确保中键点击
            {
                OpenImage(currentImagePath); // 添加调用
                return;
            }
            if ((DateTime.Now - lastClickTime).TotalMilliseconds <= doubleClickInterval)
            {
                // 执行打开图片的逻辑
                OpenImage(currentImagePath);
            }
            lastClickTime = DateTime.Now;
        }

        private void OpenImage(string imagePath)
        {
            if (!string.IsNullOrEmpty(imagePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(imagePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("无法打开图片: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void CacheImage(string imagePath)
        {
            // 检查当前图片是否已经在缓存中
            if (viewedImages.Count == 0 || viewedImages.Last() != imagePath)
            {
                if (viewedImages.Count >= cacheSize)
                {
                    viewedImages.RemoveAt(0); // 移除最旧的缓存
                }
                viewedImages.Add(imagePath);
                viewedIndex = viewedImages.Count - 1; // 更新当前视图的索引
            }
        }

        private void UpdatePathButtons(string directory, string filename)
        {
            PathButtonPanel.Children.Clear(); // 清空现有按钮

            // 按路径分割为部分
            string[] pathParts = directory.Split(Path.DirectorySeparatorChar);
            string currentPath = "";

            for (int i = 0; i < pathParts.Length; i++)
            {
                var part = pathParts[i];
                currentPath = Path.Combine(currentPath, part); // 构建每一部分的完整路径
                Button pathButton = new Button
                {
                    Focusable = false,
                    Content = part,
                    Margin = new Thickness(2),
                    Padding = new Thickness(12),
                    Tag = currentPath, // 将当前路径作为按钮的 Tag 属性
                     
                };
                pathButton.MouseRightButtonDown += PathButton_MouseRightButtonDown;
                pathButton.Background = new SolidColorBrush(settings.GetButtonColorFromPresetID());
                pathButton.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
                // 设置悬停提示，使用当前语言设置
                pathButton.ToolTip = LanguageResources.GetText("PathButtonTooltip", settings.CurrentLanguage);

                // 检查当前路径是否与 fullPath 相同，以决定高亮
                if (string.Equals(currentPath, currentDirectory, StringComparison.InvariantCultureIgnoreCase))
                {
                    pathButton.Background = new SolidColorBrush(Color.FromRgb(0, 74, 230)); // 高亮背景色
                    pathButton.Foreground = new SolidColorBrush(Colors.White); // 设置文字颜色
                }

                pathButton.Click += PathButton_Click; // 绑定按钮点击事件
                PathButtonPanel.Children.Add(pathButton);
            }

            // 添加当前文件名按钮
            if (!string.IsNullOrEmpty(filename))
            {
                Button fileButton = new Button
                {
                    Focusable = false,
                    Content = filename,
                    Margin = new Thickness(2),
                    Padding = new Thickness(5),
                    Tag = currentImagePath // 将当前文件路径作为按钮的 Tag 属性
                };
                fileButton.Background = new SolidColorBrush(settings.GetButtonColorFromPresetID());
                fileButton.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
                
                // 设置悬停提示，使用当前语言设置
                fileButton.ToolTip = LanguageResources.GetText("FileButtonTooltip", settings.CurrentLanguage);

                // 检查文件按钮的 Tag 是否与 currentImagePath 相同，以决定高亮
                if (string.Equals(currentImagePath, currentDirectory, StringComparison.InvariantCultureIgnoreCase))
                {
                    fileButton.Background = new SolidColorBrush(Colors.LightBlue); // 高亮背景色
                    fileButton.Foreground = new SolidColorBrush(Colors.White); // 设置文字颜色
                    
                }

                fileButton.Click += FileButton_Click; // 绑定按钮点击事件
                PathButtonPanel.Children.Add(fileButton);
            }
        }

        private void PathButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                string selectedDirectory = button.Tag.ToString(); // 获取按钮的路径
                if (Directory.Exists(selectedDirectory))
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        OpenFolderInExplorer(selectedDirectory);
                        return;
                    }
                    LoadImagesFromDirectory(selectedDirectory); // 从该路径加载图像
                }
            }
        }
        private void OpenFolderInExplorer(string path)
        {
            try
            {
                Process.Start("explorer.exe", path); // 在新窗口中打开指定路径
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"打开路径时出错: {ex.Message}");
            }
        }
        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                string selectedFile = button.Tag.ToString(); // 获取文件的完整路径
                OpenImage(selectedFile);
                
            }
        }
        
        private void PathButton_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            Button clickedButton = (Button)sender; // 获取点击的按钮
            string buttonFilePath = clickedButton.Tag.ToString(); // 获取对应的文件路径

            // 获取同级目录
            var siblingDirectories = GetSiblingDirectories(buttonFilePath);

            // 如果没有同级目录，则不显示菜单
            if (siblingDirectories.Count == 0)
            {
                return; // 不显示菜单，直接返回
            }

            // 创建上下文菜单
            ContextMenu contextMenu = new ContextMenu();

            foreach (var directory in siblingDirectories)
            {
                MenuItem menuItem = new MenuItem
                {
                    Header = Path.GetFileName(directory)
                };
                menuItem.Click += (s, args) =>
                {
                    LoadImagesFromDirectory(directory);
                    contextMenu.IsOpen = false; // 关闭上下文菜单
                };

                contextMenu.Items.Add(menuItem);
            }

            // 显示上下文菜单
            contextMenu.IsOpen = true; // 仅在有菜单项时显示
        }

        private List<string> GetSiblingDirectories(string currentDir)
        {
            // 获取当前文件的目录以及它的父目录
            var parentDirectory = Directory.GetParent(currentDir)?.FullName; 

            // 如果 parentDirectory 为 null, 返回空列表
            if (parentDirectory == null)
            {
                return new List<string>(); // 返回一个空列表
            }

            // 获取同级目录，同时检查是否含有图片
            var siblings = Directory.GetDirectories(parentDirectory)
                .Where(dir => dir != currentDir && ContainsImages(dir)) // 排除当前目录并检查图片
                .ToList();

            return siblings;
        }
        // 检查目录下是否包含图片文件
        private bool ContainsImages(string directoryPath)
        {
            // 定义允许的图片扩展名
            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff" };
    
            try
            {
                // 获取目录中的所有文件，检查是否有图片
                return Directory.GetFiles(directoryPath)
                    .Any(file => imageExtensions.Contains(Path.GetExtension(file).ToLower()));
            }
            catch (UnauthorizedAccessException)
            {
                // 处理没有访问权限的异常，可以选择记录日志、返回 false 等
                return false; // 直接返回 false 因为我们无法访问这个目录
            }
            catch (DirectoryNotFoundException)
            {
                // 处理没有找到目录的异常
                return false; // 直接返回 false
            }
            catch (Exception ex)
            {
                // 处理其他未预料到的异常（可选）
                Console.WriteLine($"Error accessing directory {directoryPath}: {ex.Message}");
                return false; // 直接返回 false
            }
        }
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    ShowPreviousImage(); // ���方向键
                    break;
                case Key.Right:
                    ShowNextImage(); // 右方向键
                    break;
                case Key.Up:
                    showPreviousImageInOrder(); // 上方向键
                    break;
                case Key.Down:
                    showNextImageInOrder(); // 下方向键
                    break;
                case Key.Space:
                    LoadRandomImage(); // 空格键
                    break;
                case Key.Tab:
                    ToggleSettingsPanel(); // Tab 键
                    break;
            }
        }
        private bool IsMouseOverCurrentPathButton()
        {
            foreach (var child in PathButtonPanel.Children)
            {
                if (child is Button btn &&
                    btn.Tag?.ToString() == currentDirectory &&
                    btn.IsMouseOver)
                {
                    return true;
                }
            }
            return false;
        }
        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0) // 滚动向上，切换到上一张
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control || IsMouseOverCurrentPathButton() )
                {
                    showPreviousImageInOrder(); // 如果按下了 Ctrl 键，则按顺序切换到上一张
                    return;
                }
                ShowPreviousImage();
            }
            else if (e.Delta < 0) // 滚动向下，切换到下一张
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control || IsMouseOverCurrentPathButton() )
                {
                    showNextImageInOrder();// 如果按下了 Ctrl 键，则按顺序切换到下一张
                    return;
                }
                ShowNextImage();
            }
        }

        private void showNextImageInOrder()
        {
            if (!imageFiles.Any()) return;
            string currentPath = Path.GetDirectoryName(imageFiles[currentIndex]);
            string targetPath = Path.GetDirectoryName(imageFiles[currentIndex+1]);
            if (currentPath == targetPath)
            {
                currentIndex++;
            }
            DisplayImage(imageFiles[currentIndex]);
        }
        private void showPreviousImageInOrder()
        {
            if (!imageFiles.Any()) return;
            string currentPath = Path.GetDirectoryName(imageFiles[currentIndex]);
            string targetPath = Path.GetDirectoryName(imageFiles[currentIndex-1]);
            if (currentPath == targetPath)
            {
                currentIndex--;
            }
            DisplayImage(imageFiles[currentIndex]);
        }
        private void ShowPreviousImage()
        {
            if (!imageFiles.Any()) return;
            if (viewedIndex > 0)
            {
                viewedIndex--; // 移动到缓存中的上一张
                DisplayImage(viewedImages[viewedIndex]);
            }
        }

        private void ShowNextImage()
        {
            if (!imageFiles.Any()) return;
            if (viewedIndex < viewedImages.Count - 1)
            {
                viewedIndex++; // 移动到缓存中的下一张
                DisplayImage(viewedImages[viewedIndex]);
            }
            else
            {
                // 如果已经到达缓存末尾，则加载随机图片
                LoadRandomImage();
            }
        }
        private void ToggleSettingsPanel()
        {
            SettingsPanel.Visibility = 
                SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            CacheSizeSlider.Focusable = false;
        }
        private void SetBackgroundColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                int preset = int.Parse(button.Tag.ToString()); // 获取按钮的预设值
                settings.SetColorPreset(preset); // 设置颜色预设
                RestoreSettings();
                if (!imageFiles.Any()) return;
                UpdateIndicator();
                UpdatePathButtons(Path.GetDirectoryName(currentImagePath), Path.GetFileName(currentImagePath));
            }
        }
        
        
        
        private void InitializeIndicators()
        {
            
            dots = new Ellipse[settings.CacheSize];
            IndicatorPanel.Children.Clear();
            for (int i = 0; i < settings.CacheSize; i++)
            {
                var dot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Margin = new Thickness(5),
                    Fill = Brushes.Gray // 默认状态颜色
                };
                dots[i] = dot;
                IndicatorPanel.Children.Add(dot);
            }

            UpdateIndicator(); // 初始化时更新指示器亮起状态
        }
        private void UpdateIndicator()
        {
            for (int i = 0; i < dots.Length; i++)
            {
                // 亮起对应图片索引的指示点
                dots[i].Fill = (i == viewedIndex) ? new SolidColorBrush(settings.GetForegroundColorFromPresetID()) : new SolidColorBrush(settings.GetButtonColorFromPresetID());
            }
        }
        
        // 更新界面语言
        private void UpdateUILanguage(Language language)
        {
            // 更新窗口标题
            this.Title = LanguageResources.GetText("WindowTitle", language);
            
            // 更新提示文本
            HintTextBlock.Text = LanguageResources.GetText("HintText", language);
            
            // 更新设置面板文本
            SettingTittle.Text = LanguageResources.GetText("Settings", language);
            CacheSizeText.Text = LanguageResources.GetText("CacheSize", language)+ settings.CacheSize.ToString();
            BackgroundColor.Text = LanguageResources.GetText("BackgroundColor", language);
            LanguageLabel.Text = LanguageResources.GetText("Language", language);
            
            // 只有当已经加载了图片且currentImagePath不为空时，才更新路径按钮
            if (IsLoaded && !string.IsNullOrEmpty(currentImagePath))
            {
                UpdatePathButtons(Path.GetDirectoryName(currentImagePath), Path.GetFileName(currentImagePath)); // 更新路径按钮
            }
            
            // 更新恢复会话按钮的文本（如果存在）
            UpdateRestoreButtonText(language);
        }
        
        // 更新恢复会话按钮的文本
        private void UpdateRestoreButtonText(Language language)
        {
            // 遍历HintArea中的所有子控件
            foreach (var child in HintArea.Children)
            {
                // 如果是按钮，且绑定了RestoreButton_Click事件
                if (child is Button button)
                {
                    // 使用反射检查是否绑定了RestoreButton_Click
                    var clickEvent = button.GetType().GetEvent("Click");
                    if (clickEvent != null)
                    {
                        // 更新按钮文本
                        button.Content = LanguageResources.GetText("RestoreSession", language);
                    }
                }
            }
        }
        
        // 处理语言下拉列表的选择变化
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag.ToString(), out int languageIndex))
                {
                    // 更���语言设置
                    settings.SetLanguage(languageIndex);
                    
                    // 更新界面语言
                    UpdateUILanguage(settings.CurrentLanguage);
                }
            }
        }
    }
}
