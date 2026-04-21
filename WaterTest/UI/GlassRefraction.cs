using System;
using Microsoft.Win32;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WaterTest.UI
{
    public sealed class GlassMaterial : IDisposable
    {
        public int TargetFps { get; set; } = 10;
        public double ScaleFactor { get; set; } = 1.0;

        public Func<WriteableBitmap, WriteableBitmap> OnFrameCaptured;

        public bool IsRunning { get; private set; }
        public int LastFps { get; private set; }

        private readonly FrameworkElement Target;
        private readonly Window ParentWindow;

        private ImageBrush OutputBrush;
        private DxgiCapture Dxgi;

        private long LastTick;
        private int FrameCount;
        private long FpsTimer;
        private bool Disposed;

        private double DpiScaleX = 1.0;
        private double DpiScaleY = 1.0;

        public Window Win;

        public GlassMaterial(FrameworkElement target)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));

            if (!(Target is System.Windows.Controls.Grid) &&
                !(Target is System.Windows.Controls.Border))
            {
                throw new ArgumentException("Target must be Grid or Border");
            }

            ParentWindow = Window.GetWindow(Target)
                ?? throw new InvalidOperationException("Target must be inside Window");

            OutputBrush = new ImageBrush();
            ApplyBackground();
            UpdateDpiFromWindow();
            Start();
        }

        ~GlassMaterial()
        {
            Dispose();
        }

        private void UpdateDpiFromWindow()
        {
            if (ParentWindow == null) return;

            var dpi = VisualTreeHelper.GetDpi(ParentWindow);
            DpiScaleX = dpi.DpiScaleX;
            DpiScaleY = dpi.DpiScaleY;
        }

        private void Start()
        {
            if (IsRunning) return;

            RefreshDpiCache();

            if (Dxgi == null)
            {
                Dxgi = new DxgiCapture();
            }

            ParentWindow.DpiChanged += OnDpiChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            IsRunning = true;
            CompositionTarget.Rendering += OnRendering;
        }

        public void Stop()
        {
            if (!IsRunning) return;

            ParentWindow.DpiChanged -= OnDpiChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            CompositionTarget.Rendering -= OnRendering;
            IsRunning = false;
        }

        private void OnDpiChanged(object sender, DpiChangedEventArgs e)
        {
            DpiScaleX = e.NewDpi.DpiScaleX;
            DpiScaleY = e.NewDpi.DpiScaleY;
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            ParentWindow.Dispatcher.BeginInvoke(new Action(RefreshDpiCache));
        }

        private void RefreshDpiCache()
        {
            var src = PresentationSource.FromVisual(Target);
            if (src?.CompositionTarget == null) return;

            DpiScaleX = src.CompositionTarget.TransformToDevice.M11;
            DpiScaleY = src.CompositionTarget.TransformToDevice.M22;
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (Disposed || Dxgi == null || Win == null)
                return;

            long now = DateTime.UtcNow.Ticks;

            long minInterval = TargetFps > 0
                ? (long)(TimeSpan.TicksPerSecond / TargetFps)
                : 0;

            if (minInterval > 0 && (now - LastTick) < minInterval)
                return;

            LastTick = now;

            if (Target.ActualWidth < 1 || Target.ActualHeight < 1)
                return;

            var bitmap = CaptureFromDxgi();
            if (bitmap == null)
                return;

            var final = OnFrameCaptured?.Invoke(bitmap) ?? bitmap;

            OutputBrush.ImageSource = final;
            ApplyBackground();

            FrameCount++;
            if (now - FpsTimer >= TimeSpan.TicksPerSecond)
            {
                LastFps = FrameCount;
                FrameCount = 0;
                FpsTimer = now;
            }
        }

        private WriteableBitmap CaptureFromDxgi()
        {
            var full = Dxgi.Take(Win);
            if (full == null) return null;

            int winX = (int)(Win.Left * DpiScaleX);
            int winY = (int)(Win.Top * DpiScaleY);

            var pt = Target.TransformToAncestor(Win)
                           .Transform(new Point(0, 0));

            int x = winX + (int)(pt.X * DpiScaleX);
            int y = winY + (int)(pt.Y * DpiScaleY);

            int w = (int)(Target.ActualWidth * DpiScaleX * ScaleFactor);
            int h = (int)(Target.ActualHeight * DpiScaleY * ScaleFactor);

            if (w <= 0 || h <= 0) return null;

            x = Math.Max(0, x);
            y = Math.Max(0, y);
            w = Math.Min(w, full.PixelWidth - x);
            h = Math.Min(h, full.PixelHeight - y);

            if (w <= 0 || h <= 0) return null;

            var cropped = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
            return new WriteableBitmap(cropped);
        }

        public static class Win32Rect
        {
            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }
        }

        private void ApplyBackground()
        {
            if (Target is System.Windows.Controls.Grid grid)
                grid.Background = OutputBrush;
            else if (Target is System.Windows.Controls.Border border)
                border.Background = OutputBrush;
        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;

            Stop();

            Dxgi?.Dispose();
            Dxgi = null;

            if (Target is System.Windows.Controls.Grid grid)
                grid.Background = null;
            else if (Target is System.Windows.Controls.Border border)
                border.Background = null;

            OutputBrush = null;
        }
    }
}


