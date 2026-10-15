using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;

namespace EzView;

// The thumbnail Windows Explorer shows (video frame, gif, some 3D formats), via IShellItemImageFactory.
static class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct Size { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapHeader { public int Type, Width, Height, WidthBytes; public short Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid, out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BitmapHeader bmp);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    const int ThumbnailOnly = 0x08; // no file-type icon as a fallback

    public static Bitmap? Get(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        try
        {
            if (factory.GetImage(new Size { Cx = size, Cy = size }, ThumbnailOnly, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                GetObject(hbmp, Marshal.SizeOf<BitmapHeader>(), out var bmp);
                int w = bmp.Width, h = bmp.Height;
                var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32 }; // top-down BGRA
                var bits = new byte[w * h * 4];
                var dc = GetDC(IntPtr.Zero);
                try { if (GetDIBits(dc, hbmp, 0, (uint)h, bits, ref info, 0) == 0) return null; }
                finally { ReleaseDC(IntPtr.Zero, dc); }
                // Video frames come without alpha (all zero): make them opaque.
                bool noAlpha = true;
                for (int i = 3; i < bits.Length && noAlpha; i += 4) noAlpha = bits[i] == 0;
                if (noAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
                return ImageView.ToBitmap(bits, w, h);
            }
            finally { DeleteObject(hbmp); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
