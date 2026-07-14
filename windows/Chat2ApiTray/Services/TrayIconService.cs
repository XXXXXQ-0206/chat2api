using System.Drawing;
using System.Runtime.InteropServices;
using Chat2ApiTray.Models;

namespace Chat2ApiTray.Services;

public enum TrayCommand
{
    StartService,
    StopService,
    RestartService,
    OpenLogin,
    CheckStatus,
    OpenHealth,
    CopyOpenAiBaseUrl,
    CopyAnthropicBaseUrl,
    OpenProjectDirectory,
    OpenDataDirectory,
    Exit
}

public enum TraySettingToggle
{
    LaunchAtStartup,
    StartServiceOnLaunch
}

public sealed class TrayIconService : IDisposable
{
    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;
    private const int WmApp = 0x8000;
    private const int WmTrayIcon = WmApp + 1;
    private const int WmCommand = 0x0111;
    private const int WmDestroy = 0x0002;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int TpmLeftAlign = 0x0000;
    private const int TpmBottomAlign = 0x0020;
    private const int TpmRightButton = 0x0002;
    private const int MiimState = 0x00000001;
    private const int MiimId = 0x00000002;
    private const int MiimSubmenu = 0x00000004;
    private const int MiimType = 0x00000010;
    private const int MftString = 0x00000000;
    private const int MftSeparator = 0x00000800;
    private const int MfsEnabled = 0x00000000;
    private const int MfsDisabled = 0x00000003;
    private const int MfsChecked = 0x00000008;
    private const int MfsUnchecked = 0x00000000;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int IdStartService = 1001;
    private const int IdStopService = 1002;
    private const int IdRestartService = 1003;
    private const int IdOpenLogin = 1004;
    private const int IdCheckStatus = 1005;
    private const int IdOpenHealth = 1006;
    private const int IdCopyOpenAiBaseUrl = 1007;
    private const int IdCopyAnthropicBaseUrl = 1008;
    private const int IdOpenProjectDirectory = 1009;
    private const int IdOpenDataDirectory = 1010;
    private const int IdToggleLaunchAtStartup = 1101;
    private const int IdToggleStartServiceOnLaunch = 1102;
    private const int IdExit = 1999;

    private readonly string _windowClassName = $"Chat2ApiTray.TrayWindow.{Guid.NewGuid():N}";
    private readonly WndProc _wndProc;
    private readonly Icon _runningIcon = SystemIcons.Information;
    private readonly Icon _warningIcon = SystemIcons.Warning;
    private readonly Icon _unknownIcon = SystemIcons.Question;
    private readonly Icon _stoppedIcon = SystemIcons.Application;
    private readonly nint _windowHandle;
    private readonly uint _windowClassAtom;
    private readonly uint _taskbarCreatedMessage;
    private readonly SynchronizationContext _syncContext;
    private readonly object _stateLock = new();
    private ServiceSnapshot _snapshot = ServiceSnapshot.Stopped("正在初始化。");
    private TraySettings _settings = new();
    private Icon _currentIcon;

    public TrayIconService()
    {
        _syncContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _wndProc = WindowProc;
        _windowClassAtom = RegisterWindowClass(_windowClassName, _wndProc);
        if (_windowClassAtom == 0)
        {
            throw new InvalidOperationException("Failed to register tray window class.");
        }

        _windowHandle = CreateMessageWindow(_windowClassName);
        if (_windowHandle == 0)
        {
            throw new InvalidOperationException("Failed to create tray message window.");
        }

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _currentIcon = _stoppedIcon;
        AddTrayIcon(_currentIcon, _snapshot.Tooltip);
    }

    public event Action<TrayCommand>? CommandRequested;

    public event Action<TraySettingToggle, bool>? ToggleRequested;

    public void Update(ServiceSnapshot snapshot, TraySettings settings)
    {
        lock (_stateLock)
        {
            _snapshot = snapshot;
            _settings = settings;
        }

        _currentIcon = SelectIcon(snapshot);
        ModifyTrayIcon(_currentIcon, snapshot.Tooltip);
    }

    public void ShowContextMenuAtCursor()
    {
        PostToUi(ShowContextMenuCore);
    }

    public void Dispose()
    {
        RemoveTrayIcon();

        if (_windowHandle != 0)
        {
            DestroyWindow(_windowHandle);
        }

        if (_windowClassAtom != 0)
        {
            UnregisterClass(_windowClassName, GetModuleHandle(null));
        }
    }

