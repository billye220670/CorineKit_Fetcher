using System;
using System.Collections.Generic;

namespace ImageSelector
{
    /// <summary>
    /// 单个视口的画布动作录制/回放状态（无 WPF 依赖）。
    ///
    /// 录制以"录制起点 = (0,0)"记录归一化位移；松手后的循环时间轴是：
    ///   [fade: end → start] + [clip: start → end]
    /// 播放从 phase 0（end）开始，所以松手瞬间不会跳；每次循环接缝都由同一段
    /// fade 把 end 拉回 start。偏移按视口高度归一化，渲染时再乘回当前高度。
    /// </summary>
    public sealed class CanvasMotionPlayer
    {
        private const double FadeRatio = 0.15;
        private const double MinFadeSeconds = 0.15;
        private const double MaxFadeSeconds = 0.60;
        private const double MaxFrameSeconds = 0.10;
        private const double Epsilon = 1e-9;

        private struct Sample
        {
            public double Time;
            public double X;
            public double Y;
        }

        private readonly List<Sample> samples = new List<Sample>();
        private double clipDuration;
        private double fadeDuration;
        private double endX;
        private double endY;
        private double phase;
        private double currentX;
        private double currentY;

        private bool hasClip;
        private bool isRecording;
        private bool isPlaying;
        private bool isPaused;
        private bool isStopped;

        public bool HasClip => hasClip;
        public bool IsRecording => isRecording;
        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;
        public bool IsStopped => isStopped;
        public bool HasOffset => Math.Abs(currentX) > Epsilon || Math.Abs(currentY) > Epsilon;
        public double ClipDuration => clipDuration;
        public double FadeDuration => fadeDuration;

        /// <summary>开始一段新录制；旧 clip 立即丢弃，offset 归零。</summary>
        public void BeginRecording()
        {
            samples.Clear();
            samples.Add(new Sample { Time = 0, X = 0, Y = 0 });
            clipDuration = 0;
            fadeDuration = 0;
            endX = endY = 0;
            phase = 0;
            currentX = currentY = 0;
            hasClip = false;
            isRecording = true;
            isPlaying = false;
            isPaused = false;
            isStopped = false;
        }

        /// <summary>追加一个归一化位移样本；time 为相对录制开始的秒数。</summary>
        public void AddSample(double time, double x, double y)
        {
            if (!isRecording) return;

            double t = Math.Max(0, time);
            if (samples.Count > 0 && t <= samples[samples.Count - 1].Time)
            {
                // 同一时刻的鼠标事件很少见；保留时间递增，避免回放时除以 0。
                t = samples[samples.Count - 1].Time + 0.001;
            }

            samples.Add(new Sample { Time = t, X = x, Y = y });
            currentX = x;
            currentY = y;
        }

        /// <summary>结束录制并开始播放。播放从 end→start 的 fade 开始。</summary>
        public void EndRecording()
        {
            if (!isRecording) return;
            isRecording = false;

            if (samples.Count < 2)
            {
                samples.Clear();
                hasClip = false;
                isPlaying = false;
                isPaused = false;
                isStopped = false;
                currentX = currentY = 0;
                return;
            }

            clipDuration = samples[samples.Count - 1].Time;
            if (clipDuration <= 0.01)
            {
                samples.Clear();
                hasClip = false;
                isPlaying = false;
                isPaused = false;
                isStopped = false;
                currentX = currentY = 0;
                return;
            }

            endX = samples[samples.Count - 1].X;
            endY = samples[samples.Count - 1].Y;
            fadeDuration = Math.Clamp(clipDuration * FadeRatio, MinFadeSeconds, MaxFadeSeconds);
            phase = 0;
            currentX = endX;
            currentY = endY;
            hasClip = true;
            isPlaying = true;
            isPaused = false;
            isStopped = false;
        }

        /// <summary>取消正在录制的片段；旧 clip 已被 BeginRecording 丢弃。</summary>
        public void CancelRecording()
        {
            samples.Clear();
            isRecording = false;
            hasClip = false;
            isPlaying = false;
            isPaused = false;
            isStopped = false;
            currentX = currentY = 0;
        }

        public void Pause()
        {
            if (!hasClip || isStopped || !isPlaying) return;
            isPlaying = false;
            isPaused = true;
        }

        public void Resume()
        {
            if (!hasClip || isStopped || !isPaused) return;
            isPlaying = true;
            isPaused = false;
        }

        /// <summary>停止并冻结在当前 offset；clip 保留，后续不会自己恢复。</summary>
        public void Stop()
        {
            if (!hasClip) return;
            isPlaying = false;
            isPaused = false;
            isStopped = true;
        }

        /// <summary>F：停播、offset 归零；clip 保留但不再播放。</summary>
        public void FitReset()
        {
            isPlaying = false;
            isPaused = false;
            isStopped = hasClip;
            currentX = currentY = 0;
            phase = 0;
        }

        public void Reset()
        {
            samples.Clear();
            clipDuration = 0;
            fadeDuration = 0;
            endX = endY = 0;
            phase = 0;
            currentX = currentY = 0;
            hasClip = false;
            isRecording = false;
            isPlaying = false;
            isPaused = false;
            isStopped = false;
        }

        /// <summary>推进播放；dt 单位秒。暂停/停止/录制态自动空操作。</summary>
        public void Update(double dt)
        {
            if (!isPlaying || !hasClip) return;

            if (double.IsNaN(dt) || dt <= 0) return;
            dt = Math.Min(dt, MaxFrameSeconds);
            phase += dt;

            double total = fadeDuration + clipDuration;
            if (total <= 0)
            {
                Stop();
                return;
            }

            phase %= total;
            if (phase < 0) phase += total;

            if (phase < fadeDuration)
            {
                double k = SmoothStep(phase / fadeDuration);
                currentX = Lerp(endX, 0, k);
                currentY = Lerp(endY, 0, k);
            }
            else
            {
                SampleAt(phase - fadeDuration);
            }
        }

        /// <summary>当前 offset（归一化）乘以视口高度，得到屏幕像素。</summary>
        public void GetOffsetPixels(double viewportHeight, out double x, out double y)
        {
            double height = Math.Max(1, viewportHeight);
            x = currentX * height;
            y = currentY * height;
        }

        private void SampleAt(double time)
        {
            if (samples.Count == 0)
            {
                currentX = currentY = 0;
                return;
            }

            if (time <= samples[0].Time)
            {
                currentX = samples[0].X;
                currentY = samples[0].Y;
                return;
            }

            if (time >= samples[samples.Count - 1].Time)
            {
                currentX = endX;
                currentY = endY;
                return;
            }

            int lo = 0;
            int hi = samples.Count - 1;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) / 2;
                if (samples[mid].Time <= time) lo = mid;
                else hi = mid;
            }

            Sample a = samples[lo];
            Sample b = samples[hi];
            double span = b.Time - a.Time;
            double k = span > Epsilon ? (time - a.Time) / span : 0;
            currentX = Lerp(a.X, b.X, k);
            currentY = Lerp(a.Y, b.Y, k);
        }

        private static double Lerp(double a, double b, double k)
        {
            return a + (b - a) * k;
        }

        private static double SmoothStep(double k)
        {
            k = Math.Clamp(k, 0, 1);
            return k * k * (3 - 2 * k);
        }
    }
}