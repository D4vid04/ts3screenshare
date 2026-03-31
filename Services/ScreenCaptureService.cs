using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using TS3ScreenShare.Models;

namespace TS3ScreenShare.Services
{
    public sealed class ScreenCaptureService : IDisposable
    {
        private System.Threading.Timer? _timer;
        private bool _capturing;
        private CaptureSource? _source;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public event Action<int, int, byte[]>? FrameCaptured;

        public void Start(int fps, CaptureSource source)
        {
            _source = source;

            if (source.Type == CaptureSourceType.FullScreen)
            {
                Width = source.Bounds.Width;
                Height = source.Bounds.Height;
            }
            else
            {
                GetWindowRect(source.Hwnd, out var r);
                Width = Math.Max(1, r.Right - r.Left);
                Height = Math.Max(1, r.Bottom - r.Top);
            }

            _capturing = true;
            var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
            _timer = new System.Threading.Timer(_ => Capture(), null, TimeSpan.Zero, interval);
        }

        public void Stop()
        {
            _capturing = false;
            _timer?.Dispose();
            _timer = null;
            _source = null;
        }

        private void Capture()
        {
            if (!_capturing || _source == null) return;
            try
            {
                Rectangle bounds;

                if (_source.Type == CaptureSourceType.FullScreen)
                {
                    bounds = _source.Bounds;
                }
                else
                {
                    GetWindowRect(_source.Hwnd, out var r);
                    bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    if (bounds.Width <= 0 || bounds.Height <= 0) return;

                    if (bounds.Width != Width || bounds.Height != Height)
                    {
                        Width = bounds.Width;
                        Height = bounds.Height;
                    }
                }

                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);

                DrawCursorOnBitmap(g, bounds);

                var rect = new Rectangle(0, 0, bounds.Width, bounds.Height);
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    var bytes = new byte[stride * bounds.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    FrameCaptured?.Invoke(bounds.Width, bounds.Height, bytes);
                }
                finally { bmp.UnlockBits(data); }
            }
            catch { }
        }

        private static void DrawCursorOnBitmap(Graphics g, Rectangle bounds)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref ci)) return;
            if (ci.flags != CURSOR_SHOWING) return;

            // Hotspot offset so the cursor tip lands on the correct pixel
            if (!GetIconInfo(ci.hCursor, out var ii)) return;
            int x = ci.ptScreenPos.x - bounds.Left - (int)ii.xHotspot;
            int y = ci.ptScreenPos.y - bounds.Top  - (int)ii.yHotspot;

            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);

            DrawIconEx(g.GetHdc(), x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            g.ReleaseHdc();
        }

        // ── Enumerate sources ─────────────────────────────────────────────────

        public static List<CaptureSource> GetSources()
        {
            var sources = new List<CaptureSource>();

            // Monitors
            int i = 1;
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                sources.Add(new CaptureSource
                {
                    Name = $"Monitor {i++}{(screen.Primary ? " (primary)" : "")}",
                    Type = CaptureSourceType.FullScreen,
                    Bounds = screen.Bounds
                });
            }

            // Windows
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                var title = sb.ToString().Trim();
                if (string.IsNullOrEmpty(title)) return true;
                if (title.Length < 2) return true;

                sources.Add(new CaptureSource
                {
                    Name = title.Length > 60 ? title[..60] + "…" : title,
                    Type = CaptureSourceType.Window,
                    Hwnd = hwnd
                });
                return true;
            }, IntPtr.Zero);

            return sources;
        }

        // ── P/Invoke ──────────────────────────────────────────────────────────

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop,
            IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
        [DllImport("gdi32.dll")]  private static extern bool DeleteObject(IntPtr hObject);

        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public uint xHotspot;
            public uint yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        public void Dispose() => Stop();
    }
}
