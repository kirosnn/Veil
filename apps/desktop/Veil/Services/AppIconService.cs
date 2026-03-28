using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Veil.Services;

internal static class AppIconService
{
    private const int IconSize = 32;
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> Cache = new();

    internal static WriteableBitmap? GetIcon(InstalledApp app)
    {
        return Cache.GetOrAdd(app.AppId, _ => ExtractIcon(app.AppId));
    }

    private static WriteableBitmap? ExtractIcon(string appId)
    {
        try
        {
            string shellPath = $"shell:AppsFolder\\{appId}";
            int hr = SHCreateItemFromParsingName(shellPath, IntPtr.Zero, ref IID_IShellItemImageFactory, out IntPtr ppv);
            if (hr != 0 || ppv == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
                try
                {
                    var size = new SIZE { cx = IconSize, cy = IconSize };
                    hr = factory.GetImage(size, SIIGBF_ICONONLY, out IntPtr hBitmap);
                    if (hr != 0 || hBitmap == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        return HBitmapToWriteableBitmap(hBitmap);
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
            finally
            {
                Marshal.Release(ppv);
            }
        }
        catch
        {
            return null;
        }
    }

    private static WriteableBitmap? HBitmapToWriteableBitmap(IntPtr hBitmap)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = IconSize,
            biHeight = -IconSize,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        byte[] pixels = new byte[IconSize * IconSize * 4];

        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr oldBitmap = SelectObject(hdc, hBitmap);
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)IconSize, pixels, ref bmi, 0);
            SelectObject(hdc, oldBitmap);

            if (lines == 0)
            {
                return null;
            }
        }
        finally
        {
            DeleteDC(hdc);
        }

        var bitmap = new WriteableBitmap(IconSize, IconSize);
        pixels.CopyTo(bitmap.PixelBuffer);
        return bitmap;
    }

    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int SIIGBF_ICONONLY = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
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

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
