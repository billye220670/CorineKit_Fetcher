using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

namespace ImageSelector
{
    /// <summary>
    /// 左侧收藏夹看图区。
    ///
    /// 它不是第二个 Fetcher：没有目录扫描、随机遍历、面包屑和缓存圆点，
    /// 只维护一份按加入顺序排列的 fav 路径列表、当前索引，以及自己的媒体/画布状态。
    /// 所有媒体和缩放平移资源都由本控件持有，和右侧主视图互不影响。
    /// </summary>
    public partial class FavPane : UserControl
    {
        private const double PanEdgeMargin = 60;

        private readonly List<string> favorites = new List<string>();
        private readonly HashSet<string> favoriteLookup =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly GifPlayer gifPlayer = new GifPlayer();
        private readonly VideoSourceResolver videoResolver = new VideoSourceResolver();

        private int currentIndex = -1;
        private MediaKind currentKind = MediaKind.None;
        private Size naturalSize = Size.Empty;
        private CanvasViewState canvas = CanvasViewState.Fit;

        private MouseButton? pendingGestureButton;
        private Point gestureStartPos;
        private Point lastPanPos;
        private bool isPanning;

        private bool isVideoPaused;
        private bool resumeGifAfterShow;
        private bool resumeVideoAfterShow;

        //---------------------------画布动作录制/回放-------------------------
        private const double MotionClickSlop = 8.0;
        // 左键先按时，右键必须在这段时间内跟上才算同一组双键手势。
        private const double MotionPairWindowMs = 400.0;
        private readonly CanvasMotionPlayer motion = new CanvasMotionPlayer();
        private readonly Stopwatch motionClock = Stopwatch.StartNew();
        private bool isMotionCandidate;
        private bool isMotionRecording;
        private bool isRightPanEnded;
        private Point motionCandidateStart;
        private TimeSpan motionStartTime;
        private Point motionLastPoint;
        private double motionAccumX;
        private double motionAccumY;
        private bool resumeMotionAfterShow;

        // 左键先按：记录起点与时刻；右键跟上才升级为双键手势。
        // 文件拖出也用它做位移阈值，避免点击抖动直接开始拖拽。
        private bool hasLeftDownForMotion;
        private Point leftDownForMotion;
        private TimeSpan leftDownForMotionTime;

        public bool IsMotionCapturing => isMotionCandidate || isMotionRecording;

        public FavPane()
        {
            InitializeComponent();
            gifPlayer.FrameReady += GifPlayer_FrameReady;
            gifPlayer.Failed += GifPlayer_Failed;
            IsVisibleChanged += FavPane_IsVisibleChanged;
        }

        /// <summary>当前 fav 路径（按加入顺序）；空列表时为 null。</summary>
        public string? CurrentPath =>
            currentIndex >= 0 && currentIndex < favorites.Count ? favorites[currentIndex] : null;

        public int Count => favorites.Count;

        public int CurrentIndex => currentIndex;

        public bool HasFavorites => favorites.Count > 0;

        /// <summary>左侧正在作为拖拽源；窗口 Drop 用它区分内部拖拽与外部导入。</summary>
        public bool IsDraggingOut { get; private set; }

        public IReadOnlyList<string> FavoritePaths => favorites;

        /// <summary>跟随主题背景；空列表时露出的就是这块颜色。</summary>
        public void ApplyTheme(Color background)
        {
            Background = new SolidColorBrush(background);
        }

        /// <summary>
        /// 加入一张 fav。已存在时去重；show=true 时切换到那一张。
        /// 返回是否新增了列表项。
        /// </summary>
        public bool AddFavorite(string path, bool show = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!MediaFormats.IsSupported(fullPath) || !File.Exists(fullPath)) return false;

                if (favoriteLookup.Contains(fullPath))
                {
                    if (show)
                    {
                        int existing = favorites.FindIndex(
                            item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
                        if (existing >= 0) DisplayFavorite(existing);
                    }
                    return false;
                }

                favorites.Add(fullPath);
                favoriteLookup.Add(fullPath);

                if (show) DisplayFavorite(favorites.Count - 1);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding favorite {path}: {ex.Message}");
                return false;
            }
        }

        public bool ContainsFavorite(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && favoriteLookup.Contains(path);
        }

        /// <summary>在 fav 列表内循环切换；delta = +1 下一张，-1 上一张。</summary>
        public void Cycle(int delta)
        {
            if (favorites.Count == 0 || delta == 0) return;

            int baseIndex = currentIndex >= 0 && currentIndex < favorites.Count ? currentIndex : 0;
            int next = ((baseIndex + delta) % favorites.Count + favorites.Count) % favorites.Count;
            if (next == currentIndex) return;

            DisplayFavorite(next);
        }

