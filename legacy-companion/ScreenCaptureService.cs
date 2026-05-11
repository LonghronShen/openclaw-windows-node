using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace LegacyCompanion
{
    /// <summary>
    /// Screen capture service using GDI+ and Win32 APIs.
    /// .NET Framework 2.0 compatible — no LINQ, lambdas, async, or JSON.
    /// </summary>
    public class ScreenCaptureService
    {
        // Holds monitor data during EnumDisplayMonitors callback
        private List<MonitorData> _monitorData;

        // ───────────────────── Public API ─────────────────────

        /// <summary>
        /// Capture a screenshot from the specified monitor.
        /// Returns raw byte[] of the encoded image (PNG or JPEG).
        /// Caller handles base64/JSON encoding.
        /// </summary>
        public byte[] CaptureScreenshot(int monitorIndex, string format,
            int maxWidth, int quality, bool includePointer)
        {
            EnumerateMonitors();

            if (_monitorData.Count == 0)
            {
                throw new InvalidOperationException("No screens available");
            }

            // Select target screen bounds
            int targetX = 0;
            int targetY = 0;
            int targetWidth = 0;
            int targetHeight = 0;

            if (monitorIndex >= 0 && monitorIndex < _monitorData.Count)
            {
                MonitorData md = _monitorData[monitorIndex];
                targetX = md.X;
                targetY = md.Y;
                targetWidth = md.Width;
                targetHeight = md.Height;
            }
            else
            {
                // Default to primary monitor
                bool found = false;
                for (int i = 0; i < _monitorData.Count; i++)
                {
                    MonitorData md = _monitorData[i];
                    if (md.IsPrimary)
                    {
                        targetX = md.X;
                        targetY = md.Y;
                        targetWidth = md.Width;
                        targetHeight = md.Height;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    MonitorData md = _monitorData[0];
                    targetX = md.X;
                    targetY = md.Y;
                    targetWidth = md.Width;
                    targetHeight = md.Height;
                }
            }

            int width = targetWidth;
            int height = targetHeight;

            IntPtr hdcScreen = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            Bitmap bitmap = null;
            Bitmap scaledBitmap = null;

            try
            {
                hdcScreen = GetDC(IntPtr.Zero);
                hdcMem = CreateCompatibleDC(hdcScreen);
                hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
                hOld = SelectObject(hdcMem, hBitmap);

                BitBlt(hdcMem, 0, 0, width, height, hdcScreen,
                    targetX, targetY, SRCCOPY);

                // Optionally overlay mouse cursor
                if (includePointer)
                {
                    DrawCursor(hdcMem, targetX, targetY, width, height);
                }

                SelectObject(hdcMem, hOld);
                hOld = IntPtr.Zero; // Mark as restored

                // Create managed Bitmap from GDI handle
                bitmap = Image.FromHbitmap(hBitmap);

                Bitmap finalBitmap = bitmap;

                // Scale down if wider than maxWidth (preserve aspect ratio)
                if (width > maxWidth)
                {
                    double scale = (double)maxWidth / (double)width;
                    int newWidth = maxWidth;
                    int newHeight = (int)((double)height * scale);

                    scaledBitmap = new Bitmap(bitmap, new Size(newWidth, newHeight));
                    finalBitmap = scaledBitmap;
                    width = newWidth;
                    height = newHeight;
                }

                // Determine image format
                bool isJpeg = (string.Compare(format, "jpeg", true) == 0)
                           || (string.Compare(format, "jpg", true) == 0);

                // Encode to byte[]
                byte[] resultBytes;
                using (MemoryStream ms = new MemoryStream())
                {
                    if (isJpeg)
                    {
                        ImageCodecInfo encoder = GetEncoder(ImageFormat.Jpeg);
                        if (encoder != null)
                        {
                            using (EncoderParameters encoderParams = new EncoderParameters(1))
                            {
                                encoderParams.Param[0] =
                                    new EncoderParameter(Encoder.Quality, (long)quality);
                                finalBitmap.Save(ms, encoder, encoderParams);
                            }
                        }
                        else
                        {
                            finalBitmap.Save(ms, ImageFormat.Jpeg);
                        }
                    }
                    else
                    {
                        finalBitmap.Save(ms, ImageFormat.Png);
                    }

                    resultBytes = ms.ToArray();
                }

                return resultBytes;
            }
            finally
            {
                // Always clean up GDI+ and Win32 resources
                if (scaledBitmap != null) scaledBitmap.Dispose();
                if (bitmap != null) bitmap.Dispose();

                if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
                    SelectObject(hdcMem, hOld);
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (hdcMem != IntPtr.Zero)
                    DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        /// <summary>
        /// Returns the number of detected monitors (displays).
        /// </summary>
        public int GetMonitorCount()
        {
            EnumerateMonitors();
            return _monitorData.Count;
        }

        // ───────────────────── Monitor Enumeration ─────────────────────

        private void EnumerateMonitors()
        {
            _monitorData = new List<MonitorData>();
            MonitorEnumProc enumProc = new MonitorEnumProc(MonitorEnumCallback);
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, enumProc, IntPtr.Zero);
        }

        private bool MonitorEnumCallback(IntPtr hMonitor, IntPtr hdcMonitor,
            ref RECT lprcMonitor, IntPtr dwData)
        {
            MONITORINFOEX info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));

            if (GetMonitorInfo(hMonitor, ref info))
            {
                MonitorData md = new MonitorData();
                md.X = info.rcMonitor.left;
                md.Y = info.rcMonitor.top;
                md.Width = info.rcMonitor.right - info.rcMonitor.left;
                md.Height = info.rcMonitor.bottom - info.rcMonitor.top;
                md.IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                md.DeviceName = info.szDevice;
                _monitorData.Add(md);
            }

            return true; // Continue enumeration
        }

        // ───────────────────── Cursor Drawing ─────────────────────

        private static void DrawCursor(IntPtr hdc, int boundsX, int boundsY,
            int boundsWidth, int boundsHeight)
        {
            try
            {
                CURSORINFO cursorInfo = new CURSORINFO();
                cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));

                if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    IntPtr iconHandle = CopyIcon(cursorInfo.hCursor);
                    if (iconHandle != IntPtr.Zero)
                    {
                        try
                        {
                            ICONINFO iconInfo = new ICONINFO();
                            if (GetIconInfo(iconHandle, out iconInfo))
                            {
                                int x = cursorInfo.ptScreenPos.x - boundsX - iconInfo.xHotspot;
                                int y = cursorInfo.ptScreenPos.y - boundsY - iconInfo.yHotspot;

                                DrawIconEx(hdc, x, y, iconHandle, 0, 0, 0,
                                    IntPtr.Zero, DI_NORMAL);

                                if (iconInfo.hbmMask != IntPtr.Zero)
                                    DeleteObject(iconInfo.hbmMask);
                                if (iconInfo.hbmColor != IntPtr.Zero)
                                    DeleteObject(iconInfo.hbmColor);
                            }
                        }
                        finally
                        {
                            DestroyIcon(iconHandle);
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore cursor drawing failures
            }
        }

        // ───────────────────── Image Codec Helpers ─────────────────────

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == format.Guid)
                {
                    return codecs[i];
                }
            }
            return null;
        }

        // ───────────────────── Win32 P/Invoke Declarations ─────────────────────

        private const int CURSOR_SHOWING = 1;
        private const int SRCCOPY = 0x00CC0020;
        private const int MONITORINFOF_PRIMARY = 1;
        private const int DI_NORMAL = 0x0003;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

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
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
            ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
            MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
            int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop,
            IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur,
            IntPtr hbrFlickerFreeDraw, int diFlags);

        // ───────────────────── Internal Data Holder ─────────────────────

        /// <summary>
        /// Internal monitor data used during enumeration.
        /// </summary>
        private class MonitorData
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public bool IsPrimary;
            public string DeviceName;
        }
    }

    // ───────────────────── Public Data Class ─────────────────────

    /// <summary>
    /// Public information about a detected screen/monitor.
    /// Simple data holder — just public fields.
    /// </summary>
    public class ScreenInfo
    {
        public int Width;
        public int Height;
        public bool IsPrimary;
        public string DeviceName;
    }
}