    private void PostToUi(Action action)
    {
        _syncContext.Post(_ => action(), null);
    }

    private void ShowContextMenuCore()
    {
        var (snapshot, settings) = GetState();
        var menu = BuildMenu(snapshot, settings);
        if (menu == 0)
        {
            return;
        }

        try
        {
            TryEnableDarkMode(menu);

            if (!GetCursorPos(out var point))
            {
                return;
            }

            SetForegroundWindow(_windowHandle);
            TrackPopupMenuEx(
                menu,
                TpmLeftAlign | TpmBottomAlign | TpmRightButton,
                point.X,
                point.Y,
                _windowHandle,
                nint.Zero);
            PostMessage(_windowHandle, 0, 0, 0);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private nint BuildMenu(ServiceSnapshot snapshot, TraySettings settings)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            AppendInfoItem(menu, 2001, $"状态：{snapshot.Headline}");

            var statusMenu = CreatePopupMenu();
            if (statusMenu == 0)
            {
                throw new InvalidOperationException("Failed to create status submenu.");
            }

            AppendInfoItem(statusMenu, 2101, $"说明：{snapshot.Detail}");
            AppendInfoItem(statusMenu, 2102, $"地址：{settings.BaseUrl}");
            AppendInfoItem(statusMenu, 2103, $"OpenAI：{settings.OpenAiBaseUrl}");
            AppendInfoItem(statusMenu, 2104, $"Provider：{snapshot.Provider}");
            AppendInfoItem(statusMenu, 2105, $"登录：{FormatLoginState(snapshot)}");
            AppendSubMenu(menu, statusMenu, "状态详情");
            AppendSeparator(menu);

            AppendActionItem(menu, IdStartService, "启动服务", !snapshot.ProcessRunning || !snapshot.HealthOk);
            AppendActionItem(menu, IdStopService, "停止服务", snapshot.ProcessRunning);
            AppendActionItem(menu, IdRestartService, "重启服务");
            AppendSeparator(menu);

            AppendActionItem(menu, IdOpenLogin, "打开 DeepSeek 登录");
            AppendActionItem(menu, IdCheckStatus, "手动检查登录态");
            AppendActionItem(menu, IdOpenHealth, "打开健康检查");
            AppendSeparator(menu);

            var urlMenu = CreatePopupMenu();
            if (urlMenu == 0)
            {
                throw new InvalidOperationException("Failed to create URL submenu.");
            }

            AppendActionItem(urlMenu, IdCopyOpenAiBaseUrl, "复制 OpenAI Base URL");
            AppendActionItem(urlMenu, IdCopyAnthropicBaseUrl, "复制 Anthropic Base URL");
            AppendSubMenu(menu, urlMenu, "客户端地址");

            var settingsMenu = CreatePopupMenu();
            if (settingsMenu == 0)
            {
                throw new InvalidOperationException("Failed to create settings submenu.");
            }

            AppendToggleItem(settingsMenu, IdToggleLaunchAtStartup, "开机自启托盘", settings.LaunchAtStartup);
            AppendToggleItem(settingsMenu, IdToggleStartServiceOnLaunch, "托盘启动时自动启动服务", settings.StartServiceOnLaunch);
            AppendSubMenu(menu, settingsMenu, "偏好设置");

            var fileMenu = CreatePopupMenu();
            if (fileMenu == 0)
            {
                throw new InvalidOperationException("Failed to create file submenu.");
            }

            AppendActionItem(fileMenu, IdOpenProjectDirectory, "打开项目目录");
            AppendActionItem(fileMenu, IdOpenDataDirectory, "打开托盘日志/配置目录");
            AppendSubMenu(menu, fileMenu, "文件");
            AppendSeparator(menu);

            AppendActionItem(menu, IdExit, "退出托盘");
            return menu;
        }
        catch
        {
            DestroyMenu(menu);
            throw;
        }
    }

    private (ServiceSnapshot Snapshot, TraySettings Settings) GetState()
    {
        lock (_stateLock)
        {
            return (_snapshot, _settings);
        }
    }

    private void AppendInfoItem(nint menu, uint id, string text)
    {
        AppendMenuItem(menu, id, text, enabled: false, isChecked: false, subMenu: 0, isSeparator: false);
    }