        /// <summary>
        /// 移除当前 fav。只动列表，不碰磁盘文件。
        /// 删除后显示原本排在它后面的那张；删的是最后一张则回到第一张；删空则空白。
        /// </summary>
        public void RemoveCurrent()
        {
            if (favorites.Count == 0) return;

            int removedIndex = currentIndex;
            if (removedIndex < 0 || removedIndex >= favorites.Count) removedIndex = 0;

            string removed = favorites[removedIndex];
            favorites.RemoveAt(removedIndex);
            favoriteLookup.Remove(removed);

            if (favorites.Count == 0)
            {
                currentIndex = -1;
                ShowEmpty();
                return;
            }

            if (removedIndex >= favorites.Count) removedIndex = 0;
            DisplayFavorite(removedIndex);
        }

        public void ClearFavorites()
        {
            favorites.Clear();
            favoriteLookup.Clear();
            currentIndex = -1;
            ShowEmpty();
        }

        /// <summary>F 键 / 右键单击：按当前视口重新适配并复位画布。</summary>
        public void Refit()
        {
            motion.FitReset();
            if (naturalSize.Width > 0 && naturalSize.Height > 0)
            {
                FitMediaToView(naturalSize.Width, naturalSize.Height);
            }
            ResetCanvasTransform();
        }

        /// <summary>P 键：仅在当前媒体是 GIF / 视频时有意义。</summary>
        public void CancelGesture()
        {
            CancelMotionGesture();
            if (pendingGestureButton.HasValue || isPanning)
            {
                EndGesture();
            }
            else
            {
                Cursor = null;
            }
        }

        public void TogglePlayback()
        {
            switch (currentKind)
            {
                case MediaKind.Gif:
                    gifPlayer.TogglePause();
                    break;
                case MediaKind.Video:
                    if (!FavVideo.CanPause) break;
                    if (isVideoPaused)
                    {
                        FavVideo.Play();
                        isVideoPaused = false;
                    }
                    else
                    {
                        FavVideo.Pause();
                        isVideoPaused = true;
                    }
                    break;
            }
        }

        /// <summary>窗口关闭：停媒体、释放替身与流。fav 列表本身不落盘。</summary>
        public void Shutdown()
        {
            motion.Reset();
            FavMotionRecordingBorder.Visibility = Visibility.Collapsed;
            TeardownMedia();
            gifPlayer.Dispose();
            videoResolver.Dispose();
            favorites.Clear();
            favoriteLookup.Clear();
            currentIndex = -1;
        }

        /// <summary>由窗口的统一渲染循环驱动；左右各自一份状态。</summary>
        public void UpdateMotion(double dt)
        {
            if (!motion.IsPlaying) return;
            motion.Update(dt);
            ApplyCanvasTransform();
        }

        private void DisplayFavorite(int index)
        {
            if (index < 0 || index >= favorites.Count)
            {
                currentIndex = -1;
                ShowEmpty();
                return;
            }

            currentIndex = index;
            DisplayMedia(favorites[index]);
        }

        private void DisplayMedia(string path)
        {
            MediaKind kind = MediaFormats.Classify(path);
            bool started = kind switch
            {
                MediaKind.Image => DisplayStillImage(path),
                MediaKind.Gif => DisplayGif(path),
                MediaKind.Video => DisplayVideo(path),
                _ => false
            };

            if (!started)
            {
                Console.WriteLine($"FavPane: cannot display {path}");
                ShowEmpty();
                return;
            }

            ResetCanvasTransform();
        }

