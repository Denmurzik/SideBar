using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SideBar.Core;
using SideBar.Core.Settings;
using SideBar.Edge;
using SideBar.Nearby.Identity;
using SideBar.Sharing;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CoreVisibility = SideBar.Core.Nearby.Visibility;

namespace SideBar.Settings;

/// <summary>Всё, что окну нужно от приложения (окно создаётся по требованию и ничего не хранит дольше своей жизни).</summary>
sealed record SettingsDeps(SettingsStore Store, Autostart Autostart, string ExePath, ReceiveHost Receive, IdentityProvider Identity,
    Action ClearShelf, Action Opened, Action Closed);

/// <summary>
/// Окно настроек: единственное окно SideBar, которое активируется. Одно на процесс; закрытие освобождает его.
/// Значения читаются из <see cref="SettingsStore"/>/ReceiveHost/Autostart и обновляются по их событиям; запись применяется сразу.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    const double DefaultWidth = 900, DefaultHeight = 640, MinWidth = 640, MinHeight = 480;   // epx
    const string DefaultOfficialPath = @"C:\Program Files\Google\NearbyShare\nearby_share.exe";
    const string RepoUrl = "https://github.com/Denmurzik/SideBar";
    static readonly TimeSpan ClearArmWindow = TimeSpan.FromSeconds(5);

    static SettingsWindow? s_current;

    /// <summary>Перечитать сертификаты официального Quick Share сейчас (M5). null — кнопка недоступна.</summary>
    internal static Func<Task>? RefreshCertificatesNow { get; set; }

    /// <summary>Проверить обновления сейчас (M7): текст итога для карточки либо null. null — кнопка недоступна.</summary>
    internal static Func<Task<string?>>? CheckForUpdatesNow { get; set; }

    internal static void ShowOrActivate(SettingsDeps deps, int? section = null)
    {
        var created = s_current is null;
        s_current ??= new SettingsWindow(deps);
        Log.Write(created ? "settings window opened" : "settings window raised");
        s_current.Raise(section);
#if DEBUG
        if (created) s_weak = new WeakReference(s_current);
#endif
    }

#if DEBUG
    static WeakReference? s_weak;

    // самопроверка без ввода (SIDEBAR_DEBUG_SETTINGS=selftest): закрыть окно и проверить, что сборка мусора его освобождает
    internal static void DebugClose() => s_current?.Close();

    internal static void DebugReport()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Log.Write("settings window released=" + (s_weak is { IsAlive: false }) + " current=" + (s_current is not null) + " ws=" + Environment.WorkingSet / (1 << 20) + "MB gc=" + GC.GetTotalMemory(false) / (1 << 20) + "MB");
    }
