using System.Runtime.InteropServices;

namespace Dpt.App.Infrastructure;

/// <summary>Minimal notification-area icon (Shell_NotifyIcon) driven by the main window's message loop.</summary>
public sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 0x51; // WM_APP + 0x51
    public const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _visible;

    public TrayIcon(IntPtr hwnd) => _hwnd = hwnd;

    public bool IsVisible => _visible;

    private NOTIFYICONDATA Data(string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show(string tip)
    {
        if (_icon == IntPtr.Zero) _icon = ExtractIcon(IntPtr.Zero, Environment.ProcessPath ?? "", 0);
        var d = Data(tip);
        Shell_NotifyIcon(_visible ? NIM_MODIFY : NIM_ADD, ref d);
        _visible = true;
    }

    public void Balloon(string title, string text)
    {
        if (!_visible) return;
        var d = Data("DrivePartitionTool");
        d.uFlags |= NIF_INFO;
        d.szInfoTitle = title;
        d.szInfo = text;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Hide()
    {
        if (!_visible) return;
        var d = Data("");
        Shell_NotifyIcon(NIM_DELETE, ref d);
        _visible = false;
    }

    public void Dispose()
    {
        Hide();
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }
}
