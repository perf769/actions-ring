using System.Runtime.InteropServices;

namespace ActionsRing.Platform.Windows.Interop;

internal static class CoreAudioInterop
{
    internal static readonly Guid DeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal enum DataFlow
    {
        Render,
        Capture,
        All
    }

    internal enum Role
    {
        Console,
        Multimedia,
        Communications
    }

    [Flags]
    internal enum ClassContext : uint
    {
        InProcessServer = 0x1,
        InProcessHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InProcessServer | InProcessHandler | LocalServer | RemoteServer
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDevice
    {
        [PreserveSig]
        int Activate(
            in Guid interfaceId,
            ClassContext classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfaceObject);

        [PreserveSig]
        int OpenPropertyStore(uint access, out nint properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint notify);

        [PreserveSig]
        int GetChannelCount(out uint channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, in Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, in Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, in Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, in Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, in Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);

        [PreserveSig]
        int GetVolumeStepInfo(out uint step, out uint stepCount);

        [PreserveSig]
        int VolumeStepUp(in Guid eventContext);

        [PreserveSig]
        int VolumeStepDown(in Guid eventContext);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);

        [PreserveSig]
        int GetVolumeRange(out float minimumDb, out float maximumDb, out float incrementDb);
    }
}