    private void AppendActionItem(nint menu, uint id, string text, bool enabled = true)
    {
        AppendMenuItem(menu, id, text, enabled, isChecked: false, subMenu: 0, isSeparator: false);
    }

    private void AppendToggleItem(nint menu, uint id, string text, bool isChecked)
    {
        AppendMenuItem(menu, id, text, enabled: true, isChecked, subMenu: 0, isSeparator: false);
    }

    private void AppendSubMenu(nint menu, nint subMenu, string text)
    {
        AppendMenuItem(menu, 0, text, enabled: true, isChecked: false, subMenu, isSeparator: false);
    }

    private void AppendSeparator(nint menu)
    {
        AppendMenuItem(menu, 0, string.Empty, enabled: false, isChecked: false, subMenu: 0, isSeparator: true);
    }

    private static void AppendMenuItem(nint menu, uint id, string text, bool enabled, bool isChecked, nint subMenu, bool isSeparator)
    {
        var item = new MENUITEMINFO
        {
            cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
            fMask = MiimId | MiimState | MiimType
        };

        if (isSeparator)
        {
            item.fType = MftSeparator;
            item.fState = MfsDisabled;
        }
        else
        {
            item.fType = MftString;
            item.fState = (uint)((enabled ? MfsEnabled : MfsDisabled) | (isChecked ? MfsChecked : MfsUnchecked));
            item.wID = id;
            item.dwTypeData = text;
            item.cch = (uint)text.Length;
        }

        if (subMenu != 0)
        {
            item.fMask |= MiimSubmenu;
            item.hSubMenu = subMenu;
            item.fState = MfsEnabled;
            item.dwTypeData = text;
            item.cch = (uint)text.Length;
        }

        if (!InsertMenuItem(menu, uint.MaxValue, true, ref item))
        {
            throw new InvalidOperationException("Failed to insert tray menu item.");
        }
    }

    private void TryEnableDarkMode(nint menu)
    {
        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(_windowHandle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            if (SetPreferredAppMode is not null)
            {
                SetPreferredAppMode(PreferredAppMode.AllowDark);
            }

            FlushMenuThemes?.Invoke();

            if (AllowDarkModeForWindow is not null)
            {
                AllowDarkModeForWindow(_windowHandle, true);
            }
        }
        catch
        {
        }
    }

