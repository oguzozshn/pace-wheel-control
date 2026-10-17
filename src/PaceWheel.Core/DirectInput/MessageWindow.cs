using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaceWheel.Core.DirectInput;

/// <summary>
/// Hidden message-only window (parent HWND_MESSAGE). DirectInput needs a window handle for
/// SetCooperativeLevel, and a console app has none of its own. Create and use it on the same thread.
/// </summary>
public sealed partial class MessageWindow : IDisposable
{
    private static readonly IntPtr HwndMessage = new(-3);

    public MessageWindow(string title = "PaceWheel")
    {
        Handle = CreateWindowExW(0, "STATIC", title, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create message-only window.");
        }
    }

    public IntPtr Handle { get; private set; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? moduleName);
}
