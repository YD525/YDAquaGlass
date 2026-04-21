using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace WaterTest.UI
{
    public sealed class DxgiCapture : IDisposable
    {
        // ── Win32 API ─────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(
            IntPtr hWnd, uint dwAffinity);

        private const uint WdaExcludeFromCapture = 0x00000011;
        private const uint WdaNone = 0x00000000;

        // ── Fields ────────────────────────────────────────────────
        private readonly Device Device;
        private readonly OutputDuplication Duplication;
        private readonly Texture2D StagingTexture;
        private readonly int Width;
        private readonly int Height;

        private bool FrameAcquired = false;
        private bool StagingHasContent = false;
        private bool Disposed = false;

        // ── Constructor (initialize once and reuse) ───────────────
        public DxgiCapture(int monitorIndex = 0)
        {
            Device = new Device(
                SharpDX.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.None);

            SharpDX.DXGI.Device dxgiDevice = null;
            Adapter adapter = null;
            Output output = null;
            Output1 output1 = null;

            try
            {
                dxgiDevice = Device.QueryInterface<SharpDX.DXGI.Device>();
                adapter = dxgiDevice.Adapter;
                output = adapter.GetOutput(monitorIndex);
                output1 = output.QueryInterface<Output1>();

                var desc = output.Description;
                Width = desc.DesktopBounds.Right - desc.DesktopBounds.Left;
                Height = desc.DesktopBounds.Bottom - desc.DesktopBounds.Top;

                Duplication = output1.DuplicateOutput(Device);
            }
            finally
            {
                output1?.Dispose();
                output?.Dispose();
                adapter?.Dispose();
                dxgiDevice?.Dispose();
            }

            StagingTexture = new Texture2D(Device, new Texture2DDescription
            {
                Width = Width,
                Height = Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            });
        }

        // ── Capture desktop frame ────────────────────────────────
        /// <summary>
        /// If a window is provided, it will be excluded from capture.
        /// This only needs to be set once and remains active.
        /// </summary>
        public BitmapSource Take(Window window = null, int timeoutMs = 500)
        {
            IntPtr hwnd = IntPtr.Zero;

            if (window != null)
            {
                hwnd = new WindowInteropHelper(window).Handle;
            }

            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);

            AcquireFrame(0);
            AcquireFrame(0);

            BitmapSource source = AcquireFrame(timeoutMs);

            SetWindowDisplayAffinity(hwnd, WdaNone);

            return source;
        }

        // ── Restore window capture visibility ─────────────────────
        public void RestoreWindow(Window window)
        {
            if (window == null) return;

            IntPtr hwnd = new System.Windows.Interop
                .WindowInteropHelper(window).Handle;

            SetWindowDisplayAffinity(hwnd, WdaNone);
        }

        // ── Save captured frame as JPG ────────────────────────────
        public void SaveToJpg(
            string fileName,
            Window window = null,
            int timeoutMs = 500)
        {
            BitmapSource bmp = Take(window, timeoutMs);
            if (bmp == null) return;

            string path = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                fileName);

            using (var fs = new System.IO.FileStream(
                path,
                System.IO.FileMode.Create))
            {
                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 95
                };

                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(fs);
            }
        }

        // ── Core frame acquisition logic ─────────────────────────
        private BitmapSource AcquireFrame(int timeoutMs)
        {
            // Release previous frame before acquiring a new one
            if (FrameAcquired)
            {
                try
                {
                    Duplication.ReleaseFrame();
                }
                catch
                {
                }

                FrameAcquired = false;
            }

            SharpDX.DXGI.Resource desktopResource = null;

            try
            {
                SharpDX.DXGI.OutputDuplicateFrameInformation frameInfo;

                var result = Duplication.TryAcquireNextFrame(
                    timeoutMs,
                    out frameInfo,
                    out desktopResource);

                // If timeout occurs, return previous cached frame
                if (result == SharpDX.DXGI.ResultCode.WaitTimeout ||
                    desktopResource == null)
                {
                    return StagingHasContent ? MapToBitmap() : null;
                }

                FrameAcquired = true;

                Texture2D desktopTexture = null;

                try
                {
                    desktopTexture =
                        desktopResource.QueryInterface<Texture2D>();

                    Device.ImmediateContext.CopyResource(
                        desktopTexture,
                        StagingTexture);
                }
                finally
                {
                    desktopTexture?.Dispose();
                }

                StagingHasContent = true;

                return MapToBitmap();
            }
            finally
            {
                desktopResource?.Dispose();
            }
        }

        // ── Convert GPU texture to BitmapSource ──────────────────
        private BitmapSource MapToBitmap()
        {
            var context = Device.ImmediateContext;

            var mapped = context.MapSubresource(
                StagingTexture,
                0,
                MapMode.Read,
                MapFlags.None);

            try
            {
                var bmp = BitmapSource.Create(
                    Width,
                    Height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    mapped.DataPointer,
                    mapped.RowPitch * Height,
                    mapped.RowPitch);

                bmp.Freeze();
                return bmp;
            }
            finally
            {
                context.UnmapSubresource(StagingTexture, 0);
            }
        }

        // ── Dispose resources ────────────────────────────────────
        public void Dispose()
        {
            if (Disposed) return;

            Disposed = true;

            if (FrameAcquired)
            {
                try
                {
                    Duplication.ReleaseFrame();
                }
                catch
                {
                }
            }

            StagingTexture?.Dispose();
            Duplication?.Dispose();
            Device?.Dispose();
        }
    }
}