using System;

namespace TS3ScreenShare.Models
{
    public enum CaptureSourceType { FullScreen, Window }

    public sealed class CaptureSource
    {
        public string Name { get; init; } = "";
        public CaptureSourceType Type { get; init; }
        public System.Drawing.Rectangle Bounds { get; init; }
        public IntPtr Hwnd { get; init; }

        public override string ToString() => Name;
    }
}
