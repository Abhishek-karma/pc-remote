// GDI screen capture (BitBlt) of the whole virtual desktop into a BGRA buffer.
//
// This runs inside the user-session agent (PCRemoteSession.exe), which is why it
// can capture the real interactive desktop at all — a session-0 service cannot.
// It needs no GPU and works headless/on VMs; the Desktop Duplication (DXGI) path
// with dirty/move rects is the later optimization. The process should be DPI-aware
// so GetSystemMetrics returns physical pixels that match the coordinate space.

using System.Runtime.InteropServices;

namespace PcRemote.Session.Streaming;

/// <summary>
/// Captures the desktop into a fixed-size BGRA32 buffer via GDI BitBlt.
/// Rows are top-down (GetDIBits with a negative biHeight), matching the row order
/// the H.264 encoder expects. Not thread-safe: capture on one thread.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private bool _disposed;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;
    private readonly int _x, _y;            // virtual-screen origin (can be negative)
    private readonly int _width, _height;
    private readonly byte[] _buffer;

    public int Width => _width;
    public int Height => _height;
    public int Stride => _width * 4;

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    public ScreenCapture()
    {
        _x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        _width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        _height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (_width <= 0 || _height <= 0)
            throw new InvalidOperationException("ScreenCapture: empty virtual desktop");

        _screenDc = GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
            throw new InvalidOperationException("ScreenCapture: GetDC failed");
        _memDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateCompatibleBitmap(_screenDc, _width, _height);
        _oldBitmap = SelectObject(_memDc, _bitmap);
        _buffer = new byte[checked(_width * _height * 4)];
    }

    /// <summary>Captures the current desktop into the internal buffer and returns it.</summary>
    public byte[] Capture()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ScreenCapture));

        if (!BitBlt(_memDc, 0, 0, _width, _height, _screenDc, _x, _y, SRCCOPY | CAPTUREBLT))
            throw new InvalidOperationException($"ScreenCapture: BitBlt failed ({Marshal.GetLastWin32Error()})");

        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = _width,
            biHeight = -_height, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<BitmapInfoHeader>());
        try
        {
            Marshal.StructureToPtr(header, hdr, false);
            if (GetDIBits(_memDc, _bitmap, 0, (uint)_height, _buffer, hdr, 0) == 0)
                throw new InvalidOperationException("ScreenCapture: GetDIBits failed");
        }
        finally
        {
            Marshal.FreeHGlobal(hdr);
        }
        return _buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_bitmap != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBitmap);
            DeleteObject(_bitmap);
        }
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint startScan, uint scanLines,
        byte[] bits, IntPtr bi, uint usage);
}
