using System.Runtime.InteropServices;

namespace Veil.Services;

internal static class SystemVolumeHelper
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role,
            [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float levelDB, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDB);
        int GetMasterVolumeLevelScalar(out float level);
    }

    private static readonly Guid AudioEndpointVolumeIid =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    internal static float GetVolume()
    {
        try
        {
            var volume = GetEndpointVolume();
            volume.GetMasterVolumeLevelScalar(out float level);
            return level;
        }
        catch
        {
            return 0.5f;
        }
    }

    internal static void SetVolume(float level)
    {
        try
        {
            var volume = GetEndpointVolume();
            var guid = Guid.Empty;
            volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref guid);
        }
        catch { }
    }

    private static IAudioEndpointVolume GetEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
        var iid = AudioEndpointVolumeIid;
        device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
        return (IAudioEndpointVolume)obj;
    }
}
