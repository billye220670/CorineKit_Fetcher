using System;

namespace ImageSelector
{
    /// <summary>
    /// 画布（图片）的缩放/平移状态，以及它的纯数学运算。
    ///
    /// 坐标约定：图片以"适配窗口"的尺寸做布局，RenderTransform 叠加其上，且
    /// RenderTransformOrigin = 0,0，因此
    ///     容器坐标 = 布局原点 + 布局坐标 * Scale + Offset
    /// Scale = 1 表示恰好适配窗口，与 XAML 中 ScaleTransform → TranslateTransform
    /// 的顺序严格对应（先缩放后平移）。
    ///
    /// 单独成类是因为缩放锚点公式最容易写错又最难肉眼排查，抽成无 WPF 依赖的
    /// 纯计算后可以独立验证"指针压住的图像点缩放前后不动"这一不变量。
    /// </summary>
    public struct CanvasViewState : IEquatable<CanvasViewState>
    {
        /// <summary>相对"适配窗口"的最小缩放：fit 即下限，不再往更小缩。</summary>
        public const double MinScale = 1.0;

        /// <summary>
        /// 缩小到该倍率以下才开始把平移量往 fit 收敛。
        /// 高于它时缩小保持纯指针锚点手感（和主流看图软件一致），
        /// 只有临近 fit 的最后一段才"渐渐退回居中"，到 1 时恰好落回适配。
        /// </summary>
        public const double FitEaseScale = 2.0;

        /// <summary>相对"适配窗口"的最大缩放。</summary>
        public const double MaxScale = 40.0;

        public double Scale;
        public double OffsetX;
        public double OffsetY;

        /// <summary>适配窗口的初始状态。</summary>
        public static CanvasViewState Fit => new CanvasViewState { Scale = 1.0, OffsetX = 0, OffsetY = 0 };

        /// <summary>
        /// 画布运算需要的布局信息：图片布局尺寸、它在容器中的原点、以及容器尺寸。
        /// </summary>
        public struct Layout
        {
            public double ImageWidth;
            public double ImageHeight;
            public double OriginX;
            public double OriginY;
            public double ViewWidth;
            public double ViewHeight;
        }

        /// <summary>
        /// 滚轮增量换算成缩放倍率。一格(±120)约 1.24 倍，
        /// 指数曲线同时兼容高精度滚轮的小步长，不会出现跳变。
        /// </summary>
        public static double WheelToFactor(int wheelDelta)
        {
            return Math.Pow(1.0018, wheelDelta);
        }

        /// <summary>
        /// 以鼠标位置为锚点缩放：让指针压住的那个图像点在缩放前后停在原处，
        /// 也就是"往哪里指就往哪里放大"，与主流看图软件一致。
        /// 结果会被夹在 [MinScale, MaxScale] 内；缩放没变化时原样返回。
        /// 缩小时只有落到 FitEaseScale 以下才把平移量按比例往 fit(0,0) 收敛，
        /// 之前完全保持锚点手感；落到 Scale=1 时恰好回到居中适配。
        /// </summary>
        public CanvasViewState ZoomAt(Layout layout, double mouseX, double mouseY, double factor)
        {
            double newScale = Math.Min(MaxScale, Math.Max(MinScale, Scale * factor));
            if (Math.Abs(newScale - Scale) < 1e-9) return this;

            double ratio = newScale / Scale;

            // u = 鼠标相对图片布局原点的偏移。
            // 要求 u = ratio * (u - offset) + offset' 恒成立，解出 offset'：
            double ux = mouseX - layout.OriginX;
            double uy = mouseY - layout.OriginY;

            double offsetX = ux - ratio * (ux - OffsetX);
            double offsetY = uy - ratio * (uy - OffsetY);

            if (ratio < 1 && newScale < FitEaseScale)
            {
                // k 在 FitEaseScale 处为 1（与纯锚点段无缝衔接），在 fit 处为 0
                double k = (newScale - MinScale) / (FitEaseScale - MinScale);
                offsetX *= k;
                offsetY *= k;
            }

            return new CanvasViewState
            {
                Scale = newScale,
                OffsetX = offsetX,
                OffsetY = offsetY
            };
        }

        /// <summary>
        /// 按鼠标位移平移。
        /// </summary>
        public CanvasViewState PanBy(double deltaX, double deltaY)
        {
            return new CanvasViewState
            {
                Scale = Scale,
                OffsetX = OffsetX + deltaX,
                OffsetY = OffsetY + deltaY
            };
        }

        /// <summary>
        /// 限制平移范围，避免把图片拖到完全看不见找不回来：
        /// 变换后的图片包围盒必须与容器保留至少 edgeMargin 的重叠。
        /// </summary>
        public CanvasViewState Clamp(Layout layout, double edgeMargin)
        {
            if (layout.ImageWidth <= 0 || layout.ImageHeight <= 0) return this;
            if (layout.ViewWidth <= 0 || layout.ViewHeight <= 0) return this;

            double scaledW = layout.ImageWidth * Scale;
            double scaledH = layout.ImageHeight * Scale;

            // 右边缘不得缩到容器左边界内侧 edgeMargin 以左
            double minX = edgeMargin - layout.OriginX - scaledW;
            // 左边缘不得越过容器右边界内侧 edgeMargin
            double maxX = layout.ViewWidth - edgeMargin - layout.OriginX;
            double minY = edgeMargin - layout.OriginY - scaledH;
            double maxY = layout.ViewHeight - edgeMargin - layout.OriginY;

            // 图片比容器还小时区间可能反向，退化成居中
            if (minX > maxX) { double mid = (minX + maxX) / 2; minX = maxX = mid; }
            if (minY > maxY) { double mid = (minY + maxY) / 2; minY = maxY = mid; }

            return new CanvasViewState
            {
                Scale = Scale,
                OffsetX = Math.Min(maxX, Math.Max(minX, OffsetX)),
                OffsetY = Math.Min(maxY, Math.Max(minY, OffsetY))
            };
        }

        /// <summary>
        /// 把布局坐标 (lx, ly) 映射到容器坐标，用于验证缩放锚点不变量。
        /// </summary>
        public void ToContainer(Layout layout, double lx, double ly, out double cx, out double cy)
        {
            cx = layout.OriginX + lx * Scale + OffsetX;
            cy = layout.OriginY + ly * Scale + OffsetY;
        }

        /// <summary>
        /// 把容器坐标反算回布局坐标，即鼠标当前压住的是图片上的哪个点。
        /// </summary>
        public void ToLayout(Layout layout, double cx, double cy, out double lx, out double ly)
        {
            lx = (cx - layout.OriginX - OffsetX) / Scale;
            ly = (cy - layout.OriginY - OffsetY) / Scale;
        }

        public bool Equals(CanvasViewState other)
        {
            return Math.Abs(Scale - other.Scale) < 1e-9
                && Math.Abs(OffsetX - other.OffsetX) < 1e-9
                && Math.Abs(OffsetY - other.OffsetY) < 1e-9;
        }

        public override bool Equals(object? obj) => obj is CanvasViewState other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Scale, OffsetX, OffsetY);

        public override string ToString() => $"scale={Scale:F4} offset=({OffsetX:F2}, {OffsetY:F2})";
    }
}
