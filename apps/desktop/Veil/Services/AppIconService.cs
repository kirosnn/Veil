using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal static class AppIconService
{
    private const int IconSize = 32;
    private const int HighResIconSize = 256;
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> Cache = new();
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> HighResCache = new();
    private static readonly ConcurrentDictionary<nint, WriteableBitmap?> WindowHighResCache = new();
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> ResourceIconCache = new();

    internal static WriteableBitmap? GetIcon(InstalledApp app)
    {
        return Cache.GetOrAdd(app.AppId, _ => ExtractIcon(app.AppId));
    }

    internal static WriteableBitmap? GetWindowIcon(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            using Process process = Process.GetProcessById((int)processId);
            string? executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return null;
            }

            return Cache.GetOrAdd($"exe:{executablePath}", _ => ExtractExecutableIcon(executablePath));
        }
        catch
        {
            return null;
        }
    }

    internal static WriteableBitmap? GetHighResWindowIcon(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            using Process process = Process.GetProcessById((int)processId);
            string? executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return hwnd == IntPtr.Zero
                    ? null
                    : WindowHighResCache.GetOrAdd(hwnd, handle => ExtractWindowIcon((IntPtr)handle, HighResIconSize));
            }

            return GetHighResIcon(executablePath)
                ?? (hwnd == IntPtr.Zero
                    ? null
                    : WindowHighResCache.GetOrAdd(hwnd, handle => ExtractWindowIcon((IntPtr)handle, HighResIconSize)));
        }
        catch
        {
            return hwnd == IntPtr.Zero
                ? null
                : WindowHighResCache.GetOrAdd(hwnd, handle => ExtractWindowIcon((IntPtr)handle, HighResIconSize));
        }
    }

    internal static WriteableBitmap? GetHighResIcon(string executablePath)
    {
        return HighResCache.GetOrAdd($"hires:{executablePath}", _ =>
            ExtractShellItemIcon(executablePath, HighResIconSize)
            ?? ExtractIndexedIcon(executablePath, 0, HighResIconSize)
            ?? ExtractExecutableIcon(executablePath, HighResIconSize));
    }

    internal static WriteableBitmap? GetHighResIconResource(string iconPath, int iconIndex)
    {
        return ResourceIconCache.GetOrAdd($"resource:{iconPath}|{iconIndex}", _ =>
        {
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            {
                return null;
            }

            return ExtractIndexedIcon(iconPath, iconIndex, HighResIconSize)
                ?? ExtractShellItemIcon(iconPath, HighResIconSize)
                ?? ExtractExecutableIcon(iconPath, HighResIconSize);
        });
    }

    internal static WriteableBitmap? GetIconByAppUserModelId(string appUserModelId)
    {
        return HighResCache.GetOrAdd($"aumid:{appUserModelId}", _ =>
        {
            try
            {
                string shellPath = $"shell:AppsFolder\\{appUserModelId}";
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
                        var size = new SIZE { cx = HighResIconSize, cy = HighResIconSize };
                        hr = factory.GetImage(size, SIIGBF_RESIZETOFIT, out IntPtr hBitmap);
                        if (hr != 0 || hBitmap == IntPtr.Zero)
                        {
                            return null;
                        }

                        try
                        {
                            return HBitmapToWriteableBitmap(hBitmap, HighResIconSize);
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
        });
    }

    private static WriteableBitmap? ExtractShellItemIcon(string executablePath, int size)
    {
        try
        {
            int hr = SHCreateItemFromParsingName(executablePath, IntPtr.Zero, ref IID_IShellItemImageFactory, out IntPtr ppv);
            if (hr != 0 || ppv == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(ppv);
                try
                {
                    var requestedSize = new SIZE { cx = size, cy = size };
                    hr = factory.GetImage(requestedSize, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
                    if (hr != 0 || hBitmap == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        return HBitmapToWriteableBitmap(hBitmap, size);
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
                    hr = factory.GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
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
        return HBitmapToWriteableBitmap(hBitmap, IconSize);
    }

    private static WriteableBitmap? HBitmapToWriteableBitmap(IntPtr hBitmap, int size)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        byte[] pixels = new byte[size * size * 4];

        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr oldBitmap = SelectObject(hdc, hBitmap);
            int lines = GetDIBits(hdc, hBitmap, 0, (uint)size, pixels, ref bmi, 0);
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

        var bitmap = new WriteableBitmap(size, size);
        pixels.CopyTo(bitmap.PixelBuffer);
        return bitmap;
    }

    private static WriteableBitmap? ExtractExecutableIcon(string executablePath)
    {
        return ExtractExecutableIcon(executablePath, IconSize);
    }

    private static WriteableBitmap? ExtractExecutableIcon(string executablePath, int size)
    {
        IntPtr hIcon = ExtractIconW(IntPtr.Zero, executablePath, 0);
        if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1))
        {
            return null;
        }

        try
        {
            return HIconToWriteableBitmap(hIcon, size);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static WriteableBitmap? ExtractIndexedIcon(string executablePath, int iconIndex, int size)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        IntPtr[] largeIcons = [IntPtr.Zero];
        IntPtr[] smallIcons = [IntPtr.Zero];
        uint extracted = ExtractIconExW(executablePath, iconIndex, largeIcons, smallIcons, 1);
        if (extracted == 0)
        {
            return null;
        }

        IntPtr hIcon = largeIcons[0] != IntPtr.Zero ? largeIcons[0] : smallIcons[0];
        if (hIcon == IntPtr.Zero)
        {
            if (smallIcons[0] != IntPtr.Zero)
            {
                DestroyIcon(smallIcons[0]);
            }

            return null;
        }

        try
        {
            return HIconToWriteableBitmap(hIcon, size);
        }
        finally
        {
            if (largeIcons[0] != IntPtr.Zero)
            {
                DestroyIcon(largeIcons[0]);
            }

            if (smallIcons[0] != IntPtr.Zero && smallIcons[0] != largeIcons[0])
            {
                DestroyIcon(smallIcons[0]);
            }
        }
    }

    private static WriteableBitmap? ExtractWindowIcon(IntPtr hwnd, int size)
    {
        IntPtr hIcon = TryGetWindowIconHandle(hwnd);
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return HIconToWriteableBitmap(hIcon, size);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static IntPtr TryGetWindowIconHandle(IntPtr hwnd)
    {
        IntPtr hIcon = SendMessageW(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (hIcon == IntPtr.Zero)
        {
            hIcon = SendMessageW(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero);
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = SendMessageW(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = GetClassLongPtrW(hwnd, GCLP_HICON);
        }

        if (hIcon == IntPtr.Zero)
        {
            hIcon = GetClassLongPtrW(hwnd, GCLP_HICONSM);
        }

        if (hIcon == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr copy = CopyIcon(hIcon);
        return copy != IntPtr.Zero ? copy : IntPtr.Zero;
    }

    private static WriteableBitmap? HIconToWriteableBitmap(IntPtr hIcon)
    {
        return HIconToWriteableBitmap(hIcon, IconSize);
    }

    private static WriteableBitmap? HIconToWriteableBitmap(IntPtr hIcon, int size)
    {
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return null;
        }

        IntPtr hBitmap = CreateCompatibleBitmap(hdc, size, size);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(hdc);
            return null;
        }

        try
        {
            IntPtr oldBitmap = SelectObject(hdc, hBitmap);
            try
            {
                if (!DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DI_NORMAL))
                {
                    return null;
                }
            }
            finally
            {
                SelectObject(hdc, oldBitmap);
            }

            return HBitmapToWriteableBitmap(hBitmap, size);
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(hdc);
        }
    }


    private static Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_ICONONLY = 0x00000004;
    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int DI_NORMAL = 0x0003;

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
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        int diFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
