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

        //---------------------------媒体播放（GIF / 视频）-------------------------
        private readonly GifPlayer gifPlayer = new GifPlayer();
        private readonly VideoSourceResolver videoResolver = new VideoSourceResolver();

        private MediaKind currentMediaKind = MediaKind.None;

        // 当前媒体的固有尺寸，F 键重新适配时用它重算（视频/图片/GIF 都记在这里）
        private Size currentMediaNaturalSize;

        // 连续失败保护：整个目录都解不出来时不要闷头刷牌刷屏
        private const int MaxConsecutiveMediaFailures = 30;
        private int consecutiveMediaFailures;

        // 缺解码器之类的提示一个会话只弹一次，否则随机浏览会被对话框连环打断
        private bool webpHintShown;
        private bool videoCodecHintShown;

        private string? displayError;
        private bool isVideoPaused;

        //---------------------------左侧 fav 分栏-------------------------
        private const double FavSplitterWidth = 8;
        private const double MinPaneWidth = 300;
        private const double MinFavRatio = 0.25;
        private const double MaxFavRatio = 0.75;
        private double favSplitRatio = 0.5;
        private bool isFavPanelOpen;
        private bool isDraggingFavSplitter;
        private bool isRightDraggingOut;

        //---------------------------右侧画布动作录制/回放-------------------------
        private const double MotionClickSlop = 8.0;
        // 左键先按时，右键必须在这段时间内跟上才算同一组双键手势，
        // 避免"左键按住半天后再按右键"被误判成停止/录制。
        private const double MotionPairWindowMs = 400.0;
        private readonly CanvasMotionPlayer rightMotion = new CanvasMotionPlayer();
        private readonly Stopwatch motionClock = Stopwatch.StartNew();
        private TimeSpan lastMotionFrame;
        private bool isRightMotionCandidate;
        private bool isRightMotionRecording;
        private bool isRightPanEnded;
        private Point rightMotionCandidateStart;
        private TimeSpan rightMotionStartTime;
        private Point rightMotionLastPoint;
        private double rightMotionAccumX;
        private double rightMotionAccumY;

        // 左键先按：记录起点与时刻，右键在窗口内跟上才升级为双键手势；
        // 同时给文件拖出加位移阈值，避免点击时的抖动直接开始拖拽。
        private bool hasLeftDownForMotion;
        private Point leftDownForMotion;
        private TimeSpan leftDownForMotionTime;

        private bool IsAnyMotionCapturing =>
            isRightMotionCandidate || isRightMotionRecording || FavView.IsMotionCapturing;

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

            // 滚轮随机区：必须在 loaded 置位之前设，SelectionChanged 里才不会回写一次设置
            WheelZoneComboBox.SelectedIndex = settings.WheelRandomZone == 1 ? 1 : 0;
            
            // 窗口关闭时保存设置，并释放媒体资源（GIF 的流、webm 的 .mkv 替身）。
            // TeardownMedia 这一步不能省，也不能挪到后面：MediaElement 是
            // UnloadedBehavior="Manual"，WPF 不会在卸载时替我们关媒体会话，而替身是
            // 硬链接、正被 Media Foundation 占着句柄——此时直接 File.Delete 会把 UI 线程
            // 阻塞到句柄释放为止（实测 3MB 的 webm 卡 4182ms，先 Stop+Close 再删只要 11ms），
            // 表现就是关窗后"未响应"。TeardownMedia → StopVideo 的顺序已经是对的。
            Closing += (s, e) => {
                settings.Save();
                CompositionTarget.Rendering -= OnMotionFrame;
                rightMotion.Reset();
                FavView.Shutdown();
                TeardownMedia();
                gifPlayer.Dispose();
                videoResolver.Dispose();
            };

            // GIF 逐帧推进：帧送到 RandomImage 上，尺寸在 Open 之后一次性适配
            gifPlayer.FrameReady += GifPlayer_FrameReady;
            gifPlayer.Failed += GifPlayer_Failed;

            // 视口尺寸变化（窗口缩放、Space 开合、拖分隔条）时实时重算 fit 基准，
            // 再收敛平移边界；没手动放大时始终贴合视口。
            RootGrid.SizeChanged += (s, e) => HandleFetcherViewportSizeChanged();
            WindowRoot.SizeChanged += (s, e) =>
            {
                if (isFavPanelOpen) ApplyFavSplitRatio();
            };

            FavSplitter.MouseEnter += (s, e) => SetFavSplitterHover(true);
            FavSplitter.MouseLeave += (s, e) => SetFavSplitterHover(false);

            // 失焦时鼠标抬起可能收不到；把两边的 gesture / capture / 光标一起收干净，
            // 否则会留下"移动光标粘手、失焦后 hover 还会拖"的状态。
            Deactivated += (s, e) =>
            {
                CancelCanvasGesture();
                CancelRightMotionGesture();
                FavView.CancelGesture();
                CancelFavSplitterDrag();
            };

            lastMotionFrame = motionClock.Elapsed;
            CompositionTarget.Rendering += OnMotionFrame;
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
            WheelZoneLabel.Foreground = new SolidColorBrush(settings.GetForegroundColorFromPresetID());
            this.Background = new SolidColorBrush(settings.GetBackgroundColorFromPresetID()); //恢复上次会话的背景色

            // 左侧 fav 面板跟随同一套主题；空列表时露出的就是这块背景。
            FavView.ApplyTheme(settings.GetBackgroundColorFromPresetID());
            Color foreground = settings.GetForegroundColorFromPresetID();
            FavSplitterLine.Background = new SolidColorBrush(Color.FromArgb(48, foreground.R, foreground.G, foreground.B));
        }

        private void SetFavSplitterHover(bool hover)
        {
            if (isDraggingFavSplitter) return;

            Color foreground = settings.GetForegroundColorFromPresetID();
            FavSplitterLine.Background = new SolidColorBrush(
                Color.FromArgb((byte)(hover ? 130 : 48), foreground.R, foreground.G, foreground.B));
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
            // 右键 / 中键正在做画布手势时，不要触发文件拖放；
            // 右键按住时左键也可能是录制的第二根手指。
            if (Mouse.RightButton == MouseButtonState.Pressed) return;
            if (IsAnyMotionCapturing) return;
            if (pendingGestureButton.HasValue || isPanning) return;

            if (e.LeftButton == MouseButtonState.Pressed && !string.IsNullOrEmpty(currentImagePath))
            {
                // 左键先按可能是在等右键组成双键手势；只有真的移动过阈值才拖出文件。
                if (!hasLeftDownForMotion) return;
                Point position = e.GetPosition(RootGrid);
                double threshold = MotionGestureThreshold();
                if (Math.Abs(position.X - leftDownForMotion.X) < threshold &&
                    Math.Abs(position.Y - leftDownForMotion.Y) < threshold)
                {
                    return;
                }

                // 开始拖动操作。拖拽期间置位，Drop 里据此区分“右→左收藏”和外部拖入。
                hasLeftDownForMotion = false;
                isRightDraggingOut = true;
                try
                {
                    DataObject data = new DataObject(DataFormats.FileDrop, new[] { currentImagePath });
                    DragDrop.DoDragDrop(this, data, DragDropEffects.Copy); // 拖动效果为复制
                }
                finally
                {
                    isRightDraggingOut = false;
                }
            }
        }
        //---------------------------拖放图片进入APP-------------------------
        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (IsAnyMotionCapturing) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] filePaths || filePaths.Length == 0) return;

            Point favPoint = e.GetPosition(FavView);
            bool overFav = isFavPanelOpen
                && FavView.Visibility == Visibility.Visible
                && favPoint.X >= 0 && favPoint.Y >= 0
                && favPoint.X <= FavView.ActualWidth && favPoint.Y <= FavView.ActualHeight;

            Point rightPoint = e.GetPosition(RootGrid);
            bool overRight = rightPoint.X >= 0 && rightPoint.Y >= 0
                && rightPoint.X <= RootGrid.ActualWidth && rightPoint.Y <= RootGrid.ActualHeight;

            // 左侧 fav → 右侧：右侧切到该图所在目录继续浏览，但不清缓存。
            if (FavView.IsDraggingOut)
            {
                if (overRight)
                {
                    foreach (string path in filePaths)
                    {
                        if (File.Exists(path) && MediaFormats.IsSupported(path))
                        {
                            ShowFavoriteOnRight(path);
                            break;
                        }
                    }
                }
                e.Handled = true;
                return;
            }

            // 右侧当前图 → 左侧：加入 fav；同侧落下不处理。
            if (isRightDraggingOut)
            {
                if (overFav)
                {
                    foreach (string path in filePaths)
                    {
                        FavView.AddFavorite(path);
                    }
                }
                e.Handled = true;
                return;
            }

            // 外部拖入只在右侧生效：文件夹仍按原逻辑加载。左侧不接收外部导入。
            if (!overRight)
            {
                e.Handled = true;
                return;
            }

            foreach (string filePath in filePaths)
            {
                if (Directory.Exists(filePath))
                {
                    LoadImagesFromDirectory(filePath); // 从新目录加载图像
                }
            }
            e.Handled = true;
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
            imageFiles = GetMediaFiles(directory);
            if (imageFiles.Any())
            {
                traversal.Rebuild(imageFiles); // 重建目录调度状态，必须在 LoadRandomImage 之前
                UpdateHintVisibility(); // 更新提示信息的可见性
                UpdatePathButtons(directory, null); //根据当前目录更新路径按钮，并且不显示文件名
                LoadRandomImage(); // 初始化显示随机图片
                BuildDirectoryIndex(); // 必须在LoadRandomImage之后调用，以便currentIndex已被设置
            }
            else
            {
                MessageBox.Show(LanguageResources.GetText("NoMediaFound", settings.CurrentLanguage),
                                LanguageResources.GetText("Notice", settings.CurrentLanguage),
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 左侧 fav 拖回右侧：把右侧浏览根切到该图所在目录并显示它。
        /// 不清 viewedImages，旧缓存仍可 ←/→ 回滚；但不改写 LastPath，
        /// 下次启动仍恢复用户上次主动加载的文件夹。
        /// </summary>
        private void ShowFavoriteOnRight(string mediaPath)
        {
            if (string.IsNullOrEmpty(mediaPath)) return;
            if (!File.Exists(mediaPath) || !MediaFormats.IsSupported(mediaPath)) return;

            string? directory = Path.GetDirectoryName(mediaPath);
            if (string.IsNullOrEmpty(directory)) return;

            currentDirectory = directory;
            imageFiles = GetMediaFiles(directory);
            if (imageFiles.Count == 0) imageFiles.Add(mediaPath);

            int index = imageFiles.FindIndex(
                item => string.Equals(item, mediaPath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                // 目录扫描没覆盖到（权限/竞态）时至少把这一张放进当前列表。
                imageFiles.Insert(0, mediaPath);
                index = 0;
            }

            currentIndex = index;
            traversal.Rebuild(imageFiles);
            BuildDirectoryIndex();
            UpdateCurrentIndex(mediaPath);
            UpdateHintVisibility();
            DisplayMedia(mediaPath);
        }

        /// <summary>
        /// 递归收集目录下所有受支持的图片和视频。扩展名清单只在 <see cref="MediaFormats"/> 里维护一份。
        /// </summary>
        private List<string> GetMediaFiles(string directory)
        {
            List<string> files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                                        .Where(MediaFormats.IsSupported));
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

            // 由遍历引擎发牌：以叶子目录为单位做软避让，刚出过图的目录权重降低、随后平滑恢复；
            // 目录内部按洗牌顺序轮换图片，不会连续回到同一目录。
            int next = traversal.Next();
            if (next < 0 || next >= imageFiles.Count)
            {
                next = new Random().Next(imageFiles.Count); // 兜底：引擎未就绪时退回纯随机
            }

            currentIndex = next;
            DisplayMedia(imageFiles[next]);
        }

        //---------------------------媒体分发-------------------------
        // 图片 / GIF / 视频共用同一套导航、缓存和画布变换，区别只在"画面从哪来"：
        //   图片 —— BitmapImage 一次性解码（OnLoad，解完立刻关流）
        //   GIF  —— GifPlayer 逐帧推进，帧写进同一个 RandomImage
        //   视频 —— MediaElement；webm 要先经 VideoSourceResolver 换成 .mkv 替身
        // Image 层和 MediaElement 层始终只有一层可见，RandomImage 同时承担图片和 GIF。

        private void DisplayMedia(string mediaPath)
        {
            currentImagePath = mediaPath; // 更新当前媒体路径
            UpdatePathButtons(Path.GetDirectoryName(mediaPath), Path.GetFileName(mediaPath)); // 更新路径按钮
            UpdateCurrentIndex(mediaPath);

            displayError = null;
            MediaKind kind = MediaFormats.Classify(mediaPath);
            bool started = kind switch
            {
                MediaKind.Image => DisplayStillImage(mediaPath),
                MediaKind.Gif => DisplayGif(mediaPath),
                MediaKind.Video => DisplayVideo(mediaPath),
                _ => false
            };

            if (!started)
            {
                ReportMediaFailure(mediaPath, displayError ?? "不支持的文件类型", kind);
                return;
            }

            consecutiveMediaFailures = 0;

            // 换媒体后画布复位：上一张的放大/平移状态不应带到下一张
            ResetCanvasTransform();

            if (!viewedImages.Contains(mediaPath)) // 仅在缓存中不包含当前媒体时添加
            {
                CacheImage(mediaPath);
            }
            else
            {
                // 从面包屑 / 文件按钮 / 左侧 fav 跳回缓存里已有的图时，
                // 当前位置也要跟着对齐，否则圆点和 ←/→ 还停在上一张。
                viewedIndex = viewedImages.IndexOf(mediaPath);
            }

            UpdateIndicator();
            // 圆点指示器常驻：加载出第一张图后就不再隐藏
            if (IndicatorPanel.Visibility != Visibility.Visible)
            {
                IndicatorPanel.Visibility = Visibility.Visible;
            }
        }

        /// <summary>把 currentIndex 以及"当前子目录的索引区间"对齐到指定文件。</summary>
        private void UpdateCurrentIndex(string mediaPath)
        {
            int newIndex = imageFiles.FindIndex(
                item => string.Equals(item, mediaPath, StringComparison.OrdinalIgnoreCase));
            if (newIndex < 0) return;

            currentIndex = newIndex;

            string? currentDir = Path.GetDirectoryName(mediaPath);
            if (currentDir != null && directoryImageIndices.TryGetValue(currentDir, out var indices))
            {
                currentDirStartIndex = indices.First();
                currentDirEndIndex = indices.Last();
            }
        }

        private bool DisplayStillImage(string path)
        {
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                // OnLoad：EndInit 内就把整张图解码进内存，之后不再需要文件句柄。
                // 之前用默认的 OnDemand，BitmapImage 会一直把文件锁到 GC 收走为止
                // （实测：引用还活着时既不可写也不可改名），用户想在资源管理器里
                // 挪走当前这张图就会被拒绝。OnLoad + Freeze 实测两者都放行。
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze(); // 彻底断开与流的联系，顺带允许跨线程复用

                // 解码成功之后才拆旧的，失败时至少还能留着上一张
                TeardownMedia();
                currentMediaKind = MediaKind.Image;
                RandomImage.Visibility = Visibility.Visible;
                RandomImage.Source = bitmap;

                currentMediaNaturalSize = new Size(bitmap.Width, bitmap.Height);
                FitMediaToView(bitmap.Width, bitmap.Height);
                return true;
            }
            catch (Exception ex)
            {
                displayError = ex.Message;
                return false;
            }
        }

        private bool DisplayGif(string path)
        {
            TeardownMedia();

            // 先置位再 Open：Open 内部会同步送出首帧，FrameReady 靠这个标记判断该不该接收
            currentMediaKind = MediaKind.Gif;
            RandomImage.Visibility = Visibility.Visible;

            if (!gifPlayer.Open(path))
            {
                currentMediaKind = MediaKind.None;
                displayError = gifPlayer.LastError ?? "GIF 解码失败";
                return false;
            }

            // 尺寸只在开图时按 GIF 画布适配一次：每帧画布尺寸都一样，
            // 逐帧重设 Width/Height 只会白白触发一轮布局。
            currentMediaNaturalSize = new Size(gifPlayer.PixelWidth, gifPlayer.PixelHeight);
            FitMediaToView(gifPlayer.PixelWidth, gifPlayer.PixelHeight);
            return true;
        }

        private void GifPlayer_FrameReady(BitmapSource frame)
        {
            // 拆媒体时 currentMediaKind 已经不是 Gif，此时必须忽略：
            // OnDemand 的帧随流关闭一起失效，挂回可视树会在下次渲染时抛异常。
            if (currentMediaKind != MediaKind.Gif) return;
            RandomImage.Source = frame;
        }

        private void GifPlayer_Failed(string reason)
        {
            // 播放中途出错（文件被截断、磁盘掉线等）。已经切走的话就不要误伤当前媒体。
            if (currentMediaKind != MediaKind.Gif) return;
            ReportMediaFailure(currentImagePath, reason, MediaKind.Gif);
        }

        private bool DisplayVideo(string path)
        {
            TeardownMedia();

            // webm 要先换成 .mkv 替身，Media Foundation 的老管线才认（原因见 VideoSourceResolver）。
            // 这一步必须排在 TeardownMedia 之后：Resolve 和 StopVideo 都会释放替身，
            // 顺序反了会把刚建好的替身又删掉。
            string? source = videoResolver.Resolve(path);
            if (source == null)
            {
                displayError = videoResolver.LastError ?? "无法准备视频文件";
                return false;
            }

            currentMediaKind = MediaKind.Video;
            isVideoPaused = false;

            RandomImage.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;

            try
            {
                VideoPlayer.Source = new Uri(source);
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                currentMediaKind = MediaKind.None;
                displayError = ex.Message;
                return false;
            }

            // 固有尺寸要等 MediaOpened 才知道，先留空，届时再适配一次
            currentMediaNaturalSize = Size.Empty;
            return true;
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            int width = VideoPlayer.NaturalVideoWidth;
            int height = VideoPlayer.NaturalVideoHeight;
            if (width <= 0 || height <= 0) return;

            currentMediaNaturalSize = new Size(width, height);
            FitMediaToView(width, height);
            // 布局尺寸变了，画布平移的合法区间也要跟着重新收敛
            ReclampCanvas();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (currentMediaKind != MediaKind.Video) return;

            string reason = e.ErrorException?.Message ?? "未知播放错误";
            // 走到这里通常是系统缺对应解码器（VP9 / AV1 / HEVC 扩展等），
            // 也可能是替身建成功了但容器里的编码本身不受支持。
            ReportMediaFailure(currentImagePath, reason, MediaKind.Video);
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // 循环播放：随机浏览的场景下停在最后一帧黑屏上没有意义。
            // 必须 Stop 再 Play —— 播放中直接改 Position 是不生效的。
            if (currentMediaKind != MediaKind.Video || isVideoPaused) return;
            VideoPlayer.Stop();
            VideoPlayer.Play();
        }

        /// <summary>
        /// 拆掉当前正在显示 / 播放的媒体，回到"两层都空"的干净状态。
        /// 顺序有讲究：先清空 currentMediaKind，GIF 的 FrameReady 才会忽略停播过程送来的帧；
        /// 紧接着必须清空 Image.Source，OnDemand 的帧在流关闭后不能再渲染。
        /// </summary>
        private void TeardownMedia()
        {
            currentMediaKind = MediaKind.None;

            gifPlayer.Stop();
            RandomImage.Source = null;

            StopVideo();

            // 两层一起收起来，再由接下来的 Display* 点亮自己那一层。
            // 这步不能省：MediaElement 的 Source 置空后仍会渲染一块黑底，
            // 从视频切回图片时不折叠它就会把图盖住。
            RandomImage.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Collapsed;
        }

        private void StopVideo()
        {
            if (VideoPlayer.Source == null && !videoResolver.HasAlias) return;

            try
            {
                VideoPlayer.Stop();
                VideoPlayer.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping video: {ex.Message}");
            }
            VideoPlayer.Source = null;

            // 替身是硬链接，删掉只是减一次引用计数，动不到源文件。
            // 必须排在 Close 之后，否则文件还被 Media Foundation 占着，删不掉。
            videoResolver.Release();
        }

        /// <summary>
        /// 一个文件放不出来时的统一处理：记日志、按需提示一次原因、然后换下一个。
        /// 不逐个弹错误框 —— 随机浏览时每弹一次都要用户点一下，几十张坏图能把人逼疯。
        /// </summary>
        private void ReportMediaFailure(string mediaPath, string reason, MediaKind kind)
        {
            Console.WriteLine($"Error displaying {mediaPath}: {reason}");
            ShowFormatHintOnce(kind, mediaPath);

            if (++consecutiveMediaFailures >= MaxConsecutiveMediaFailures)
            {
                // 连着这么多个都放不出来，基本可以断定这个目录缺解码器，停下来告诉用户
                consecutiveMediaFailures = 0;
                MessageBox.Show(LanguageResources.GetText("TooManyMediaFailures", settings.CurrentLanguage),
                                LanguageResources.GetText("Notice", settings.CurrentLanguage),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 换下一个：由遍历引擎发牌，一轮之内不重复，所以不会在同一个坏文件上打转
            LoadRandomImage();
        }

        /// <summary>缺解码器的提示一个会话只弹一次，之后同类文件安静跳过。</summary>
        private void ShowFormatHintOnce(MediaKind kind, string mediaPath)
        {
            string key;
            if (kind == MediaKind.Video)
            {
                if (videoCodecHintShown) return;
                videoCodecHintShown = true;
                key = "VideoCodecMissing";
            }
            else if (MediaFormats.ExtensionOf(mediaPath) == ".webp")
            {
                // webp 靠商店扩展 Microsoft.WebpImageExtension 提供的 WIC 解码器，
                // 没装的机器上每张都会解码失败，所以专门提示一次去哪儿装。
                if (webpHintShown) return;
                webpHintShown = true;
                key = "WebpCodecMissing";
            }
            else
            {
                return;
            }

            MessageBox.Show(LanguageResources.GetText(key, settings.CurrentLanguage),
                            LanguageResources.GetText("Notice", settings.CurrentLanguage),
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 当前该往哪一层写画面。图片和 GIF 共用 RandomImage，视频走 VideoPlayer。
        /// </summary>
        private FrameworkElement ActiveMediaElement =>
            currentMediaKind == MediaKind.Video ? VideoPlayer : RandomImage;

        /// <summary>
        /// 按固有尺寸把当前媒体缩放到"恰好适配窗口"，也就是画布 Scale = 1 的基准。
        /// 图片传 BitmapImage.Width/Height（DIP，含 DPI 换算），GIF 和视频传像素尺寸。
        /// </summary>
        private void FitMediaToView(double mediaWidth, double mediaHeight)
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

            if (mediaWidth <= 0 || mediaHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return;

            double scale = Math.Min(viewWidth / mediaWidth, viewHeight / mediaHeight); // 取小的以保持纵横比

            FrameworkElement target = ActiveMediaElement;
            target.Width = mediaWidth * scale;
            target.Height = mediaHeight * scale;
        }

        // 双击图片 / 视频：使用系统默认程序打开
        // 中键与右键已交给 Window 层的画布手势统一处理，这里只认左键
        private void Media_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (Mouse.RightButton == MouseButtonState.Pressed) return;

            if ((DateTime.Now - lastClickTime).TotalMilliseconds <= doubleClickInterval)
            {
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
                    MessageBox.Show("无法打开文件: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

            // 获取同级目录，同时检查是否含有可显示的媒体
            var siblings = Directory.GetDirectories(parentDirectory)
                .Where(dir => dir != currentDir && ContainsMedia(dir)) // 排除当前目录并检查媒体
                .ToList();

            return siblings;
        }

        /// <summary>
        /// 检查目录下（不递归）是否含有受支持的媒体文件。
        /// 扩展名清单与扫描逻辑共用 <see cref="MediaFormats"/>：之前两处各写了一份且不一致
        /// （.tiff 只在这里有），导致右键菜单会列出含 tiff 的文件夹、点进去却说找不到图片。
        /// </summary>
        private bool ContainsMedia(string directoryPath)
        {
            try
            {
                return Directory.GetFiles(directoryPath).Any(MediaFormats.IsSupported);
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
        /// <summary>
        /// Space 在 Preview 阶段处理：MediaElement 自带的播放/暂停命令会吃掉 Space，
        /// 拖过视频后焦点若落在媒体上，KeyDown 就收不到。这里先接管，保证全局可切。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;
            if (IsAnyMotionCapturing)
            {
                e.Handled = true;
                return;
            }
            if (!e.IsRepeat) ToggleFavPanel();
            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsAnyMotionCapturing)
            {
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Left:
                    ShowPreviousImage(); // 左方向键
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
                case Key.Delete:
                    // 只在鼠标停在左侧 fav 面板上时移除收藏；绝不动右侧文件。
                    if (isFavPanelOpen && FavView.IsMouseOver)
                    {
                        FavView.RemoveCurrent();
                        e.Handled = true;
                    }
                    break;
                case Key.F:
                    if (isFavPanelOpen && FavView.IsMouseOver) FavView.Refit();
                    else RefitCanvas(); // F 键：重新适配窗口并复位画布
                    break;
                case Key.P:
                    if (isFavPanelOpen && FavView.IsMouseOver) FavView.TogglePlayback();
                    else TogglePlayback(); // P 键：GIF / 视频 播放暂停
                    break;
                case Key.Tab:
                    ToggleSettingsPanel(); // Tab 键
                    break;
            }
        }

        /// <summary>P 键：暂停 / 继续当前 GIF 或视频。静态图片上是空操作。</summary>
        private void TogglePlayback()
        {
            switch (currentMediaKind)
            {
                case MediaKind.Gif:
                    gifPlayer.TogglePause();
                    break;
                case MediaKind.Video:
                    // CanPause 只有在 MediaOpened 之后才有意义，靠 currentMediaKind 挡在前面
                    if (!VideoPlayer.CanPause) break;
                    if (isVideoPaused) VideoPlayer.Play();
                    else VideoPlayer.Pause();
                    isVideoPaused = !isVideoPaused;
                    break;
            }
        }

        //---------------------------画布：缩放与平移-------------------------
        // 缩放基准是 FitMediaToView 算出的"适配窗口"尺寸，canvas.Scale = 1.0 即适配状态。
        // 全部数学运算都在 CanvasViewState 里，这里只负责取布局信息和把结果写回 RenderTransform。

        private void ApplyCanvasTransform()
        {
            rightMotion.GetOffsetPixels(RootGrid.ActualHeight, out double motionX, out double motionY);

            CanvasScaleTransform.ScaleX = canvas.Scale;
            CanvasScaleTransform.ScaleY = canvas.Scale;
            CanvasTranslateTransform.X = canvas.OffsetX + motionX;
            CanvasTranslateTransform.Y = canvas.OffsetY + motionY;

            // 视频层有自己的一份 Transform（两个元素共用同一个未冻结的 Freezable 容易踩
            // 继承上下文的坑），所以同样的值再写一遍。隐藏的那层写了也不渲染，不亏。
            VideoScaleTransform.ScaleX = canvas.Scale;
            VideoScaleTransform.ScaleY = canvas.Scale;
            VideoTranslateTransform.X = canvas.OffsetX + motionX;
            VideoTranslateTransform.Y = canvas.OffsetY + motionY;
        }

        private void ResetCanvasTransform()
        {
            canvas = CanvasViewState.Fit;
            ApplyCanvasTransform();
        }

        /// <summary>
        /// 重新适配窗口并复位画布（F 键 / 右键原地单击）。
        /// 只复位 transform 是不够的：媒体只在切换时才做适配，窗口被拉伸过之后
        /// scale=1 已经不再等于"贴合窗口"，所以要按当前尺寸重算一次。
        /// 尺寸取自 currentMediaNaturalSize，图片 / GIF / 视频三条路都覆盖。
        /// </summary>
        private void RefitCanvas()
        {
            // F：真正回到 fit 必须把运动 overlay 也归零，否则 base 复位了、
            // 循环偏移还在，画面仍然不是 fit。
            rightMotion.FitReset();
            if (currentMediaNaturalSize.Width > 0 && currentMediaNaturalSize.Height > 0)
            {
                FitMediaToView(currentMediaNaturalSize.Width, currentMediaNaturalSize.Height);
            }
            ResetCanvasTransform();
        }

        /// <summary>
        /// 取当前媒体的布局尺寸（不受 RenderTransform 影响），用于换算缩放锚点与平移边界。
        /// </summary>
        private Size GetImageLayoutSize()
        {
            FrameworkElement target = ActiveMediaElement;
            // 优先用显式 Width/Height：分栏/拖分隔条时刚重算完 fit，
            // ActualWidth 可能还是上一轮布局的值；先读它会把缩放锚点和边界算偏。
            double w = target.Width;
            double h = target.Height;
            if (double.IsNaN(w) || w <= 0) w = target.ActualWidth;
            if (double.IsNaN(h) || h <= 0) h = target.ActualHeight;
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

            // 当前视觉 = base + motion overlay。缩放只改 base，所以锚点要把
            // 已经叠加的运动偏移扣掉，光标下的像素才会真的不动。
            rightMotion.GetOffsetPixels(RootGrid.ActualHeight, out double motionX, out double motionY);
            CanvasViewState next = canvas
                .ZoomAt(layout, mouseInGrid.X - motionX, mouseInGrid.Y - motionY, factor)
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
        // 右键：按住 = 暂停播放；按住 + 拖拽 = 平移；松手 = 从暂停相位续播。
        // 双键（右键先按，或左键先按后 400ms 内右键跟上）：位移过阈值 = 录制画布动作，
        // 松左键结束；双键 click（位移低于阈值）= 停止播放；F = fit。右键不再复位。
        // 中键：按住 + 拖拽 = 平移，原地单击 = 沿用旧逻辑（面包屑开资源管理器 / 图片开默认程序）

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 录制期间锁掉所有滚轮：不缩放，也不切图/fav。
            if (IsAnyMotionCapturing)
            {
                e.Handled = true;
                return;
            }

            // 按位置分区，而不是只看 OriginalSource：左栏 / 分隔条上的滚轮归左栏，
            // 即使右栏手势状态因为某种原因没清干净，也不能把它抢过来。
            if (!IsPointInside(RootGrid, e.GetPosition(RootGrid))) return;

            // 缩放的唯一入口：右键按住 + 滚轮。
            // 不能只看 e.RightButton 是否按下：右键点在面包屑上会弹同级目录菜单，
            // 那种情况下滚轮不该变成缩放。
            if (pendingGestureButton == MouseButton.Right)
            {
                ZoomCanvasAt(e.GetPosition(RootGrid), CanvasViewState.WheelToFactor(e.Delta));
                e.Handled = true;
                return;
            }

            // 正在平移时不要把滚轮解释成切图
            if (isPanning)
            {
                e.Handled = true;
                return;
            }

            DependencyObject? source = e.OriginalSource as DependencyObject;

            // 左侧 fav 面板 / 分隔条有自己的滚轮语义（顺序切 fav、右键缩放），
            // 窗口层不接管，交给 FavView 的 PreviewMouseWheel。
            if (IsDescendantOf(source, FavView) || IsDescendantOf(source, FavSplitter)) return;

            // 悬停在展开的设置面板上时不接管滚轮，交给面板里的 Slider / ComboBox 自己滚。
            // 面板收起时是 Collapsed，命中不到，所以下面的分区判断不受影响。
            if (IsDescendantOf(source, SettingsPanel)) return;

            if (currentMediaKind == MediaKind.None)
            {
                e.Handled = true;
                return;
            }

            // 滚轮按悬停位置分两区：面包屑 / 其余区域，哪一区是随机切由设置决定，
            // 另一区就是顺序切（↑/↓ 那套）
            bool onBreadcrumb = IsDescendantOf(source, PathButtonPanel);
            bool randomZone = onBreadcrumb
                ? settings.WheelRandomZone == 0
                : settings.WheelRandomZone == 1;

            if (randomZone)
            {
                if (e.Delta > 0) ShowPreviousImage();
                else if (e.Delta < 0) ShowNextImage();
            }
            else
            {
                if (e.Delta > 0) showPreviousImageInOrder();
                else if (e.Delta < 0) showNextImageInOrder();
            }
            e.Handled = true;
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsAnyMotionCapturing && e.ChangedButton == MouseButton.Middle)
            {
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Left)
            {
                Point leftDown = e.GetPosition(RootGrid);

                // 先只记录，不劫持：右键在短窗口内跟上才升级为双键手势。
                if (IsPointInside(RootGrid, leftDown)
                    && CanStartMotionLeftDown(e.OriginalSource as DependencyObject))
                {
                    hasLeftDownForMotion = true;
                    leftDownForMotion = leftDown;
                    leftDownForMotionTime = motionClock.Elapsed;
                }

                // 右键 pan 保持时再按左键：进入录制候选。位移过阈值才正式录。
                if (pendingGestureButton == MouseButton.Right
                    && !isRightMotionCandidate && !isRightMotionRecording
                    && IsPointInside(RootGrid, leftDown))
                {
                    BeginRightMotionCandidate(leftDown);
                    e.Handled = true;
                }
                return;
            }

            if (e.ChangedButton != MouseButton.Right && e.ChangedButton != MouseButton.Middle) return;

            // 右键/中键画布手势只属于右侧 Fetcher 视口。用位置判定兜底，避免
            // OriginalSource 落在 MediaElement 模板里时误判成右栏，从左侧抢走 capture。
            if (!IsPointInside(RootGrid, e.GetPosition(RootGrid))) return;
            if (!CanStartCanvasGesture(e.OriginalSource as DependencyObject, e.ChangedButton)) return;

            if (e.ChangedButton == MouseButton.Right)
            {
                // 右键按住 = 暂停播放；松开时若没有进入停止/录制，再续播。
                rightMotion.Pause();
                isRightPanEnded = false;

                // 左键先按住、右键在短窗口内跟上：同样进入双键候选。
                // 候选以右键按下的位置为锚点，左键按下后的抖动不计入阈值。
                Point rightDown = e.GetPosition(RootGrid);
                if (TryArmLeftFirstMotion(rightDown))
                {
                    BeginRightMotionCandidate(rightDown);
                    // 这次左键不再作为双击打开的第一击。
                    lastClickTime = DateTime.MinValue;
                }
            }

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
            if (isRightMotionCandidate || isRightMotionRecording)
            {
                HandleRightMotionMove(e.GetPosition(RootGrid));
                e.Handled = true;
                return;
            }

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
            if (e.ChangedButton == MouseButton.Left) hasLeftDownForMotion = false;

            if (e.ChangedButton == MouseButton.Left
                && (isRightMotionCandidate || isRightMotionRecording))
            {
                FinishRightMotionGesture(e.GetPosition(RootGrid));
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Right
                && (isRightMotionCandidate || isRightMotionRecording))
            {
                // 右键先松：pan 结束，但录制保持到左键松；capture 留着接左键 up。
                isRightPanEnded = true;
                pendingGestureButton = null;
                isPanning = false;
                Cursor = null;
                e.Handled = true;
                return;
            }

            if (!pendingGestureButton.HasValue || e.ChangedButton != pendingGestureButton.Value) return;

            MouseButton button = pendingGestureButton.Value;
            Button? pathButton = pendingGesturePathButton;
            bool wasDrag = isPanning;
            EndCanvasGesture();

            if (button == MouseButton.Right)
            {
                // 右键单击不再复位；按住只负责暂停，松手续播。
                rightMotion.Resume();
            }

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
        }

        private void Window_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (isRightMotionCandidate || isRightMotionRecording) CancelRightMotionGesture();
            CancelCanvasGesture();
        }

        private static double MotionGestureThreshold()
        {
            double system = Math.Max(SystemParameters.MinimumHorizontalDragDistance,
                                     SystemParameters.MinimumVerticalDragDistance);
            return Math.Max(MotionClickSlop, system * 2);
        }

        /// <summary>
        /// 左键先按是否够格升级为双键手势：左键仍按着、两键间隔在窗口内、
        /// 期间没有明显位移，且不是从设置/提示/面包屑上按下的。
        /// </summary>
        private bool TryArmLeftFirstMotion(Point rightPosition)
        {
            if (pendingGestureButton.HasValue || isRightMotionCandidate || isRightMotionRecording)
            {
                return false;
            }
            if (Mouse.LeftButton != MouseButtonState.Pressed || !hasLeftDownForMotion)
            {
                return false;
            }

            double elapsedMs = (motionClock.Elapsed - leftDownForMotionTime).TotalMilliseconds;
            if (elapsedMs < 0 || elapsedMs > MotionPairWindowMs)
            {
                return false;
            }

            double threshold = MotionGestureThreshold();
            return Math.Abs(rightPosition.X - leftDownForMotion.X) < threshold
                && Math.Abs(rightPosition.Y - leftDownForMotion.Y) < threshold;
        }

        private bool CanStartMotionLeftDown(DependencyObject? source)
        {
            if (source == null) return true;
            if (IsDescendantOf(source, SettingsPanel)) return false;
            if (IsDescendantOf(source, HintArea)) return false;
            if (IsDescendantOf(source, PathButtonPanel)) return false;
            return true;
        }

        private void BeginRightMotionCandidate(Point position)
        {
            isRightMotionCandidate = true;
            isRightMotionRecording = false;
            isRightPanEnded = false;
            rightMotionCandidateStart = position;
            rightMotionLastPoint = position;
            rightMotionAccumX = 0;
            rightMotionAccumY = 0;
        }

        private void HandleRightMotionMove(Point position)
        {
            if (isRightPanEnded) return;

            if (!isRightMotionRecording)
            {
                double dxTotal = position.X - rightMotionCandidateStart.X;
                double dyTotal = position.Y - rightMotionCandidateStart.Y;
                double threshold = MotionGestureThreshold();
                if (Math.Abs(dxTotal) < threshold && Math.Abs(dyTotal) < threshold) return;

                // 新录制替换旧 clip。先把当前 overlay 吸收进 base，视觉不跳；
                // 然后从当前指针位置开始录。
                rightMotion.GetOffsetPixels(RootGrid.ActualHeight, out double overlayX, out double overlayY);
                if (Math.Abs(overlayX) > 0.01 || Math.Abs(overlayY) > 0.01)
                {
                    canvas = canvas.PanBy(overlayX, overlayY);
                }

                rightMotion.BeginRecording();
                isRightMotionCandidate = false;
                isRightMotionRecording = true;
                rightMotionLastPoint = position;
                rightMotionStartTime = motionClock.Elapsed;
                rightMotionAccumX = 0;
                rightMotionAccumY = 0;
                RightMotionRecordingBorder.Visibility = Visibility.Visible;
                ApplyCanvasTransform();
                return;
            }

            double height = Math.Max(1, RootGrid.ActualHeight);
            double dx = (position.X - rightMotionLastPoint.X) / height;
            double dy = (position.Y - rightMotionLastPoint.Y) / height;
            rightMotionLastPoint = position;
            rightMotionAccumX += dx;
            rightMotionAccumY += dy;

            double time = (motionClock.Elapsed - rightMotionStartTime).TotalSeconds;
            rightMotion.AddSample(time, rightMotionAccumX, rightMotionAccumY);
            ApplyCanvasTransform();
        }

        private void FinishRightMotionGesture(Point position)
        {
            bool wasRecording = isRightMotionRecording;
            bool rightStillHeld = pendingGestureButton == MouseButton.Right;

            isRightMotionCandidate = false;
            isRightMotionRecording = false;
            RightMotionRecordingBorder.Visibility = Visibility.Collapsed;

            if (wasRecording)
            {
                rightMotion.EndRecording();
                if (rightStillHeld) rightMotion.Pause();
            }
            else
            {
                // 双键 click：停止；没有 clip 时是空操作。
                rightMotion.Stop();
            }

            if (rightStillHeld)
            {
                // 右键还按着：pan 从当前指针位置继续；播放先保持暂停。
                isRightPanEnded = false;
                pendingGestureButton = MouseButton.Right;
                isPanning = true;
                lastPanPos = position;
                gestureStartPos = position;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                isRightPanEnded = false;
                pendingGestureButton = null;
                isPanning = false;
                Cursor = null;
                if (IsMouseCaptured) ReleaseMouseCapture();
            }

            ApplyCanvasTransform();
        }

        private void CancelRightMotionGesture()
        {
            hasLeftDownForMotion = false;
            if (isRightMotionRecording)
            {
                rightMotion.GetOffsetPixels(RootGrid.ActualHeight, out double overlayX, out double overlayY);
                if (Math.Abs(overlayX) > 0.01 || Math.Abs(overlayY) > 0.01)
                {
                    canvas = canvas.PanBy(overlayX, overlayY);
                }
                rightMotion.CancelRecording();
                ApplyCanvasTransform();
            }

            isRightMotionCandidate = false;
            isRightMotionRecording = false;
            isRightPanEnded = false;
            RightMotionRecordingBorder.Visibility = Visibility.Collapsed;
        }

        private void OnMotionFrame(object? sender, EventArgs e)
        {
            TimeSpan now = motionClock.Elapsed;
            double dt = (now - lastMotionFrame).TotalSeconds;
            lastMotionFrame = now;

            if (rightMotion.IsPlaying)
            {
                rightMotion.Update(dt);
                ApplyCanvasTransform();
            }

            FavView.UpdateMotion(dt);
        }

        private void CancelCanvasGesture()
        {
            hasLeftDownForMotion = false;
            if (pendingGestureButton.HasValue || isPanning)
            {
                EndCanvasGesture();
            }
            else
            {
                Cursor = null;
            }
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
            if (IsDescendantOf(source, FavView) || IsDescendantOf(source, FavSplitter)) return false;
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

        private static bool IsPointInside(FrameworkElement element, Point point)
        {
            return point.X >= 0 && point.Y >= 0
                && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
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

        private Dictionary<string, List<int>> directoryImageIndices =
            new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
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
            DisplayMedia(imageFiles[currentIndex]);
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
            DisplayMedia(imageFiles[currentIndex]);
        }
        private void ShowPreviousImage()
        {
            if (!imageFiles.Any()) return;
            if (viewedIndex > 0)
            {
                viewedIndex--; // 移动到缓存中的上一张
                DisplayMedia(viewedImages[viewedIndex]);
            }
        }

        private void ShowNextImage()
        {
            if (!imageFiles.Any()) return;
            if (viewedIndex < viewedImages.Count - 1)
            {
                viewedIndex++; // 移动到缓存中的下一张
                DisplayMedia(viewedImages[viewedIndex]);
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

        //---------------------------左侧 fav 分栏-------------------------

        private void ToggleFavPanel()
        {
            // 开合前先收掉可能残留的手势/捕获，避免切布局后光标和 capture 粘在旧栏。
            CancelCanvasGesture();
            FavView.CancelGesture();
            CancelFavSplitterDrag();

            if (isFavPanelOpen) CloseFavPanel();
            else OpenFavPanel();
        }

        private void OpenFavPanel()
        {
            // 右侧还没加载任何媒体时不开一个没东西可拖的空面板。
            if (string.IsNullOrEmpty(currentImagePath)) return;

            isFavPanelOpen = true;
            FavView.Visibility = Visibility.Visible;
            FavSplitter.Visibility = Visibility.Visible;
            ApplyFavSplitRatio();
        }

        private void CloseFavPanel()
        {
            isFavPanelOpen = false;
            CancelFavSplitterDrag();

            FavView.Visibility = Visibility.Collapsed;
            FavSplitter.Visibility = Visibility.Collapsed;
            FavColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            FetcherColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        /// <summary>
        /// 按当前窗口宽度和记录的比例放置左栏。默认 50/50；
        /// 限制在 25%–75%，且两边都不小于约 300px。
        /// </summary>
        private void ApplyFavSplitRatio()
        {
            if (!isFavPanelOpen) return;

            double available = WindowRoot.ActualWidth - FavSplitterWidth;
            if (available <= 0) return;

            double minLeft = Math.Max(MinPaneWidth, available * MinFavRatio);
            double maxLeft = Math.Min(available - MinPaneWidth, available * MaxFavRatio);
            if (minLeft > maxLeft)
            {
                // 窗口窄到两边都放不下最小宽度时，居中兜底；MinWidth 已尽量避免走到这里。
                minLeft = maxLeft = available / 2;
            }

            double left = Math.Clamp(available * favSplitRatio, minLeft, maxLeft);
            favSplitRatio = left / available;

            FavColumn.Width = new GridLength(left, GridUnitType.Pixel);
            SplitterColumn.Width = new GridLength(FavSplitterWidth, GridUnitType.Pixel);
            FetcherColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        private void FavSplitter_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isFavPanelOpen) return;

            isDraggingFavSplitter = true;
            Color foreground = settings.GetForegroundColorFromPresetID();
            FavSplitterLine.Background = new SolidColorBrush(
                Color.FromArgb(150, foreground.R, foreground.G, foreground.B));

            FavSplitter.CaptureMouse();
            e.Handled = true;
        }

        private void FavSplitter_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!isDraggingFavSplitter) return;

            double available = WindowRoot.ActualWidth - FavSplitterWidth;
            if (available <= 0) return;

            double minLeft = Math.Max(MinPaneWidth, available * MinFavRatio);
            double maxLeft = Math.Min(available - MinPaneWidth, available * MaxFavRatio);
            if (minLeft > maxLeft) minLeft = maxLeft = available / 2;

            double left = Math.Clamp(e.GetPosition(WindowRoot).X - FavSplitterWidth / 2.0, minLeft, maxLeft);
            favSplitRatio = left / available;

            FavColumn.Width = new GridLength(left, GridUnitType.Pixel);
            SplitterColumn.Width = new GridLength(FavSplitterWidth, GridUnitType.Pixel);
            FetcherColumn.Width = new GridLength(1, GridUnitType.Star);
            e.Handled = true;
        }

        private void FavSplitter_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDraggingFavSplitter) return;

            isDraggingFavSplitter = false;
            if (FavSplitter.IsMouseCaptured) FavSplitter.ReleaseMouseCapture();
            SetFavSplitterHover(FavSplitter.IsMouseOver);
            e.Handled = true;
        }

        private void FavSplitter_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!isDraggingFavSplitter) return;
            isDraggingFavSplitter = false;
            SetFavSplitterHover(FavSplitter.IsMouseOver);
        }

        private void CancelFavSplitterDrag()
        {
            if (!isDraggingFavSplitter) return;
            isDraggingFavSplitter = false;
            if (FavSplitter.IsMouseCaptured) FavSplitter.ReleaseMouseCapture();
            SetFavSplitterHover(FavSplitter.IsMouseOver);
        }

        /// <summary>
        /// 右侧视口尺寸变化（窗口缩放 / 左右分栏 / 拖分隔条）时，实时重算 fit 基准。
        /// Scale 仍由用户手势控制：没手动放大时 Scale=1，就是始终贴合新视口。
        /// </summary>
        private void HandleFetcherViewportSizeChanged()
        {
            if (currentMediaNaturalSize.Width > 0 && currentMediaNaturalSize.Height > 0)
            {
                FitMediaToView(currentMediaNaturalSize.Width, currentMediaNaturalSize.Height);
            }
            ReclampCanvas();
            ApplyCanvasTransform();
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
            WheelZoneLabel.Text = LanguageResources.GetText("WheelRandomZone", language);
            SetComboItemText(WheelZoneComboBox, 0, "WheelZoneBreadcrumb", language);
            SetComboItemText(WheelZoneComboBox, 1, "WheelZoneElsewhere", language);
            
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
            // 构造函数里设 SelectedIndex 也会触发到这里，而界面语言那时已由 UpdateUILanguage
            // 单独刷过了；不挡住的话每次启动都会白存一次盘，万一 Load 读到的是别的程序
            // 留下的文件，还会把默认值直接写死到磁盘上。
            if (settings == null || !loaded) return;

            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                if (int.TryParse(selectedItem.Tag.ToString(), out int languageIndex))
                {
                    // 更新语言设置
                    settings.SetLanguage(languageIndex);
                    
                    // 更新界面语言
                    UpdateUILanguage(settings.CurrentLanguage);
                }
            }
        }

        // 滚轮随机区下拉：选完立即落盘，滚轮逻辑直接读 settings.WheelRandomZone
        private void WheelZoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (settings == null || !loaded) return;
            if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem selectedItem
                && int.TryParse(selectedItem.Tag?.ToString(), out int zone))
            {
                settings.SetWheelRandomZone(zone);
            }
        }

        private static void SetComboItemText(ComboBox comboBox, int index, string key, Language language)
        {
            if (comboBox.Items.Count > index && comboBox.Items[index] is ComboBoxItem item)
            {
                item.Content = LanguageResources.GetText(key, language);
            }
        }
    }
}
