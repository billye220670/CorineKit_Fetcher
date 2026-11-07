using System;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageSelector
{
    /// <summary>
    /// GIF 逐帧播放器：在 UI 线程上用 DispatcherTimer 推进 <see cref="BitmapDecoder"/> 的帧。
    ///
    /// 为什么不做成"一次性解码全部帧再播放"：实测一张 369 帧、1228x753 的 GIF，
    /// 全帧驻留要吃掉 1.3GB 工作集；而 OnDemand 懒解码连跑两遍整段动画，
    /// 进程工作集只到 77MB。
    ///
    /// 为什么懒解码不会越播越慢：GIF 帧带处置方式，合成第 N 帧理论上要从头算，
    /// 但实测按顺序访问时"最后 1/4 帧的耗时 / 最前 1/4 帧的耗时 = 1.75x"
    /// （若真按重合成模型应接近 7x），单帧平均 0.19ms，冷启动直接跳到最后一帧也只要 6.5ms，
    /// 相对 60ms 量级的帧间隔可以忽略。所以顺序推进就够了，不需要预取缓存。
    ///
    /// 唯一要小心的是流的生命周期：OnDemand 的帧是懒解码的，流一关，之后任何触发
    /// 重新解码的渲染都会抛异常。所以 <see cref="Stop"/> 之后调用方必须立刻把
    /// Image.Source 清掉或换成别的位图，不能再让上一帧挂在可视树上。
    /// </summary>
    internal sealed class GifPlayer : IDisposable
    {
        // DispatcherTimer 挂在 UI 消息泵上，实际分辨率受系统计时器限制（约 15.6ms），
        // 再小的间隔也没有意义。
        private const int MinFrameMs = 20;
        private const int MaxFrameMs = 10000;

        // GIF 里写着 0 延迟的帧很常见，沿用浏览器惯例按 100ms 兜底，否则会闪成噪点。
        private const int DefaultFrameMs = 100;

        private readonly DispatcherTimer timer;
        private Stream? stream;
        private BitmapDecoder? decoder;

        private int frameIndex;
        private int completedLoops;
        private int loopCount;          // NETSCAPE2.0 循环次数，0 = 无限
        private bool paused;

        /// <summary>新一帧可以显示了（首帧在 <see cref="Open"/> 内同步触发）。</summary>
        public event Action<BitmapSource>? FrameReady;

        /// <summary>播放中途解码失败（<see cref="Open"/> 的失败走返回值 + <see cref="LastError"/>）。</summary>
        public event Action<string>? Failed;

        /// <summary>最近一次 <see cref="Open"/> 失败的原因，成功时为 null。</summary>
        public string? LastError { get; private set; }

        public int FrameCount { get; private set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }

        /// <summary>是否无限循环（读不到元数据时也按无限处理，宁可多播不要静帧）。</summary>
        public bool IsLooping => loopCount == 0;

        public bool IsPlaying => timer.IsEnabled;
        public bool IsPaused => paused;

        public GifPlayer()
        {
            // Render 优先级低于 Input，动画推进不会抢走鼠标键盘响应
            timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Tick += OnTick;
        }

        /// <summary>
        /// 打开并开始播放。失败返回 false 并触发 <see cref="Failed"/>，此时不持有任何资源。
        /// 调用方应先订阅 <see cref="FrameReady"/>：首帧在本方法内同步送达。
        /// </summary>
        public bool Open(string path)
        {
            LastError = null;
            Stop();

            try
            {
                // FileShare.Read：只挡住写入和删除，别的程序照样能读这张图。
                // 之前图片走 UriSource 是独占锁，用户想挪文件都得先切走。
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                                               BitmapCacheOption.OnDemand);

                FrameCount = decoder.Frames.Count;
                if (FrameCount == 0) throw new InvalidDataException("GIF 中没有可解码的帧");

                PixelWidth = decoder.Frames[0].PixelWidth;
                PixelHeight = decoder.Frames[0].PixelHeight;
                loopCount = ReadLoopCount(decoder);

                frameIndex = 0;
                completedLoops = 0;
                paused = false;

                ShowFrame(0);
                if (FrameCount > 1) ScheduleNext();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseDecoder();
                return false;
            }
        }

        /// <summary>暂停 / 继续。单帧 GIF 或播放已结束时是空操作。</summary>
        public void TogglePause()
        {
            if (decoder == null || FrameCount <= 1) return;

            paused = !paused;
            if (paused) timer.Stop();
            else ScheduleNext();
        }

        /// <summary>
        /// 停止播放并关闭文件。
        /// 调用方必须紧接着清掉 Image.Source：OnDemand 的帧随流一起失效，留着会渲染出错。
        /// </summary>
        public void Stop()
        {
            timer.Stop();
            paused = false;
            CloseDecoder();
        }

        private void ShowFrame(int index)
        {
            FrameReady?.Invoke(decoder!.Frames[index]);
        }

        private void ScheduleNext()
        {
            timer.Interval = TimeSpan.FromMilliseconds(GetFrameDelayMs(frameIndex));
            timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            timer.Stop();
            if (decoder == null || paused) return;

            int next = frameIndex + 1;
            if (next >= FrameCount)
            {
                completedLoops++;
                if (!IsLooping && completedLoops >= loopCount) return; // 有限循环：停在最后一帧
                next = 0;
            }

            frameIndex = next;
            try
            {
                ShowFrame(frameIndex);
                if (FrameCount > 1) ScheduleNext();
            }
            catch (Exception ex)
            {
                CloseDecoder();
                Failed?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// 取某一帧的显示时长。GIF 的延迟写在图形控制扩展里，单位是厘秒。
        /// 元数据缺失或读不出来时退回 <see cref="DefaultFrameMs"/>。
        /// </summary>
        private int GetFrameDelayMs(int index)
        {
            try
            {
                if (decoder!.Frames[index].Metadata is BitmapMetadata metadata
                    && metadata.ContainsQuery("/grctlext/Delay"))
                {
                    int centiseconds = Convert.ToUInt16(metadata.GetQuery("/grctlext/Delay"));
                    if (centiseconds <= 0) return DefaultFrameMs;
                    return Math.Clamp(centiseconds * 10, MinFrameMs, MaxFrameMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading gif frame delay: {ex.Message}");
            }
            return DefaultFrameMs;
        }

        /// <summary>
        /// 读 NETSCAPE2.0 应用扩展里的循环次数。WIC 把整块数据挂在 /appext/Data 下，
        /// 实测字节 2..3 是小端 ushort，0 表示无限循环。
        /// </summary>
        private static int ReadLoopCount(BitmapDecoder decoder)
        {
            try
            {
                if (decoder.Metadata is BitmapMetadata metadata
                    && metadata.ContainsQuery("/appext/Data")
                    && metadata.GetQuery("/appext/Data") is byte[] data
                    && data.Length >= 4)
                {
                    return data[2] | (data[3] << 8);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading gif loop count: {ex.Message}");
            }
            return 0;
        }

        private void CloseDecoder()
        {
            decoder = null;
            FrameCount = 0;

            if (stream == null) return;
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error closing gif stream: {ex.Message}");
            }
            finally
            {
                stream = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
