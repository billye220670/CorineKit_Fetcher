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

        // 全覆盖随机遍历引擎：按轮发放，保证每张图片都被抽到且不重复
        private readonly RandomTraversalEngine traversal = new RandomTraversalEngine();

        //---------------------------画布（缩放 / 平移）状态-------------------------
        private const double PanEdgeMargin = 60;   // 平移时图片至少留在可视区的边距
        private CanvasViewState canvas = CanvasViewState.Fit;   // Scale = 1.0 即恰好适配窗口

        // 右键 / 中键手势：按下后先不判定，位移越过阈值才算 drag，否则算 click
        private MouseButton? pendingGestureButton;
        private Button? pendingGesturePathButton;     // 中键按下时命中的面包屑按钮（可能为 null）
        private Point gestureStartPos;
        private Point lastPanPos;
        private bool isPanning;

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

            // 窗口尺寸变化后重新收敛画布平移边界，避免图片被留在可视区外找不回来
            RootGrid.SizeChanged += (s, e) => ReclampCanvas();
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
            // 右键 / 中键正在做画布手势时，不要触发文件拖放
            if (pendingGestureButton.HasValue || isPanning) return;

            if (e.LeftButton == MouseButtonState.Pressed && !string.IsNullOrEmpty(currentImagePath))
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
            // 已经加载出图片，提示文字和"恢复上次会话"按钮都该退场。
            // HintArea 在 XAML 中位于 Image 之下，只隐藏文字的话，
            // 平移画布把图片挪开后按钮会从底下露出来，而且仍然可以被点到。
            // 折叠整个容器才能同时挡住渲染和命中测试。
            HintTextBlock.Visibility = Visibility.Collapsed;
            HintArea.Visibility = Visibility.Collapsed;
        }

        private void LoadImagesFromDirectory(string directory)
        {

            currentDirectory = directory; // 更新当前目录
            settings.LastPath = directory; //更新设置中的LastPath
            settings.Save();
            imageFiles = GetImageFiles(directory);
            if (imageFiles.Any())
            {
                traversal.Rebuild(imageFiles); // 重建遍历牌堆，必须在 LoadRandomImage 之前
                UpdateHintVisibility(); // 更新提示信息的可见性
                UpdatePathButtons(directory, null); //根据当前目录更新路径按钮，并且不显示文件名
                LoadRandomImage(); // 初始化显示随机图片
                BuildDirectoryIndex(); // 必须在LoadRandomImage之后调用，以便currentIndex已被设置
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

            // 由遍历引擎发牌：一轮之内不重复、不遗漏，且相邻两次保持足够跳跃距离；
            // 子目录按"饥饿度"轮转，深层小目录不会被大目录长期盖住。
            int next = traversal.Next();
            if (next < 0 || next >= imageFiles.Count)
            {
                next = new Random().Next(imageFiles.Count); // 兜底：引擎未就绪时退回纯随机
            }

            currentIndex = next;
            DisplayImage(imageFiles[next]);
        }

        private void DisplayImage(string imagePath)
        {
            currentImagePath = imagePath; // 更新当前图片路径
            UpdatePathButtons(Path.GetDirectoryName(imagePath), Path.GetFileName(imagePath)); // 更新路径按钮

            // 更新currentIndex以匹配当前显示的图片
            int newIndex = imageFiles.IndexOf(imagePath);
            if (newIndex >= 0)
            {
                currentIndex = newIndex;

                // 更新当前目录的起始和结束索引
                string currentDir = Path.GetDirectoryName(imagePath);
                if (directoryImageIndices.TryGetValue(currentDir, out var indices))
                {
                    currentDirStartIndex = indices.First();
                    currentDirEndIndex = indices.Last();
                }
            }

            // 显示图片
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath);
            bitmap.EndInit();
            RandomImage.Source = bitmap;

            // 计算适合窗口的缩放比例
            ScaleImageToFit(bitmap);

            // 换图后画布复位：上一张的放大/平移状态不应带到下一张
            ResetCanvasTransform();

            if (!viewedImages.Contains(imagePath)) // 仅在缓存中不包含当前图片时添加
            {
                CacheImage(imagePath);
            }

            UpdateIndicator();
            // 圆点指示器常驻：加载出第一张图后就不再隐藏
            if (IndicatorPanel.Visibility != Visibility.Visible)
            {
                IndicatorPanel.Visibility = Visibility.Visible;
            }
        }

        // 参数用 BitmapSource（BitmapImage 的基类），这样 F 键重新适配时
        // 可以直接把 RandomImage.Source 传进来
        private void ScaleImageToFit(BitmapSource bitmap)
        {
            // 用客户区（RootGrid）尺寸而非窗口外框尺寸：外框含标题栏和边框，
            // 拿它算 fit 会让图片略微溢出，同时导致画布缩放锚点的居中原点算偏。
            double viewWidth = RootGrid.ActualWidth;
            double viewHeight = RootGrid.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0)
            {
                // 布局尚未完成时退回窗口尺寸
                viewWidth = this.ActualWidth;
                viewHeight = this.ActualHeight;
            }

            // 获取图片的原始尺寸
            double imageWidth = bitmap.Width;
            double imageHeight = bitmap.Height;
            if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return;

            // 计算宽高比例
            double widthScale = viewWidth / imageWidth;
            double heightScale = viewHeight / imageHeight;
            double scale = Math.Min(widthScale, heightScale); // 选择最小的缩放比例以保持纵横比

            // 设置 Image 控件的尺寸
            RandomImage.Width = imageWidth * scale; 
            RandomImage.Height = imageHeight * scale;
        }
        // 双击图片：使用系统默认方式打开图片
        // 中键与右键已交给 Window 层的画布手势统一处理，这里只认左键
        private void RandomImage_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

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

            string normalizedCurrent = PathBreadcrumb.NormalizeForCompare(currentDirectory);

            foreach (PathSegment segment in PathBreadcrumb.Split(directory))
            {
                bool isCurrentDirButton = string.Equals(
                    PathBreadcrumb.NormalizeForCompare(segment.FullPath), normalizedCurrent,
                    StringComparison.InvariantCultureIgnoreCase);

                Button pathButton = new Button
                {
                    Focusable = false,
                    Content = segment.Label,
                    Margin = new Thickness(2),
                    Padding = new Thickness(12),
                    Tag = segment.FullPath, // 将当前路径作为按钮的 Tag 属性
                };

                pathButton.Background = new SolidColorBrush(isCurrentDirButton ? Color.FromRgb(0, 74, 230) : settings.GetButtonColorFromPresetID());
                pathButton.Foreground = new SolidColorBrush(isCurrentDirButton ? Colors.White : settings.GetForegroundColorFromPresetID());

                if (!segment.Clickable)
                {
                    // 盘符（或 UNC 主机名）纯展示：关掉命中测试后既不响应点击，
                    // 也不会弹同级目录菜单；降低不透明度让用户一眼看出它点不动。
                    pathButton.IsHitTestVisible = false;
                    pathButton.Cursor = Cursors.Arrow;
                    pathButton.Opacity = 0.6;
                    PathButtonPanel.Children.Add(pathButton);
                    continue;
                }

                pathButton.MouseRightButtonDown += PathButton_MouseRightButtonDown;
                pathButton.ToolTip = LanguageResources.GetText("PathButtonTooltip", settings.CurrentLanguage);
                pathButton.Click += PathButton_Click;
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
                fileButton.ToolTip = LanguageResources.GetText("FileButtonTooltip", settings.CurrentLanguage);
                fileButton.Click += FileButton_Click;
                PathButtonPanel.Children.Add(fileButton);
            }
        }

        private void PathButton_Click(object sender, RoutedEventArgs e)
        {
            // 中键打开资源管理器已移到 Window 层统一处理（见 HandleMiddleClick），
            // 这里只负责左键切换目录。
            if (sender is Button button && button.Tag is string selectedDirectory
                && !string.IsNullOrEmpty(selectedDirectory) && Directory.Exists(selectedDirectory))
            {
                LoadImagesFromDirectory(selectedDirectory); // 从该路径加载图像
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
            if (sender is Button button && button.Tag is string selectedFile)
            {
                OpenImage(selectedFile); // 获取文件的完整路径
            }
        }
        
        private void PathButton_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Button clickedButton)) return;

            string? buttonFilePath = clickedButton.Tag as string; // 获取对应的文件路径
            if (string.IsNullOrEmpty(buttonFilePath)) return;

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
                case Key.F:
                    RefitCanvas(); // F 键：重新适配窗口并复位画布
                    break;
                case Key.Tab:
                    ToggleSettingsPanel(); // Tab 键
                    break;
            }
        }

        //---------------------------画布：缩放与平移-------------------------
        // 缩放基准是 ScaleImageToFit 算出的"适配窗口"尺寸，canvas.Scale = 1.0 即适配状态。
        // 全部数学运算都在 CanvasViewState 里，这里只负责取布局信息和把结果写回 RenderTransform。

        private void ApplyCanvasTransform()
        {
            CanvasScaleTransform.ScaleX = canvas.Scale;
            CanvasScaleTransform.ScaleY = canvas.Scale;
            CanvasTranslateTransform.X = canvas.OffsetX;
            CanvasTranslateTransform.Y = canvas.OffsetY;
        }

        private void ResetCanvasTransform()
        {
            canvas = CanvasViewState.Fit;
            ApplyCanvasTransform();
        }

        /// <summary>
        /// 重新适配窗口并复位画布（F 键）。
        /// 只复位 transform 是不够的：图片只在换图时才做适配，窗口被拉伸过之后
        /// scale=1 已经不再等于"贴合窗口"，所以要按当前尺寸重算一次。
        /// </summary>
        private void RefitCanvas()
        {
            if (RandomImage.Source is BitmapSource source)
            {
                ScaleImageToFit(source);
            }
            ResetCanvasTransform();
        }

        /// <summary>
        /// 取图片布局尺寸（不受 RenderTransform 影响），用于换算缩放锚点与平移边界。
        /// </summary>
        private Size GetImageLayoutSize()
        {
            double w = RandomImage.ActualWidth;
            double h = RandomImage.ActualHeight;
            if (double.IsNaN(w) || w <= 0) w = RandomImage.Width;
            if (double.IsNaN(h) || h <= 0) h = RandomImage.Height;
            if (double.IsNaN(w) || w < 0) w = 0;
            if (double.IsNaN(h) || h < 0) h = 0;
            return new Size(w, h);
        }

        /// <summary>
        /// 汇总画布运算需要的布局信息。Image 设了明确宽高后在 Grid 内居中，
        /// 所以布局原点就是两侧留白的一半。
        /// </summary>
        private CanvasViewState.Layout GetCanvasLayout()
        {
            Size size = GetImageLayoutSize();
            return new CanvasViewState.Layout
            {
                ImageWidth = size.Width,
                ImageHeight = size.Height,
                OriginX = Math.Max(0, (RootGrid.ActualWidth - size.Width) / 2),
                OriginY = Math.Max(0, (RootGrid.ActualHeight - size.Height) / 2),
                ViewWidth = RootGrid.ActualWidth,
                ViewHeight = RootGrid.ActualHeight
            };
        }

        /// <summary>
        /// 以鼠标指针为锚点缩放，并立刻把平移量收敛回合法范围。
        /// </summary>
        private void ZoomCanvasAt(Point mouseInGrid, double factor)
        {
            CanvasViewState.Layout layout = GetCanvasLayout();
            CanvasViewState next = canvas
                .ZoomAt(layout, mouseInGrid.X, mouseInGrid.Y, factor)
                .Clamp(layout, PanEdgeMargin);

            if (next.Equals(canvas)) return;
            canvas = next;
            ApplyCanvasTransform();
        }

        private void PanCanvasTo(Point mouseInGrid)
        {
            CanvasViewState.Layout layout = GetCanvasLayout();
            canvas = canvas
                .PanBy(mouseInGrid.X - lastPanPos.X, mouseInGrid.Y - lastPanPos.Y)
                .Clamp(layout, PanEdgeMargin);
            lastPanPos = mouseInGrid;
            ApplyCanvasTransform();
        }

        /// <summary>
        /// 窗口尺寸变化后重新收敛平移边界，避免图片被留在可视区外。
        /// </summary>
        private void ReclampCanvas()
        {
            CanvasViewState next = canvas.Clamp(GetCanvasLayout(), PanEdgeMargin);
            if (next.Equals(canvas)) return;
            canvas = next;
            ApplyCanvasTransform();
        }

        //---------------------------右键 / 中键手势-------------------------
        // 右键：按住 + 滚轮 = 缩放，按住 + 拖拽 = 平移，原地单击 = 画布复位
        // 中键：按住 + 拖拽 = 平移，原地单击 = 沿用旧逻辑（面包屑开资源管理器 / 图片开默认程序）

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            Point mouseInGrid = e.GetPosition(RootGrid);

            // 缩放的唯一入口：右键按住 + 滚轮。
            // 不能只看 e.RightButton 是否按下：右键点在面包屑上会弹同级目录菜单，
            // 那种情况下滚轮不该变成缩放。
            if (pendingGestureButton == MouseButton.Right)
            {
                ZoomCanvasAt(mouseInGrid, CanvasViewState.WheelToFactor(e.Delta));
                e.Handled = true;
                return;
            }

            // 正在平移时不要把滚轮解释成切图
            if (isPanning)
            {
                e.Handled = true;
                return;
            }

            // 其余情况滚轮只切图，不缩放：窗口纵向对半分，
            // 上半 = 顺序切图（↑/↓ 那套），下半 = 缓存切图（←/→ 那套）
            if (RandomImage.Source == null || RootGrid.ActualHeight <= 0)
            {
                e.Handled = true;
                return;
            }

            if (mouseInGrid.Y < RootGrid.ActualHeight / 2)
            {
                if (e.Delta > 0) showPreviousImageInOrder();
                else if (e.Delta < 0) showNextImageInOrder();
            }
            else
            {
                if (e.Delta > 0) ShowPreviousImage();
                else if (e.Delta < 0) ShowNextImage();
            }
            e.Handled = true;
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (e.ChangedButton != MouseButton.Right && e.ChangedButton != MouseButton.Middle) return;
            if (!CanStartCanvasGesture(e.OriginalSource as DependencyObject, e.ChangedButton)) return;

            pendingGestureButton = e.ChangedButton;
            // 捕获鼠标后 OriginalSource 不再可靠，面包屑命中结果必须在按下时就记下来
            pendingGesturePathButton = e.ChangedButton == MouseButton.Middle
                ? FindPathButton(e.OriginalSource as DependencyObject)
                : null;

            gestureStartPos = e.GetPosition(RootGrid);
            lastPanPos = gestureStartPos;
            isPanning = false;

            CaptureMouse();
            e.Handled = true;
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!pendingGestureButton.HasValue) return;

            Point position = e.GetPosition(RootGrid);

            if (!isPanning)
            {
                // 阈值内视为 click 的抖动，不启动平移，中键才能保持"点击"语义
                double threshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance,
                                            SystemParameters.MinimumVerticalDragDistance);
                if (Math.Abs(position.X - gestureStartPos.X) < threshold &&
                    Math.Abs(position.Y - gestureStartPos.Y) < threshold)
                {
                    return;
                }

                isPanning = true;
                lastPanPos = gestureStartPos;
                Cursor = Cursors.SizeAll;
            }

            PanCanvasTo(position);
            e.Handled = true;
        }

        private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!pendingGestureButton.HasValue || e.ChangedButton != pendingGestureButton.Value) return;

            MouseButton button = pendingGestureButton.Value;
            Button? pathButton = pendingGesturePathButton;
            bool wasDrag = isPanning;
            EndCanvasGesture();

            if (wasDrag)
            {
                // 已经平移过，抬手不算点击
                e.Handled = true;
                return;
            }

            if (button == MouseButton.Middle)
            {
                HandleMiddleClick(pathButton);
                e.Handled = true;
            }
            else if (button == MouseButton.Right)
            {
                // 右键原地单击：与 F 键一致，重新适配窗口并复位画布
                RefitCanvas();
                e.Handled = true;
            }
        }

        private void Window_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (pendingGestureButton.HasValue) EndCanvasGesture();
        }

        /// <summary>
        /// 中键单击：命中面包屑就在资源管理器里打开该目录，否则沿用打开当前图片的旧逻辑。
        /// </summary>
        private void HandleMiddleClick(Button? pathButton)
        {
            string? target = pathButton?.Tag as string;
            if (!string.IsNullOrEmpty(target))
            {
                if (Directory.Exists(target))
                {
                    OpenFolderInExplorer(target);
                    return;
                }
                if (File.Exists(target))
                {
                    OpenImage(target);
                    return;
                }
            }
            OpenImage(currentImagePath);
        }

        /// <summary>
        /// 判断这次按下是否该被画布手势接管。
        /// 设置面板和提示区的控件要保持原生交互；右键还要留给面包屑的同级目录菜单。
        /// </summary>
        private bool CanStartCanvasGesture(DependencyObject? source, MouseButton button)
        {
            if (source == null) return true;
            if (IsDescendantOf(source, SettingsPanel)) return false;
            if (IsDescendantOf(source, HintArea)) return false;
            if (button == MouseButton.Right && IsDescendantOf(source, PathButtonPanel)) return false;
            return true;
        }

        private Button? FindPathButton(DependencyObject? source)
        {
            DependencyObject? node = source;
            int guard = 0;
            while (node != null && guard++ < 256)
            {
                if (node is Button button && PathButtonPanel.Children.Contains(button)) return button;
                node = GetVisualOrLogicalParent(node);
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
        {
            if (ancestor == null) return false;

            DependencyObject? node = child;
            int guard = 0;
            while (node != null && guard++ < 256)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = GetVisualOrLogicalParent(node);
            }
            return false;
        }

        private static DependencyObject? GetVisualOrLogicalParent(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(node);
            }
            return LogicalTreeHelper.GetParent(node);
        }

        private void EndCanvasGesture()
        {
            pendingGestureButton = null;
            pendingGesturePathButton = null;
            isPanning = false;
            Cursor = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private Dictionary<string, List<int>> directoryImageIndices = new Dictionary<string, List<int>>();
        private int currentDirStartIndex;
        private int currentDirEndIndex;

        private void BuildDirectoryIndex()
        {
            directoryImageIndices.Clear();
            for (int i = 0; i < imageFiles.Count; i++)
            {
                string dir = Path.GetDirectoryName(imageFiles[i]);
                if (!directoryImageIndices.ContainsKey(dir))
                    directoryImageIndices[dir] = new List<int>();
                directoryImageIndices[dir].Add(i);
            }

            if (currentIndex >= 0 && currentIndex < imageFiles.Count)
            {
                string currentDir = Path.GetDirectoryName(imageFiles[currentIndex]);
                if (directoryImageIndices.TryGetValue(currentDir, out var indices))
                {
                    currentDirStartIndex = indices.First();
                    currentDirEndIndex = indices.Last();
                }
            }
        }

        private void showNextImageInOrder()
        {
            if (!imageFiles.Any() || currentIndex < 0) return;

            if (currentIndex < currentDirEndIndex)
            {
                currentIndex++;
            }
            else if (currentIndex == currentDirEndIndex)
            {
                currentIndex = currentDirStartIndex;
            }
            DisplayImage(imageFiles[currentIndex]);
        }
        private void showPreviousImageInOrder()
        {
            if (!imageFiles.Any() || currentIndex < 0) return;

            if (currentIndex > currentDirStartIndex)
            {
                currentIndex--;
            }
            else if (currentIndex == currentDirStartIndex)
            {
                currentIndex = currentDirEndIndex;
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
