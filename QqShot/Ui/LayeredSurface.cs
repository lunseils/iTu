using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QqShot.Core;

namespace QqShot.Ui;

/// <summary>
/// 把控件变成"分层窗口"（WS_EX_LAYERED），内容以 32 位 ARGB 位图提交。
///
/// 为什么必须这么做：普通子控件的 BackColor 透明是由**父窗体**负责垫底的，
/// 而本窗体的背景是透明键色，于是圆角的抗锯齿像素会和键色混合，出现粉边/锯齿。
/// 分层窗口由 DWM 按像素 alpha 合成，圆角能真正平滑地叠在桌面或截图上。
/// </summary>
internal static class LayeredSurface
{
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    /// <summary>
    /// 以 N 倍分辨率绘制后降采样，返回成品位图。
    ///
    /// 圆角/柔影这类"边缘全是曲线"的图形，直接在 1:1 上抗锯齿仍会留下
    /// 1~2px 的台阶感（尤其在高 DPI 或缩放显示时）。先放大绘制再高质量缩小，
    /// 等于把采样密度提高 N 倍，边缘会明显更连续。
    /// </summary>
    public static Bitmap Supersample(Size size, Action<Graphics> draw, int factor = 2)
    {
        int f = Math.Max(1, factor);
        int w = Math.Max(1, size.Width), h = Math.Max(1, size.Height);

        using var big = new Bitmap(w * f, h * f, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.Clear(Color.Transparent);
            g.ScaleTransform(f, f);
            draw(g);
        }

        var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(big, new Rectangle(0, 0, w, h), 0, 0, big.Width, big.Height, GraphicsUnit.Pixel);
        }
        return small;
    }

    /// <summary>
    /// 高频重绘专用的可复用分层缓冲。
    /// <see cref="Submit"/> 每次都 CreateDIBSection/DeleteObject，全屏浮层按 60Hz
    /// 提交时会疯狂创建 GDI 对象；这里把 DC 与 DIB 缓存下来，只在尺寸变化时重建。
    /// </summary>
    internal sealed class SurfaceBuffer : IDisposable
    {
        private IntPtr _screenDc = IntPtr.Zero;
        private IntPtr _memDc = IntPtr.Zero;
        private IntPtr _dib = IntPtr.Zero;
        private IntPtr _old = IntPtr.Zero;
        private IntPtr _bits = IntPtr.Zero;
        private int _w, _h;

        public void Present(IntPtr hwnd, Bitmap bmp, Point screenPos)
        {
            if (hwnd == IntPtr.Zero || bmp.Width <= 0 || bmp.Height <= 0) return;

            if (_screenDc == IntPtr.Zero) _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) return;
            if (_memDc == IntPtr.Zero) _memDc = CreateCompatibleDC(_screenDc);
            if (_memDc == IntPtr.Zero) { Log.Info("[layer] CreateCompatibleDC 失败"); return; }

            if (_dib == IntPtr.Zero || _w != bmp.Width || _h != bmp.Height)
            {
                if (_dib != IntPtr.Zero)
                {
                    if (_old != IntPtr.Zero) SelectObject(_memDc, _old);
                    DeleteObject(_dib);
                    _dib = IntPtr.Zero;
                }

                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = (uint)bmp.Width,
                    biHeight = (uint)-bmp.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };
                _dib = CreateDIBSection(_screenDc, ref header, 0, out _bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
                {
                    Log.Info($"[layer] CreateDIBSection 失败 err={Marshal.GetLastWin32Error()}");
                    return;
                }
                _old = SelectObject(_memDc, _dib);
                _w = bmp.Width;
                _h = bmp.Height;
            }

            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // DIB 行跨距固定为 width*4；逐行拷贝避免 Bitmap.Stride 更大时越界
                int row = bmp.Width * 4;
                for (int y = 0; y < bmp.Height; y++)
                    CopyMemory(_bits + y * row, data.Scan0 + y * data.Stride, (IntPtr)row);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = screenPos.X, y = screenPos.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            if (!UpdateLayeredWindow(hwnd, _screenDc, ref dst, ref size, _memDc, ref src,
                    0, ref blend, ULW_ALPHA))
                Log.Info($"[layer] UpdateLayeredWindow 失败 err={Marshal.GetLastWin32Error()}");
        }

        public void Dispose()
        {
            if (_memDc != IntPtr.Zero && _old != IntPtr.Zero) SelectObject(_memDc, _old);
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            _dib = _old = _memDc = _screenDc = _bits = IntPtr.Zero;
        }
    }

    public static void Submit(IntPtr hwnd, Bitmap bmp, Point screenPos)
    {
        if (hwnd == IntPtr.Zero || bmp.Width <= 0 || bmp.Height <= 0) return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }
        IntPtr hBitmap = IntPtr.Zero;

        try
        {
            // 关键：分层窗口要求**预乘 alpha** 的 32 位 DIB。
            // Bitmap.GetHbitmap 会丢掉 alpha 通道，结果整块渲染成黑色，
            // 所以这里手工建 DIB 段并把预乘后的像素拷进去。
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = (uint)bmp.Width,
                biHeight = (uint)-bmp.Height,   // 负数 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            hBitmap = CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                Core.Log.Info($"[layer] CreateDIBSection 失败 err={Marshal.GetLastWin32Error()}");
                return;   // finally 仍会释放已取得的句柄
            }

            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // DIB 行跨距固定为 width*4；逐行拷贝避免 Bitmap.Stride 更大时越界写
                // （Stride 会按 4 字节对齐，个别宽度下会大于 width*4）。
                int row = bmp.Width * 4;
                for (int y = 0; y < bmp.Height; y++)
                    CopyMemory(bits + y * row, data.Scan0 + y * data.Stride, (IntPtr)row);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            IntPtr old = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = screenPos.X, y = screenPos.Y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            bool ok = UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            if (!ok)
                Core.Log.Info($"[layer] UpdateLayeredWindow 失败 err={Marshal.GetLastWin32Error()} size={bmp.Width}x{bmp.Height}");

            SelectObject(memDc, old);
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
