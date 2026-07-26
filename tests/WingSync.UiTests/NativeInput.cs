using System.Runtime.InteropServices;
using System.Windows;

namespace WingSync.UiTests;

internal static class NativeInput
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint WindowMessageKeyDown = 0x0100;
    private const uint WindowMessageKeyUp = 0x0101;
    private const byte VirtualKeyF2 = 0x71;
    private const byte VirtualKeyTab = 0x09;

    public static void DoubleClick(Point point, IntPtr windowHandle)
    {
        _ = SetForegroundWindow(windowHandle);
        if (!SetCursorPosition(
                checked((int)Math.Round(point.X)),
                checked((int)Math.Round(point.Y))))
        {
            throw new InvalidOperationException(
                $"Could not position the pointer. Win32 error {Marshal.GetLastWin32Error()}.");
        }

        Click();
        Thread.Sleep(80);
        Click();
    }

    public static void PressF2(IntPtr windowHandle)
    {
        PressKey(VirtualKeyF2, windowHandle);
    }

    public static void PressTab(IntPtr windowHandle)
    {
        PressKey(VirtualKeyTab, windowHandle);
    }

    private static void Click()
    {
        MouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static void PressKey(byte virtualKey, IntPtr windowHandle)
    {
        SendKeyDown(virtualKey, windowHandle);
        SendKeyUp(virtualKey, windowHandle);
    }

    private static void SendKeyDown(byte virtualKey, IntPtr windowHandle) =>
        _ = SendMessage(
            windowHandle,
            WindowMessageKeyDown,
            (nuint)virtualKey,
            (nint)1);

    private static void SendKeyUp(byte virtualKey, IntPtr windowHandle) =>
        _ = SendMessage(
            windowHandle,
            WindowMessageKeyUp,
            (nuint)virtualKey,
            unchecked((nint)(int)0xC0000001));

#pragma warning disable SYSLIB1054 // Classic P/Invoke is sufficient for these fixed Win32 signatures.
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "SetCursorPos", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPosition(int x, int y);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void MouseEvent(
        uint flags,
        uint x,
        uint y,
        uint data,
        UIntPtr extraInformation);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessage(
        IntPtr windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

#pragma warning restore SYSLIB1054
}