    private void AddTrayIcon(Icon icon, string tooltip)
    {
        var data = CreateNotifyIconData(icon, tooltip);
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            throw new InvalidOperationException("Failed to add tray icon.");
        }
    }

    private void ReAddTrayIcon()
    {
        AddTrayIcon(_currentIcon, GetState().Snapshot.Tooltip);
    }

    private void ModifyTrayIcon(Icon icon, string tooltip)
    {
        var data = CreateNotifyIconData(icon, tooltip);
        if (!Shell_NotifyIcon(NimModify, ref data))
        {
            Shell_NotifyIcon(NimAdd, ref data);
        }
    }

    private void RemoveTrayIcon()
    {
        var data = CreateNotifyIconData(_stoppedIcon, string.Empty);
        Shell_NotifyIcon(NimDelete, ref data);
    }

    private NOTIFYICONDATA CreateNotifyIconData(Icon icon, string tooltip)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayIcon,
            hIcon = icon.Handle,
            szTip = TrimTooltip(tooltip)
        };
    }

    private nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            ReAddTrayIcon();
            return 0;
        }

        switch (msg)
        {
            case WmTrayIcon:
                HandleTrayMouseMessage((int)lParam);
                return 0;

            case WmCommand:
                HandleCommand((int)(wParam.ToUInt64() & 0xFFFF));
                return 0;

            case WmDestroy:
                RemoveTrayIcon();
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleTrayMouseMessage(int message)
    {
        switch (message)
        {
            case WmRButtonUp:
            case WmLButtonUp:
            case WmLButtonDblClk:
                ShowContextMenuCore();
                break;
        }
    }

    private void HandleCommand(int id)
    {
        switch (id)
        {
            case IdStartService:
                CommandRequested?.Invoke(TrayCommand.StartService);
                break;
            case IdStopService:
                CommandRequested?.Invoke(TrayCommand.StopService);
                break;
            case IdRestartService:
                CommandRequested?.Invoke(TrayCommand.RestartService);
                break;
            case IdOpenLogin:
                CommandRequested?.Invoke(TrayCommand.OpenLogin);
                break;
            case IdCheckStatus:
                CommandRequested?.Invoke(TrayCommand.CheckStatus);
                break;
            case IdOpenHealth:
                CommandRequested?.Invoke(TrayCommand.OpenHealth);
                break;
            case IdCopyOpenAiBaseUrl:
                CommandRequested?.Invoke(TrayCommand.CopyOpenAiBaseUrl);
                break;
            case IdCopyAnthropicBaseUrl:
                CommandRequested?.Invoke(TrayCommand.CopyAnthropicBaseUrl);
                break;
            case IdOpenProjectDirectory:
                CommandRequested?.Invoke(TrayCommand.OpenProjectDirectory);
                break;
            case IdOpenDataDirectory:
                CommandRequested?.Invoke(TrayCommand.OpenDataDirectory);
                break;
            case IdToggleLaunchAtStartup:
                ToggleRequested?.Invoke(TraySettingToggle.LaunchAtStartup, !GetState().Settings.LaunchAtStartup);
                break;
            case IdToggleStartServiceOnLaunch:
                ToggleRequested?.Invoke(TraySettingToggle.StartServiceOnLaunch, !GetState().Settings.StartServiceOnLaunch);
                break;
            case IdExit:
                CommandRequested?.Invoke(TrayCommand.Exit);
                break;
        }
    }

    private static Icon SelectIcon(ServiceSnapshot snapshot)
    {
        if (!snapshot.ProcessRunning || !snapshot.HealthOk)
        {
            return snapshot.ProcessRunning ? SystemIcons.Warning : SystemIcons.Application;
        }

        if (snapshot.NeedsLogin == true || snapshot.LoggedIn == false)
        {
            return SystemIcons.Question;
        }

        return SystemIcons.Information;
    }

    private static string FormatLoginState(ServiceSnapshot snapshot)
    {
        if (snapshot.LoggedIn == true)
        {
            return string.IsNullOrWhiteSpace(snapshot.ExpiresAt)
                ? "有效"
                : $"有效，过期 {snapshot.ExpiresAt}";
        }

        if (snapshot.NeedsLogin == true || snapshot.LoggedIn == false)
        {
            return "需要登录";
        }

        return "未检查";
    }

    private static uint RegisterWindowClass(string className, WndProc wndProc)
    {
        var module = GetModuleHandle(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = wndProc,
            hInstance = module,
            lpszClassName = className
        };

        return RegisterClassEx(ref windowClass);
    }

    private static nint CreateMessageWindow(string className)
    {
        return CreateWindowEx(
            0,
            className,
            className,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            GetModuleHandle(null),
            0);
    }

    private static string TrimTooltip(string text)
    {
        const int max = 127;
        return text.Length <= max ? text : text[..max];
    }

    private static readonly SetPreferredAppModeDelegate? SetPreferredAppMode = LoadSetPreferredAppMode();
    private static readonly FlushMenuThemesDelegate? FlushMenuThemes = LoadUxThemeDelegate<FlushMenuThemesDelegate>(136);
    private static readonly AllowDarkModeForWindowDelegate? AllowDarkModeForWindow = LoadUxThemeDelegate<AllowDarkModeForWindowDelegate>(133);

    private static SetPreferredAppModeDelegate? LoadSetPreferredAppMode()
    {
        return LoadUxThemeDelegate<SetPreferredAppModeDelegate>(135);
    }

    private static TDelegate? LoadUxThemeDelegate<TDelegate>(int ordinal)
        where TDelegate : Delegate
    {
        var module = LoadLibrary("uxtheme.dll");
        if (module == 0)
        {
            return null;
        }

        var address = GetProcAddress(module, (nint)ordinal);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    private enum PreferredAppMode
    {
        Default,
        AllowDark
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate PreferredAppMode SetPreferredAppModeDelegate(PreferredAppMode appMode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void FlushMenuThemesDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool AllowDarkModeForWindowDelegate(nint hwnd, bool allow);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public nint hSubMenu;
        public nint hbmpChecked;
        public nint hbmpUnchecked;
        public nuint dwItemData;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string dwTypeData;
        public uint cch;
        public nint hbmpItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InsertMenuItem(nint hMenu, uint item, bool fByPosition, ref MENUITEMINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackPopupMenuEx(nint hmenu, int uFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetProcAddress(nint hModule, nint lpProcName);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