        private bool DisplayStillImage(string path)
        {
            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();

                TeardownMedia();
                currentKind = MediaKind.Image;
                FavImage.Visibility = Visibility.Visible;
                FavImage.Source = bitmap;

                naturalSize = new Size(bitmap.Width, bitmap.Height);
                FitMediaToView(bitmap.Width, bitmap.Height);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FavPane: failed to decode image {path}: {ex.Message}");
                return false;
            }
        }

        private bool DisplayGif(string path)
        {
            TeardownMedia();

            currentKind = MediaKind.Gif;
            FavImage.Visibility = Visibility.Visible;

            if (!gifPlayer.Open(path))
            {
                currentKind = MediaKind.None;
                FavImage.Visibility = Visibility.Collapsed;
                Console.WriteLine($"FavPane: failed to open gif {path}: {gifPlayer.LastError}");
                return false;
            }

            naturalSize = new Size(gifPlayer.PixelWidth, gifPlayer.PixelHeight);
            FitMediaToView(gifPlayer.PixelWidth, gifPlayer.PixelHeight);
            if (!IsVisible) SuspendPlayback();
            return true;
        }

        private bool DisplayVideo(string path)
        {
            TeardownMedia();

            string? source = videoResolver.Resolve(path);
            if (source == null)
            {
                Console.WriteLine($"FavPane: failed to resolve video {path}: {videoResolver.LastError}");
                return false;
            }

            currentKind = MediaKind.Video;
            isVideoPaused = false;

            FavImage.Visibility = Visibility.Collapsed;
            FavVideo.Visibility = Visibility.Visible;

            try
            {
                FavVideo.Source = new Uri(source);
                FavVideo.Play();
            }
            catch (Exception ex)
            {
                currentKind = MediaKind.None;
                Console.WriteLine($"FavPane: failed to start video {path}: {ex.Message}");
                return false;
            }

            naturalSize = Size.Empty;
            if (!IsVisible) SuspendPlayback();
            return true;
        }

        private void GifPlayer_FrameReady(BitmapSource frame)
        {
            if (currentKind != MediaKind.Gif) return;
            FavImage.Source = frame;
        }

        private void GifPlayer_Failed(string reason)
        {
            if (currentKind != MediaKind.Gif) return;
            Console.WriteLine($"FavPane: gif playback failed: {reason}");
            ShowEmpty();
        }

        private void FavVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (currentKind != MediaKind.Video) return;

            int width = FavVideo.NaturalVideoWidth;
            int height = FavVideo.NaturalVideoHeight;
            if (width <= 0 || height <= 0) return;

            naturalSize = new Size(width, height);
            FitMediaToView(width, height);
            ReclampCanvas();
            ApplyCanvasTransform();
        }

        private void FavVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (currentKind != MediaKind.Video) return;
            Console.WriteLine($"FavPane: video failed: {e.ErrorException?.Message}");
            ShowEmpty();
        }

        private void FavVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (currentKind != MediaKind.Video || isVideoPaused) return;
            FavVideo.Stop();
            FavVideo.Play();
        }

        private void TeardownMedia()
        {
            currentKind = MediaKind.None;

            gifPlayer.Stop();
            FavImage.Source = null;

            StopVideo();

            FavImage.Visibility = Visibility.Collapsed;
            FavVideo.Visibility = Visibility.Collapsed;
        }

        private void StopVideo()
        {
            if (FavVideo.Source == null && !videoResolver.HasAlias) return;

            try
            {
                FavVideo.Stop();
                FavVideo.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FavPane: error stopping video: {ex.Message}");
            }

            FavVideo.Source = null;
            videoResolver.Release();
        }

        private void ShowEmpty()
        {
            TeardownMedia();
            naturalSize = Size.Empty;
            canvas = CanvasViewState.Fit;
            ApplyCanvasTransform();
        }

        private void FavPane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                ResumePlayback();
                if (resumeMotionAfterShow)
                {
                    resumeMotionAfterShow = false;
                    motion.Resume();
                }
            }
            else
            {
                SuspendPlayback();
                if (motion.IsPlaying)
                {
                    resumeMotionAfterShow = true;
                    motion.Pause();
                }
                else
                {
                    resumeMotionAfterShow = false;
                }
            }
        }

        private void SuspendPlayback()
        {
            if (currentKind == MediaKind.Gif && gifPlayer.IsPlaying)
            {
                resumeGifAfterShow = true;
                gifPlayer.TogglePause();
            }

            if (currentKind == MediaKind.Video && !isVideoPaused && FavVideo.CanPause)
            {
                resumeVideoAfterShow = true;
                FavVideo.Pause();
                isVideoPaused = true;
            }
        }

        private void ResumePlayback()
        {
            if (resumeGifAfterShow)
            {
                resumeGifAfterShow = false;
                if (currentKind == MediaKind.Gif && gifPlayer.IsPaused)
                {
                    gifPlayer.TogglePause();
                }
            }

            if (resumeVideoAfterShow)
            {
                resumeVideoAfterShow = false;
                if (currentKind == MediaKind.Video)
                {
                    FavVideo.Play();
                    isVideoPaused = false;
                }
            }
        }

        //---------------------------画布：缩放与平移-------------------------

        private FrameworkElement? ActiveMediaElement => currentKind switch
        {
            MediaKind.Video => FavVideo,
            MediaKind.Image or MediaKind.Gif => FavImage,
            _ => null
        };

        private void ApplyCanvasTransform()
        {
            motion.GetOffsetPixels(PaneRoot.ActualHeight, out double motionX, out double motionY);

            FavImageScaleTransform.ScaleX = canvas.Scale;
            FavImageScaleTransform.ScaleY = canvas.Scale;
            FavImageTranslateTransform.X = canvas.OffsetX + motionX;
            FavImageTranslateTransform.Y = canvas.OffsetY + motionY;

            FavVideoScaleTransform.ScaleX = canvas.Scale;
            FavVideoScaleTransform.ScaleY = canvas.Scale;
            FavVideoTranslateTransform.X = canvas.OffsetX + motionX;
            FavVideoTranslateTransform.Y = canvas.OffsetY + motionY;
        }

        private void ResetCanvasTransform()
        {
            canvas = CanvasViewState.Fit;
            ApplyCanvasTransform();
        }

        private Size GetActiveMediaSize()
        {
            FrameworkElement? target = ActiveMediaElement;
            if (target == null) return Size.Empty;

            // 优先用显式 Width/Height：拖分隔条时刚重算完 fit，ActualWidth 可能还是上一布局的值。
            double width = target.Width;
            double height = target.Height;
            if (double.IsNaN(width) || width <= 0) width = target.ActualWidth;
            if (double.IsNaN(height) || height <= 0) height = target.ActualHeight;
            if (double.IsNaN(width) || width < 0) width = 0;
            if (double.IsNaN(height) || height < 0) height = 0;
            return new Size(width, height);
        }

        private CanvasViewState.Layout GetCanvasLayout()
        {
            Size size = GetActiveMediaSize();
            return new CanvasViewState.Layout
            {
                ImageWidth = size.Width,
                ImageHeight = size.Height,
                OriginX = Math.Max(0, (PaneRoot.ActualWidth - size.Width) / 2),
                OriginY = Math.Max(0, (PaneRoot.ActualHeight - size.Height) / 2),
                ViewWidth = PaneRoot.ActualWidth,
                ViewHeight = PaneRoot.ActualHeight
            };
        }

        private void FitMediaToView(double mediaWidth, double mediaHeight)
        {
            FrameworkElement? target = ActiveMediaElement;
            if (target == null) return;

            double viewWidth = PaneRoot.ActualWidth;
            double viewHeight = PaneRoot.ActualHeight;
            if (viewWidth <= 0 || viewHeight <= 0)
            {
                viewWidth = ActualWidth;
                viewHeight = ActualHeight;
            }

            if (mediaWidth <= 0 || mediaHeight <= 0 || viewWidth <= 0 || viewHeight <= 0) return;

            double scale = Math.Min(viewWidth / mediaWidth, viewHeight / mediaHeight);
            target.Width = mediaWidth * scale;
            target.Height = mediaHeight * scale;
        }

        private void Pane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (naturalSize.Width <= 0 || naturalSize.Height <= 0) return;

            FitMediaToView(naturalSize.Width, naturalSize.Height);
            canvas = canvas.Clamp(GetCanvasLayout(), PanEdgeMargin);
            ApplyCanvasTransform();
        }

        private void ZoomCanvasAt(Point mouseInPane, double factor)
        {
            CanvasViewState.Layout layout = GetCanvasLayout();

            // 视觉 = base + motion overlay；缩放只改 base，锚点扣掉 overlay。
            motion.GetOffsetPixels(PaneRoot.ActualHeight, out double motionX, out double motionY);
            CanvasViewState next = canvas
                .ZoomAt(layout, mouseInPane.X - motionX, mouseInPane.Y - motionY, factor)
                .Clamp(layout, PanEdgeMargin);

            if (next.Equals(canvas)) return;
            canvas = next;
            ApplyCanvasTransform();
        }

        private void PanCanvasTo(Point mouseInPane)
        {
            CanvasViewState.Layout layout = GetCanvasLayout();
            canvas = canvas
                .PanBy(mouseInPane.X - lastPanPos.X, mouseInPane.Y - lastPanPos.Y)
                .Clamp(layout, PanEdgeMargin);
            lastPanPos = mouseInPane;
            ApplyCanvasTransform();
        }

        private void ReclampCanvas()
        {
            CanvasViewState next = canvas.Clamp(GetCanvasLayout(), PanEdgeMargin);
            if (next.Equals(canvas)) return;
            canvas = next;
            ApplyCanvasTransform();
        }

        //---------------------------鼠标手势-------------------------

        private void Pane_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsMotionCapturing)
            {
                e.Handled = true;
                return;
            }

            if (pendingGestureButton == MouseButton.Right)
            {
                ZoomCanvasAt(e.GetPosition(PaneRoot), CanvasViewState.WheelToFactor(e.Delta));
                e.Handled = true;
                return;
            }

            if (isPanning)
            {
                e.Handled = true;
                return;
            }

            if (favorites.Count == 0 || currentKind == MediaKind.None)
            {
                e.Handled = true;
                return;
            }

            Cycle(e.Delta > 0 ? -1 : 1);
            e.Handled = true;
        }

        private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            if (e.ChangedButton == MouseButton.Left)
            {
                Point leftDown = e.GetPosition(PaneRoot);
                if (leftDown.X >= 0 && leftDown.Y >= 0
                    && leftDown.X <= PaneRoot.ActualWidth && leftDown.Y <= PaneRoot.ActualHeight)
                {
                    hasLeftDownForMotion = true;
                    leftDownForMotion = leftDown;
                    leftDownForMotionTime = motionClock.Elapsed;
                }

                if (pendingGestureButton == MouseButton.Right && !IsMotionCapturing
                    && leftDown.X >= 0 && leftDown.Y >= 0
                    && leftDown.X <= PaneRoot.ActualWidth && leftDown.Y <= PaneRoot.ActualHeight)
                {
                    BeginMotionCandidate(leftDown);
                    e.Handled = true;
                }
                return;
            }

            if (e.ChangedButton != MouseButton.Right) return;

            // 右键按住 = 暂停播放；松开时若没进入停止/录制，再续播。
            motion.Pause();
            isRightPanEnded = false;

            // 左键先按住、右键在短窗口内跟上：同样进入双键候选；
            // 候选以右键按下的位置为锚点，左键按下后的抖动不计入阈值。
            Point rightDown = e.GetPosition(PaneRoot);
            bool leftFirst = TryArmLeftFirstMotion(rightDown);

            pendingGestureButton = MouseButton.Right;
            gestureStartPos = rightDown;
            lastPanPos = rightDown;
            isPanning = false;
            if (leftFirst)
            {
                BeginMotionCandidate(rightDown);
            }

            // 必须捕获在 PaneRoot 上：Move/Up 处理器在 PaneRoot。
            // 捕获到 UserControl 会把后续事件路由到父级，Move/Up 永远收不到，
            // capture 就永久粘住，右侧手势也会被它抢走。
            PaneRoot.CaptureMouse();
            e.Handled = true;
        }

        private void Pane_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (IsMotionCapturing)
            {
                HandleMotionMove(e.GetPosition(PaneRoot));
                e.Handled = true;
                return;
            }

            if (!pendingGestureButton.HasValue) return;

            Point position = e.GetPosition(PaneRoot);

            if (!isPanning)
            {
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

        private void Pane_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) hasLeftDownForMotion = false;

            if (e.ChangedButton == MouseButton.Left && IsMotionCapturing)
            {
                FinishMotionGesture(e.GetPosition(PaneRoot));
                e.Handled = true;
                return;
            }

            if (e.ChangedButton == MouseButton.Right && IsMotionCapturing)
            {
                isRightPanEnded = true;
                pendingGestureButton = null;
                isPanning = false;
                Cursor = null;
                e.Handled = true;
                return;
            }

            if (!pendingGestureButton.HasValue || e.ChangedButton != pendingGestureButton.Value) return;

            MouseButton button = pendingGestureButton.Value;
            bool wasDrag = isPanning;
            EndGesture();

            if (button == MouseButton.Right)
            {
                // 右键单击不再复位；按住只负责暂停，松手续播。
                motion.Resume();
            }

            if (!wasDrag && button == MouseButton.Right)
            {
                e.Handled = true;
            }
        }

        private void Pane_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (IsMotionCapturing) CancelMotionGesture();
            if (pendingGestureButton.HasValue) EndGesture();
        }

        private static double MotionGestureThreshold()
        {
            double system = Math.Max(SystemParameters.MinimumHorizontalDragDistance,
                                     SystemParameters.MinimumVerticalDragDistance);
            return Math.Max(MotionClickSlop, system * 2);
        }

        /// <summary>
        /// 左键先按是否够格升级为双键手势：左键仍按着、两键间隔在窗口内、
        /// 期间没有明显位移。
        /// </summary>
        private bool TryArmLeftFirstMotion(Point rightPosition)
        {
            if (pendingGestureButton.HasValue || IsMotionCapturing)
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

        private void BeginMotionCandidate(Point position)
        {
            isMotionCandidate = true;
            isMotionRecording = false;
            isRightPanEnded = false;
            motionCandidateStart = position;
            motionLastPoint = position;
            motionAccumX = 0;
            motionAccumY = 0;
        }

        private void HandleMotionMove(Point position)
        {
            if (isRightPanEnded) return;

            if (!isMotionRecording)
            {
                double dxTotal = position.X - motionCandidateStart.X;
                double dyTotal = position.Y - motionCandidateStart.Y;
                double threshold = MotionGestureThreshold();
                if (Math.Abs(dxTotal) < threshold && Math.Abs(dyTotal) < threshold) return;

                motion.GetOffsetPixels(PaneRoot.ActualHeight, out double overlayX, out double overlayY);
                if (Math.Abs(overlayX) > 0.01 || Math.Abs(overlayY) > 0.01)
                {
                    canvas = canvas.PanBy(overlayX, overlayY);
                }

                motion.BeginRecording();
                isMotionCandidate = false;
                isMotionRecording = true;
                motionLastPoint = position;
                motionStartTime = motionClock.Elapsed;
                motionAccumX = 0;
                motionAccumY = 0;
                FavMotionRecordingBorder.Visibility = Visibility.Visible;
                ApplyCanvasTransform();
                return;
            }

            double height = Math.Max(1, PaneRoot.ActualHeight);
            double dx = (position.X - motionLastPoint.X) / height;
            double dy = (position.Y - motionLastPoint.Y) / height;
            motionLastPoint = position;
            motionAccumX += dx;
            motionAccumY += dy;

            double time = (motionClock.Elapsed - motionStartTime).TotalSeconds;
            motion.AddSample(time, motionAccumX, motionAccumY);
            ApplyCanvasTransform();
        }

        private void FinishMotionGesture(Point position)
        {
            bool wasRecording = isMotionRecording;
            bool rightStillHeld = pendingGestureButton == MouseButton.Right;

            isMotionCandidate = false;
            isMotionRecording = false;
            FavMotionRecordingBorder.Visibility = Visibility.Collapsed;

            if (wasRecording)
            {
                motion.EndRecording();
                if (rightStillHeld) motion.Pause();
            }
            else
            {
                motion.Stop();
            }

            if (rightStillHeld)
            {
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
                if (PaneRoot.IsMouseCaptured) PaneRoot.ReleaseMouseCapture();
            }

            ApplyCanvasTransform();
        }

        private void CancelMotionGesture()
        {
            hasLeftDownForMotion = false;
            if (isMotionRecording)
            {
                motion.GetOffsetPixels(PaneRoot.ActualHeight, out double overlayX, out double overlayY);
                if (Math.Abs(overlayX) > 0.01 || Math.Abs(overlayY) > 0.01)
                {
                    canvas = canvas.PanBy(overlayX, overlayY);
                }
                motion.CancelRecording();
                ApplyCanvasTransform();
            }

            isMotionCandidate = false;
            isMotionRecording = false;
            isRightPanEnded = false;
            FavMotionRecordingBorder.Visibility = Visibility.Collapsed;
        }

        private void EndGesture()
        {
            hasLeftDownForMotion = false;
            pendingGestureButton = null;
            isPanning = false;
            Cursor = null;
            if (PaneRoot.IsMouseCaptured) PaneRoot.ReleaseMouseCapture();
        }

        //---------------------------拖拽源-------------------------

        private void FavMedia_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (Mouse.RightButton == MouseButtonState.Pressed) return;
            if (pendingGestureButton.HasValue || isPanning || IsMotionCapturing) return;

            // 左键先按可能是在等右键组成双键手势；移动过阈值才拖出文件。
            if (!hasLeftDownForMotion) return;
            Point position = e.GetPosition(PaneRoot);
            double threshold = MotionGestureThreshold();
            if (Math.Abs(position.X - leftDownForMotion.X) < threshold &&
                Math.Abs(position.Y - leftDownForMotion.Y) < threshold)
            {
                return;
            }

            string? path = CurrentPath;
            if (string.IsNullOrEmpty(path)) return;

            hasLeftDownForMotion = false;
            IsDraggingOut = true;
            try
            {
                DataObject data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            }
            finally
            {
                IsDraggingOut = false;
            }
        }
    }
}