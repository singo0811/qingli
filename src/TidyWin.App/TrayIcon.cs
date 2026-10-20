using System.Runtime.InteropServices;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>
/// The icon in the notification area. Closing the window hides it; this icon is how it is opened again, and how it is really exited.
/// The icon is drawn by Windows from the program's own icon file. No extra library is loaded for it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint MfChecked = 0x8;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x10;
    private const int MenuOpen = 1;
    private const int MenuAuto = 2;
    private const int MenuExit = 3;

    private readonly WndProc _proc;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public TrayIcon(Action open, Action exit, Func<bool> autoOn, Action<bool> setAuto)
    {
        Open = open;
        Exit = exit;
        AutoOn = autoOn;
        SetAuto = setAuto;
        _proc = WindowProc;
        _hwnd = CreateMessageWindow();
        if (_hwnd == IntPtr.Zero) return;
        _icon = LoadIconFile();
        _added = AddIcon();
    }

    public bool Ready => _added;

    private Action Open { get; }
    private Action Exit { get; }
    private Func<bool> AutoOn { get; }
    private Action<bool> SetAuto { get; }

    public void Balloon(string title, string text)
    {
        if (!_added) return;
        var data = Base();
        data.uFlags |= NifInfo;
        data.szInfoTitle = Cut(title, 63);
        data.szInfo = Cut(text, 255);
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Base();
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private bool AddIcon()
    {
        var data = Base();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "TidyWin";
        return Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyData Base() => new()
    {
        cbSize = Marshal.SizeOf<NotifyData>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage)
        {
            var mouse = (uint)lParam;
            if (mouse is WmLButtonUp or WmLButtonDblClk) Open();
            else if (mouse == WmRButtonUp) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WmCommand)
        {
            switch (((int)wParam) & 0xFFFF)
            {
                case MenuOpen: Open(); break;
                case MenuAuto: SetAuto(!AutoOn()); break;
                case MenuExit: Exit(); break;
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, MenuOpen, Loc.T("打开 TidyWin"));
        AppendMenu(menu, MfString | (AutoOn() ? MfChecked : 0), MenuAuto, Loc.T("自动清理缓存和空闲内存"));
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, MenuExit, Loc.T("退出"));
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, 2, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
    }

    private IntPtr CreateMessageWindow()
    {
        var instance = GetModuleHandle(null);
        var cls = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = instance,
            lpszClassName = "TidyWin.Tray",
        };
        RegisterClass(ref cls);
        return CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
    }

    private static IntPtr LoadIconFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TidyWin.ico");
        if (!File.Exists(path)) return IntPtr.Zero;
        return LoadImage(IntPtr.Zero, path, ImageIcon, 0, 0, LoadFromFile | 0x40);
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass cls);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nint id, string? text);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