public class GlassRefraction
{
    private static readonly double[] SinLUT = new double[3600];
    private static readonly double[] CosLUT = new double[3600];
    private static bool _init;

    private static void InitLUT()
    {
        if (_init) return;

        for (int i = 0; i < 3600; i++)
        {
            double a = i * 0.00174532925; // 2π / 3600
            SinLUT[i] = Math.Sin(a);
            CosLUT[i] = Math.Cos(a);
        }

        _init = true;
    }

    private static double Sin(double v)
    {
        int i = (int)(v * 573.0) % 3600;
        if (i < 0) i += 3600;
        return SinLUT[i];
    }

    private static double Cos(double v)
    {
        int i = (int)(v * 573.0) % 3600;
        if (i < 0) i += 3600;
        return CosLUT[i];
    }

    public static WriteableBitmap Refraction(
        WriteableBitmap Bitmap,
        double Intensity = 15,
        double Scale = 3,
        double Time = 0)
    {
        if (Bitmap == null) return null;

        InitLUT();

        int width = Bitmap.PixelWidth;
        int height = Bitmap.PixelHeight;
        int stride = width * 4;

        WriteableBitmap result =
            new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);

        result.Lock();
        Bitmap.Lock();

        try
        {
            unsafe
            {
                byte* src = (byte*)Bitmap.BackBuffer;
                byte* dst = (byte*)result.BackBuffer;

                double invW = 1.0 / width;
                double invH = 1.0 / height;
                double t = Time;

                const double PI2 = 6.28318530718;

                for (int y = 0; y < height; y++)
                {
                    double ny = y * invH * Scale * PI2;

                    double sinNy = Sin(ny + t);
                    double cosNy = Cos(ny + t);

                    int yBase = y * stride;

                    for (int x = 0; x < width; x++)
                    {
                        double nx = x * invW * Scale * PI2;

                        double sinNx = Sin(nx * 0.5 + t);
                        double cosNx = Cos(nx + t);

                        double noise = Sin(nx + ny + t) * 0.5;

                        double flowX = sinNy * cosNx;
                        double flowY = cosNy * sinNx;

                        double offsetX = (flowX + noise) * Intensity;
                        double offsetY = (flowY + noise) * Intensity;

                        int srcX = x + (int)offsetX;
                        int srcY = y + (int)offsetY;

                        srcX = Clamp(srcX, 0, width - 1);
                        srcY = Clamp(srcY, 0, height - 1);

                        int si = srcY * stride + srcX * 4;
                        int di = yBase + x * 4;

                        dst[di] = src[si];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        dst[di + 3] = src[si + 3];
                    }
                }
            }

            result.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            Bitmap.Unlock();
            result.Unlock();
        }

        return result;
    }

    private static int Clamp(int v, int min, int max)
    {
        if (v < min) return min;
        if (v > max) return max;
        return v;
    }
}

