using System.Runtime.InteropServices;
using SideBar.Core;
using SideBar.Core.Nearby;
using SideBar.Core.Updates;

namespace SideBar.Tray;

/// <summary>Иконка в области уведомлений (NOTIFYICON_VERSION_4) с меню: автозапуск (галочка) и выход.</summary>
sealed unsafe class TrayIcon : IDisposable
{
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
    const uint NOTIFYICON_VERSION_4 = 4;
    const uint CallbackMessage = 0x8001;   // WM_APP + 1
    const uint WM_CONTEXTMENU = 0x7B, WM_NULL = 0;
    const uint MF_STRING = 0, MF_CHECKED = 8, MF_GRAYED = 1, MF_BYCOMMAND = 0, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    const int SM_CXSMICON = 49;
    const int CmdAutostart = 1, CmdExit = 2, CmdHidden = 3, CmdOwnDevices = 4, CmdEveryone = 5, CmdSettings = 6, CmdUpdate = 7;   // 3..5 — радиогруппа видимости

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll")] static extern bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern bool CheckMenuRadioItem(nint menu, uint first, uint last, uint check, uint flags);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, nint* icons, uint* ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);

    readonly MessageWindow _window;
    readonly Autostart _autostart;
    readonly string _exePath;
    readonly Action _onExit;
    readonly Func<Visibility> _mode;
    readonly Func<bool> _ownDevicesAvailable;
    readonly Action<Visibility> _setMode;
    readonly Func<bool> _stackPaused;
    readonly uint _taskbarCreated;
    nint _icon;
    bool _added;

    /// <summary>Пункт «Настройки…»: вызывается на UI-потоке после закрытия меню.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>Релиз новее текущей версии (M7): пока он есть, в меню появляется пункт «Доступна версия X.Y.Z»; null — пункта нет.</summary>
    public Func<ReleaseInfo?>? AvailableUpdate { get; set; }

    /// <summary>Открытие страницы релиза по пункту (адрес уже проверен ReleaseInfo).</summary>
    public Action<ReleaseInfo>? OpenUpdate { get; set; }

    public TrayIcon(MessageWindow window, Autostart autostart, string exePath, Action onExit,
        Func<Visibility> mode, Func<bool> ownDevicesAvailable, Action<Visibility> setMode, Func<bool> stackPaused)
    {
        _window = window;
        _autostart = autostart;
        _exePath = exePath;
        _onExit = onExit;
        _mode = mode;
        _ownDevicesAvailable = ownDevicesAvailable;
        _setMode = setMode;
        _stackPaused = stackPaused;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadIcon();
        Add();
        window.Message += OnMessage;
    }

    // иконка из ресурсов exe (<ApplicationIcon>), нужного размера под текущий DPI
    nint LoadIcon()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        nint icon = 0;
        uint id;
        return PrivateExtractIconsW(_exePath, 0, size, size, &icon, &id, 1, 0) >= 1 ? icon : 0;
    }

    void Add()
    {
        var d = Data();
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = CallbackMessage;
        d.hIcon = _icon;
        int n = Math.Min(Strings.AppName.Length, 127);
        for (int i = 0; i < n; i++) d.szTip[i] = Strings.AppName[i];
        _added |= Shell_NotifyIconW(NIM_ADD, &d);   // повторный ADD после TaskbarCreated может вернуть false, если иконка уже есть
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &d);
    }

    NOTIFYICONDATAW Data() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _window.Handle, uID = 1 };

    void OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated) { Add(); return; }   // Проводник перезапущен — иконки пропали
        if (msg != CallbackMessage) return;
        // в v4 оболочка шлёт WM_CONTEXTMENU, якорь меню — в wParam (x — младшее слово, y — старшее)
        if ((uint)lParam % 0x10000 == WM_CONTEXTMENU) ShowMenu((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
    }

    void ShowMenu(int x, int y)
    {
        nint menu = CreatePopupMenu();
        var update = AvailableUpdate?.Invoke();
        if (update is not null) AppendMenuW(menu, MF_STRING, CmdUpdate, Strings.UpdateAvailable(update.Version.ToString()));
        AppendMenuW(menu, MF_STRING, CmdSettings, Strings.SettingsMenu);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        // пока открыт официальный Quick Share, наш стек не слушает и не рекламируется
        if (_stackPaused()) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, Strings.StackPaused);
        AppendMenuW(menu, MF_STRING, CmdHidden, Strings.VisibilityHidden);
        AppendMenuW(menu, MF_STRING | (_ownDevicesAvailable() ? 0 : MF_GRAYED), CmdOwnDevices, Strings.VisibilityOwnDevices);   // без сертификатов недоступно
        AppendMenuW(menu, MF_STRING, CmdEveryone, Strings.VisibilityEveryone);
        uint current = _mode() switch { Visibility.OwnDevices => CmdOwnDevices, Visibility.Everyone => CmdEveryone, _ => CmdHidden };
        CheckMenuRadioItem(menu, CmdHidden, CmdEveryone, current, MF_BYCOMMAND);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING | (_autostart.IsEnabled ? MF_CHECKED : 0), CmdAutostart, Strings.Autostart);
        AppendMenuW(menu, MF_STRING, CmdExit, Strings.Exit);
        SetForegroundWindow(_window.Handle);   // иначе меню не закрывается кликом вне него
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, 0, _window.Handle, 0);
        PostMessageW(_window.Handle, WM_NULL, 0, 0);
        DestroyMenu(menu);

        if (cmd == CmdAutostart)
        {
            if (_autostart.IsEnabled) _autostart.Disable(); else _autostart.Enable(_exePath);
        }
        else if (cmd == CmdHidden) _setMode(Visibility.Hidden);
        else if (cmd == CmdOwnDevices) _setMode(Visibility.OwnDevices);
        else if (cmd == CmdEveryone) _setMode(Visibility.Everyone);
        else if (cmd == CmdSettings) OpenSettings?.Invoke();
        else if (cmd == CmdUpdate && update is not null) OpenUpdate?.Invoke(update);
        else if (cmd == CmdExit) _onExit();
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        if (_added)
        {
            var d = Data();
            Shell_NotifyIconW(NIM_DELETE, &d);
            _added = false;
        }
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
    }
}
