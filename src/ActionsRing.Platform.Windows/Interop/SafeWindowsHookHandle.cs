using Microsoft.Win32.SafeHandles;

namespace ActionsRing.Platform.Windows.Interop;

internal sealed class SafeWindowsHookHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeWindowsHookHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.UnhookWindowsHookEx(handle);
}