#endif

    readonly SettingsDeps _deps;
    readonly SettingsStore _store;
    readonly DispatcherQueue _ui;
    readonly nint _hwnd;
    readonly UIElement[] _pages = new UIElement[5];
    readonly string[] _titles;
    readonly List<Action> _syncs = [];
    readonly Action<AppSettings> _onStoreChanged;
    readonly Action _onExternalChange;
    DispatcherTimer? _clearTimer;
    bool _syncing, _closed, _clearArmed, _recheckPaths;
    int _certGeneration;
    Button? _clearButton;
    int _page = -1;

    SettingsWindow(SettingsDeps deps)
    {
        _deps = deps;
        _store = deps.Store;
        _ui = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Title = Strings.SettingsTitle;
        SystemBackdrop = new MicaBackdrop();
        _titles = [Strings.NavGeneral, Strings.NavQuickShare, Strings.NavPanel, Strings.NavShelf, Strings.NavAbout];
        NavGeneral.Content = _titles[0];
        NavQuickShare.Content = _titles[1];
        NavPanel.Content = _titles[2];
        NavShelf.Content = _titles[3];
        NavAbout.Content = _titles[4];

        _hwnd = WindowNative.GetWindowHandle(this);
        Place();

        _pages[0] = BuildGeneral();
        _pages[1] = BuildQuickShare();
        _pages[2] = BuildPanel();
        _pages[3] = BuildShelf();
        _pages[4] = BuildAbout();

        _onStoreChanged = _ => Post(SyncAll);
        _onExternalChange = () => Post(SyncAll);
        _store.Changed += _onStoreChanged;
        deps.Receive.ModeChanged += _onExternalChange;
        deps.Identity.Changed += _onExternalChange;
        try
        {
            // автозапуск, сертификаты и пути могли измениться снаружи; уход фокуса ничего не проверяет
            Activated += (_, e) =>
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated) return;
                _recheckPaths = true;
                try { SyncAll(); }
                finally { _recheckPaths = false; }
            };
            Closed += OnClosed;

            Nav.SelectedItem = NavGeneral;
            SyncAll();
            deps.Opened();
        }
        catch
        {
            // конструктор упал после подписок: без отписки store держал бы мёртвое окно
            Unsubscribe();
            throw;
        }
    }

    void Unsubscribe()
    {
        _store.Changed -= _onStoreChanged;
        _deps.Receive.ModeChanged -= _onExternalChange;
        _deps.Identity.Changed -= _onExternalChange;
    }

    // по центру основного монитора, физические px
    void Place()
    {
        var work = Displays.PrimaryWorkArea();
        double scale = Displays.PrimaryScale();
        int w = Math.Min((int)(DefaultWidth * scale), work.Right - work.Left);
        int h = Math.Min((int)(DefaultHeight * scale), work.Bottom - work.Top);
        int x = work.Left + (work.Right - work.Left - w) / 2;
        int y = work.Top + (work.Bottom - work.Top - h) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            // PreferredMinimum* не документированы как epx: считаем физическими пикселями (на 100% совпадает)
            p.PreferredMinimumWidth = (int)(MinWidth * scale);
            p.PreferredMinimumHeight = (int)(MinHeight * scale);
        }
    }

    void Raise(int? section)
    {
        if (_closed) return;
        if (section is >= 0 and <= 4) Nav.SelectedItem = Nav.MenuItems[section.Value];
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        AppWindow.Show();
        Activate();   // единственное окно SideBar, которое активируется
    }

    void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        Unsubscribe();
        _clearTimer?.Stop();
        _clearTimer = null;
        _syncs.Clear();
        Scroll.Content = null;   // страницы держат обработчики, указывающие на окно
        Array.Clear(_pages);
        Content = null;
        if (ReferenceEquals(s_current, this)) s_current = null;
        Log.Write("settings window closed");
        _deps.Closed();
    }

    void Post(Action a) => _ui.TryEnqueue(() => { if (!_closed) a(); });

    void SyncAll()
    {
        if (_closed) return;
        _syncing = true;
        try { foreach (var s in _syncs) s(); }
        finally { _syncing = false; }
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag } || !int.TryParse(tag, out var i) || i == _page) return;
        _page = i;
        Nav.Header = _titles[i];
        Scroll.Content = _pages[i];
        Scroll.ChangeView(0, 0, null, disableAnimation: true);
    }

    // ---------- построение страниц ----------

    static StackPanel Page(params SettingCard[] cards)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(36, 8, 36, 0), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in cards) panel.Children.Add(c.Root);
        return panel;
    }

    SettingCard Card(string title, string? desc = null, params UIElement[] controls) => new(Root, title, desc, controls);

    ToggleSwitch Toggle(Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set)
    {
        var t = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0 };
        t.Toggled += (_, _) => { if (!_syncing) _store.Update(s => set(s, t.IsOn)); };
        _syncs.Add(() => t.IsOn = get(_store.Current));
        return t;
    }

    ComboBox Choice<T>(T[] values, string[] labels, Func<AppSettings, T> get, Func<AppSettings, T, AppSettings> set) where T : struct, Enum
    {
        var c = new ComboBox { MinWidth = 160 };
        foreach (var l in labels) c.Items.Add(l);
        c.SelectionChanged += (_, _) =>
        {
            if (_syncing || c.SelectedIndex < 0) return;
            var v = values[c.SelectedIndex];
            _store.Update(s => set(s, v));
        };
        _syncs.Add(() => c.SelectedIndex = Array.IndexOf(values, get(_store.Current)));
        return c;
    }

    // проверки существования идут в фоне: сетевой путь не должен вешать окно
    void Background<T>(Func<T> work, Action<T> done)
    {
        _ = Task.Run(() =>
        {
            T value;
            try { value = work(); }
            catch (Exception) { return; }
            Post(() => done(value));
        });
    }

    UIElement BuildGeneral()
    {
        var autostart = new ToggleSwitch { OnContent = Strings.On, OffContent = Strings.Off, MinWidth = 0 };
        autostart.Toggled += (_, _) =>
        {
            if (_syncing) return;
            try { if (autostart.IsOn) _deps.Autostart.Enable(_deps.ExePath); else _deps.Autostart.Disable(); }
            catch (Exception e) { Log.Write("autostart change failed: " + Log.Describe(e)); }
            SyncAll();
        };
        _syncs.Add(() => { try { autostart.IsOn = _deps.Autostart.IsEnabled; } catch (Exception) { } });

        var language = Choice([Language.Auto, Language.Russian, Language.English], [Strings.LanguageAuto, Strings.LanguageRu, Strings.LanguageEn],
            s => s.Language, (s, v) => s with { Language = v });

        var updates = Toggle(s => s.CheckForUpdates, (s, on) => s with { CheckForUpdates = on });

        var checkNow = new Button { Content = Strings.CheckNow };
        var checkCard = Card(Strings.CheckNow, null, checkNow);
        void RenderCheck()
        {
            checkNow.IsEnabled = CheckForUpdatesNow is not null;
            if (CheckForUpdatesNow is null) checkCard.Description = Strings.CheckNowSoon;
        }
        checkNow.Click += async (_, _) =>
        {
            if (CheckForUpdatesNow is not { } check) return;
            checkNow.IsEnabled = false;
            checkCard.Description = Strings.CheckingNow;
            try { checkCard.Description = await check() ?? Strings.UpToDate; }
            catch (Exception e) { Log.Write("update check failed: " + Log.Describe(e)); checkCard.Description = Strings.CheckFailed; }
            if (!_closed) checkNow.IsEnabled = CheckForUpdatesNow is not null;
        };
        RenderCheck();

        var releases = new HyperlinkButton { Content = Strings.ReleasesLink, NavigateUri = new Uri(RepoUrl + "/releases") };
        return Page(
            Card(Strings.Autostart, Strings.AutostartDesc, autostart),
            Card(Strings.LanguageTitle, Strings.LanguageDesc, language),
            Card(Strings.CheckUpdatesTitle, Strings.CheckUpdatesDesc(VersionText()), updates),
            checkCard,
            Card(Strings.ReleasesTitle, null, releases));
    }

    static string VersionText() =>
        SideBar.Updates.UpdateChecker.CurrentVersion?.ToString() ?? "";

    UIElement BuildQuickShare()
    {
        // видимость: та же модель, что в трее
        var hidden = new RadioButton { Content = Strings.VisibilityHidden, GroupName = "vis", MinWidth = 0 };
        var own = new RadioButton { Content = Strings.VisibilityOwnDevices, GroupName = "vis", MinWidth = 0 };
        var everyone = new RadioButton { Content = Strings.VisibilityEveryone, GroupName = "vis", MinWidth = 0 };
        void Choose(CoreVisibility mode, RadioButton b)
        {
            b.Checked += (_, _) =>
            {
                if (_syncing) return;
                _deps.Receive.SetMode(mode);
                SyncAll();
            };
        }
        Choose(CoreVisibility.Hidden, hidden);
        Choose(CoreVisibility.OwnDevices, own);
        Choose(CoreVisibility.Everyone, everyone);
        var radios = new StackPanel { Spacing = 0 };
        radios.Children.Add(hidden);
        radios.Children.Add(own);
        radios.Children.Add(everyone);
        _syncs.Add(() =>
        {
            own.IsEnabled = _deps.Receive.OwnDevicesAvailable;
            switch (_deps.Receive.Mode)
            {
                case CoreVisibility.OwnDevices: own.IsChecked = true; break;
                case CoreVisibility.Everyone: everyone.IsChecked = true; break;
                default: hidden.IsChecked = true; break;
            }
        });

        // имя ПК
        var name = new TextBox { Width = 240, MaxLength = 32, PlaceholderText = Environment.MachineName };
        void CommitName()
        {
            if (_syncing) return;
            var v = SettingsStore.SanitizePcName(name.Text);
            _store.Update(s => s with { PcName = v });
            if (name.Text != (v ?? "")) name.Text = v ?? "";   // показываем то, что реально сохранено (санитизация, обрезка)
            SyncAll();
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            CommitName();
            e.Handled = true;
        };
        _syncs.Add(() => { if (name.FocusState == FocusState.Unfocused) name.Text = _store.Current.PcName ?? ""; });

        // папка для принятых файлов
        var folderChange = new Button { Content = Strings.Change };
        var folderReset = new Button { Content = Strings.Reset };
        var folderCard = Card(Strings.ReceiveFolderTitle, null, folderChange, folderReset);
        folderChange.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _hwnd);
                if (await picker.PickSingleFolderAsync() is { } folder) _store.Update(s => s with { ReceiveFolder = folder.Path });
            }
            catch (Exception e) { Log.Write("folder picker failed: " + Log.Describe(e)); }
        };
        folderReset.Click += (_, _) => _store.Update(s => s with { ReceiveFolder = null });
        string? checkedFolder = null;
        var folderChecked = false;
        _syncs.Add(() =>
        {
            var f = _store.Current.ReceiveFolder;
            folderCard.Description = f ?? Strings.DownloadsName;
            folderReset.Visibility = f is null ? Visibility.Collapsed : Visibility.Visible;
            if (folderChecked && f == checkedFolder && !_recheckPaths) return;   // значение то же: фоновая проверка не нужна
            folderChecked = true;
            checkedFolder = f;
            folderCard.Warning = null;
            if (f is not null)
                Background(() => SideBar.Settings.PathProbe.DirectoryExists(f), ok => { if (_store.Current.ReceiveFolder == f) folderCard.Warning = ok ? null : Strings.ReceiveFolderMissing; });
        });

        // сертификаты
        var certsCard = Card(Strings.CertsTitle, Strings.CertsLoading);
        _syncs.Add(() =>
        {
            var gen = ++_certGeneration;   // устаревший ответ не затирает более новый
            Background(CertStatus, text => { if (gen == _certGeneration) certsCard.Description = text; });
        });
        var refresh = new Button { Content = Strings.Refresh };
        var refreshCard = Card(Strings.CertsRefreshTitle, null, refresh);
        void RenderRefresh()
        {
            refresh.IsEnabled = RefreshCertificatesNow is not null;
            refreshCard.Description = RefreshCertificatesNow is null ? Strings.CertsRefreshSoon : Strings.CertsRefreshDesc;
        }
        refresh.Click += async (_, _) =>
        {
            if (RefreshCertificatesNow is not { } run) return;
            refresh.IsEnabled = false;
            try { await run(); }
            catch (Exception e) { Log.Write("certs refresh failed: " + Log.Describe(e)); }
            if (_closed) return;
            RenderRefresh();
            SyncAll();
        };
        RenderRefresh();

        // путь к официальному Quick Share
        var exeChange = new Button { Content = Strings.Change };
        var exeReset = new Button { Content = Strings.Reset };
        var exeCard = Card(Strings.OfficialPathTitle, null, exeChange, exeReset);
        exeChange.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(picker, _hwnd);
                if (await picker.PickSingleFileAsync() is { } file) _store.Update(s => s with { OfficialExePath = file.Path });
            }
            catch (Exception e) { Log.Write("exe picker failed: " + Log.Describe(e)); }
        };
        exeReset.Click += (_, _) => _store.Update(s => s with { OfficialExePath = null });
        string? checkedExe = null;
        _syncs.Add(() =>
        {
            var p = _store.Current.OfficialExePath;
            var effective = p ?? DefaultOfficialPath;
            exeCard.Description = effective;
            exeReset.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
            if (checkedExe == effective && !_recheckPaths) return;
            checkedExe = effective;
            exeCard.Warning = null;
            Background(() => Path.IsPathFullyQualified(effective) && SideBar.Settings.PathProbe.FileExists(effective),
                ok => { if ((_store.Current.OfficialExePath ?? DefaultOfficialPath) == effective) exeCard.Warning = ok ? null : Strings.OfficialPathMissing; });
        });

        return Page(
            Card(Strings.VisibilityTitle, Strings.VisibilityDesc, radios),
            Card(Strings.PcNameTitle, Strings.PcNameDesc, name),
            folderCard, certsCard, refreshCard, exeCard);
    }

    // состояние сертификатов; читается в фоне (Failure ждёт перезагрузку)
    string CertStatus()
    {
        var id = _deps.Identity;
        if (id.Failure is { } f)
            return Strings.CertsFailed(f switch
            {
                CertLoadFailure.Missing => Strings.CertsMissing,
                CertLoadFailure.Unreadable => Strings.CertsUnreadable,
                CertLoadFailure.FormatChanged => Strings.CertsFormat,
                _ => Strings.CertsNoValid,
            });
        if (id.Current is not null && id.Summary is { } s)
            return Strings.CertsLoaded(s.PeerCount, s.NextExpiry.ToLocalTime().ToString("dd.MM.yyyy"));
        return Strings.CertsLoading;
    }

    UIElement BuildPanel()
    {
        var delay = Choice([EdgeOpenDelay.Ms0, EdgeOpenDelay.Ms150, EdgeOpenDelay.Ms300], [Strings.Ms(0), Strings.Ms(150), Strings.Ms(300)],
            s => s.EdgeOpenDelay, (s, v) => s with { EdgeOpenDelay = v });
        var bounce = Toggle(s => s.BounceOnIncoming, (s, on) => s with { BounceOnIncoming = on });
        var fullscreen = Toggle(s => s.ShowInFullscreen, (s, on) => s with { ShowInFullscreen = on });
        return Page(
            Card(Strings.EdgeDelayTitle, Strings.EdgeDelayDesc, delay),
            Card(Strings.BounceTitle, Strings.BounceDesc, bounce),
            Card(Strings.FullscreenTitle, Strings.FullscreenDesc, fullscreen));
    }

    UIElement BuildShelf()
    {
        var retention = Choice([ShelfRetention.Never, ShelfRetention.Day1, ShelfRetention.Days7], [Strings.RetentionNever, Strings.RetentionDay, Strings.RetentionWeek],
            s => s.ShelfRetention, (s, v) => s with { ShelfRetention = v });
        var clear = new Button { Content = Strings.Clear };
        _clearButton = clear;
        clear.Click += (_, _) =>
        {
            if (!_clearArmed)
            {
                _clearArmed = true;
                clear.Content = Strings.ClearSure;
                _clearTimer ??= new DispatcherTimer { Interval = ClearArmWindow };
                _clearTimer.Tick -= DisarmClear;
                _clearTimer.Tick += DisarmClear;
                _clearTimer.Stop();
                _clearTimer.Start();
                return;
            }
            DisarmClear(null, null);
            try { _deps.ClearShelf(); }
            catch (Exception e) { Log.Write("shelf clear failed: " + Log.Describe(e)); }
        };
        return Page(
            Card(Strings.RetentionTitle, null, retention),
            Card(Strings.ClearShelfTitle, Strings.ClearShelfDesc, clear));
    }

    void DisarmClear(object? sender, object? e)
    {
        _clearTimer?.Stop();
        _clearArmed = false;
        if (_clearButton is not null) _clearButton.Content = Strings.Clear;
    }

    UIElement BuildAbout()
    {
        var repo = new HyperlinkButton { Content = Strings.AboutRepoLink, NavigateUri = new Uri(RepoUrl) };
        return Page(
            Card(Strings.AppName, Strings.CheckUpdatesDesc(VersionText())),
            Card(Strings.AboutLicense, Strings.AboutLicenseValue),
            Card(Strings.AboutRepo, null, repo));
    }
}
