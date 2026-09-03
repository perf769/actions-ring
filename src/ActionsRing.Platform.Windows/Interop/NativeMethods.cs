using System.Runtime.InteropServices;

namespace ActionsRing.Platform.Windows.Interop;

internal static class NativeMethods
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const int HcAction = 0;

    internal const uint WmQuit = 0x0012;
    internal const uint WmClose = 0x0010;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmMouseHWheel = 0x020E;
    internal const uint PmNoRemove = 0x0000;

    internal const uint LlkhfExtended = 0x01;
    internal const uint LlkhfInjected = 0x10;
    internal const uint LlmhfInjected = 0x01;

    internal const int VkEscape = 0x1B;
    internal const int VkShift = 0x10;
    internal const int VkControl = 0x11;
    internal const int VkMenu = 0x12;
    internal const int VkLShift = 0xA0;
    internal const int VkRShift = 0xA1;
    internal const int VkLControl = 0xA2;
    internal const int VkRControl = 0xA3;
    internal const int VkLMenu = 0xA4;
    internal const int VkRMenu = 0xA5;
    internal const int VkLWin = 0x5B;
    internal const int VkRWin = 0x5C;

    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint MonitorInfoPrimary = 0x00000001;

    internal const int SwMinimize = 6;
    internal const int SwMaximize = 3;
    internal const int SwRestore = 9;
    internal const uint FlashwTray = 0x00000002;
    internal const uint FlashwTimerNoForeground = 0x0000000C;

    internal const int GwlExStyle = -20;
    internal const long WsExTopmost = 0x00000008L;
    internal static readonly nint HwndTopmost = new(-1);
    internal static readonly nint HwndNoTopmost = new(-2);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const int ErrorInsufficientBuffer = 122;

    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint KeyeventfUnicode = 0x0004;
    internal const uint KeyeventfExtendedKey = 0x0001;
    internal const uint MouseeventfLeftdown = 0x0002;
    internal const uint MouseeventfLeftup = 0x0004;
    internal const uint MouseeventfRightdown = 0x0008;
    internal const uint MouseeventfRightup = 0x0010;
    internal const uint MouseeventfMiddledown = 0x0020;
    internal const uint MouseeventfMiddleup = 0x0040;
    internal const uint MouseeventfXdown = 0x0080;
    internal const uint MouseeventfXup = 0x0100;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfHWheel = 0x1000;
    internal const uint XButton1 = 0x0001;
    internal const uint XButton2 = 0x0002;
    internal const uint MapVkVirtualKeyToScanCodeEx = 4;

    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    internal delegate bool MonitorEnumProc(nint monitor, nint hdc, nint monitorRect, nint data);
    internal delegate bool EnumWindowsProc(nint window, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern SafeWindowsHookHandle SetWindowsHookEx(
        int hookId,
        HookProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessage(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out Message message,
        nint window,
        uint min,
        uint max,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessage(in Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetKeyNameText(int lParam, [Out] char[] name, int size);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(nint window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(
        Microsoft.Win32.SafeHandles.SafeProcessHandle process,
        uint flags,
        [Out] char[] executableName,
        ref uint size);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadACValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subgroupGuid,
        in Guid settingGuid,
        out uint valueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerReadDCValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subgroupGuid,
        in Guid settingGuid,
        out uint valueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteACValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subgroupGuid,
        in Guid settingGuid,
        uint valueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerWriteDCValueIndex(
        nint rootPowerKey,
        in Guid schemeGuid,
        in Guid subgroupGuid,
        in Guid settingGuid,
        uint valueIndex);

    [DllImport("powrprof.dll")]
    internal static extern uint PowerSetActiveScheme(nint userRootPowerKey, in Guid schemeGuid);

    [DllImport("kernel32.dll")]
    internal static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);

    internal static nint GetWindowLongPtr(nint window, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(window, index) : new nint(GetWindowLong32(window, index));

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Value;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;

        internal Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size;
        internal uint Flags;
        internal nint ActiveWindow;
        internal nint FocusWindow;
        internal nint CaptureWindow;
        internal nint MenuOwnerWindow;
        internal nint MoveSizeWindow;
        internal nint CaretWindow;
        internal Rect CaretBounds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardLowLevelHookData
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseLowLevelHookData
    {
        internal Point Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        internal MouseInput Mouse;

        [FieldOffset(0)]
        internal KeyboardInput Keyboard;

        [FieldOffset(0)]
        internal HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        internal uint Message;
        internal ushort ParamLow;
        internal ushort ParamHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FlashWindowInfo
    {
        internal uint Size;
        internal nint Window;
        internal uint Flags;
        internal uint Count;
        internal uint Timeout;
    }
}
