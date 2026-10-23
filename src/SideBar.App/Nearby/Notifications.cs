using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using SideBar.Core.Nearby;
using SideBar.Nearby.Session;

namespace SideBar.Sharing;

/// <summary>
/// Уведомления Windows (AppNotificationManager, unpackaged). В логи попадает только вид уведомления.
/// Обработчики кнопок хранятся в памяти по идентификатору и выполняются на UI-потоке.
/// </summary>
sealed partial class Notifications : IDisposable
{
    const int MaxActions = 64;

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);

    readonly DispatcherQueue _ui;
    readonly nint _clipboardOwner;
    readonly object _gate = new();
    readonly Dictionary<string, Action> _actions = new();
    readonly Queue<string> _order = new();
    int _seq;
    bool _registered;
    Action<PromptSpec, CancellationToken>? _fallback;

    public Notifications(DispatcherQueue ui, nint clipboardOwner)
    {
        _ui = ui;
        _clipboardOwner = clipboardOwner;
    }

    /// <summary>false: уведомления Windows недоступны (регистрация не удалась), используется запасная панель.</summary>
    public bool Available => _registered;

    /// <summary>Запасная панель (UI-поток) для случая, когда системные уведомления недоступны.</summary>
    public void SetFallback(Action<PromptSpec, CancellationToken> fallback) => _fallback = fallback;

    public void Register()
    {
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnInvoked;
            manager.Register();
            _registered = true;
            Log.Write("notifications registered");
        }
        catch (Exception e)
        {
            Log.Write("notifications register failed: " + Log.Describe(e));
        }
    }

    void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        string arg = args.Argument ?? "";
        const string key = "a=";
        if (!arg.StartsWith(key, StringComparison.Ordinal)) return;
        string id = arg[key.Length..];
        Action? action;
        lock (_gate) _actions.TryGetValue(id, out action);
        if (action is null) return;
        _ui.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception e) { Log.Write("notification action failed: " + Log.Describe(e)); }
        });
    }

    string AddAction(Action action)
    {
        string id = "n" + Interlocked.Increment(ref _seq);
        lock (_gate)
        {
            _actions[id] = action;
            _order.Enqueue(id);
            while (_order.Count > MaxActions) _actions.Remove(_order.Dequeue());
        }
        return id;
    }

    static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    static string Xml(string[] lines, (string Label, string Id)[] buttons, bool reminder)
    {
        var sb = new StringBuilder();
        sb.Append(reminder ? "<toast scenario=\"reminder\">" : "<toast>");
        sb.Append("<visual><binding template=\"ToastGeneric\">");
        foreach (var l in lines) sb.Append("<text>").Append(Esc(l)).Append("</text>");
        sb.Append("</binding></visual>");
        if (buttons.Length > 0)
        {
            sb.Append("<actions>");
            foreach (var (label, id) in buttons)
                sb.Append("<action content=\"").Append(Esc(label)).Append("\" arguments=\"a=").Append(id).Append("\" activationType=\"background\"/>");
            sb.Append("</actions>");
        }
        sb.Append("</toast>");
        return sb.ToString();
    }

    /// <summary>
    /// Показывает уведомление Windows, а если они недоступны или показ не удался — запасную панель.
    /// <paramref name="sticky"/>: не исчезает само (запрос согласия); иначе запасная панель скрывается через 8 с.
    /// </summary>
    bool Notify(string kind, string[] lines, (string Label, Action Act)[] buttons, bool sticky, string? tag = null, CancellationToken ct = default, string? pin = null)
    {
        if (_registered && ShowToast(kind, pin is null ? lines : [.. lines, Strings.Pin(pin)], buttons, sticky, tag)) return true;
        if (_fallback is not { } fallback) return false;
        var spec = new PromptSpec(lines, pin, buttons, sticky ? null : TimeSpan.FromSeconds(8));
        _ui.TryEnqueue(() => fallback(spec, ct));
        Log.Write("notify " + kind + " (panel)");
        return true;
    }

    bool ShowToast(string kind, string[] lines, (string Label, Action Act)[] buttons, bool sticky, string? tag)
    {
        try
        {
            var ids = buttons.Select(b => (b.Label, Id: AddAction(b.Act))).ToArray();
            var n = new AppNotification(Xml(lines, ids, reminder: sticky));
            if (tag is not null) n.Tag = tag;
            AppNotificationManager.Default.Show(n);
            Log.Write("notify " + kind);
            return true;
        }
        catch (Exception e)
        {
            Log.Write("notify failed " + kind + ": " + Log.Describe(e));
            return false;
        }
    }

    void Remove(string tag)
    {
        if (!_registered) return;
        try { _ = AppNotificationManager.Default.RemoveByTagAsync(tag); }
        catch (Exception e) { Log.Write("notify remove failed: " + Log.Describe(e)); }
    }

    /// <summary>
    /// Запрос «Принять/Отклонить». Завершается true/false по кнопке; по отмене <paramref name="ct"/> (таймаут решения, пир ушёл)
    /// уведомление/панель убираются и результат false. Если показать запрос нельзя — false.
    /// </summary>
    public Task<bool> AskAccept(string line, string pin, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string tag = "c" + Interlocked.Increment(ref _seq);
        var buttons = new (string, Action)[]
        {
            (Strings.Accept, () => tcs.TrySetResult(true)),
            (Strings.Decline, () => tcs.TrySetResult(false)),
        };
        if (!Notify("consent", [line], buttons, sticky: true, tag, ct, pin)) return Task.FromResult(false);

        var reg = ct.Register(() =>
        {
            if (tcs.TrySetResult(false)) Remove(tag);
        });
        _ = tcs.Task.ContinueWith(_ =>
        {
            reg.Dispose();
            Remove(tag);   // решение принято: запрос из центра уведомлений больше не нужен
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    public void Received(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string path = paths[0];
        (string, Action)[] buttons = paths.Count == 1
            ? [(Strings.Open, () => { if (DisplayText.IsExecutableLike(path)) ShowInFolder(path); else Launch(path); }), (Strings.ShowInFolder, () => ShowInFolder(path))]
            : [(Strings.ShowInFolder, () => ShowInFolder(path))];
        Notify("received", [Strings.Received(paths.Count)], buttons, sticky: false);
    }

    /// <summary>Текст/ссылка уже в буфере обмена; уведомление с превью и кнопками.</summary>
    public void SharedText(string device, TextKind kind, string value)
    {
        string preview = DisplayText.Preview(value);
        (string, Action) copy = (Strings.Copy, () => ClipboardText.Set(value, _clipboardOwner));
        if (kind == TextKind.Url && DisplayText.TryHttpUri(value, out var uri))
            Notify("link", [Strings.SharedLink(device, uri.Host), preview], [(Strings.Open, () => Launch(uri.AbsoluteUri)), copy], sticky: false);
        else
            Notify("text", [Strings.SharedText(device), preview], [copy], sticky: false);
    }

    public void Info(string kind, string text) => Notify(kind, [text], [], sticky: false);

    static void Launch(string target) => ShellExecute(0, "open", target, null, null, 1);

    static void ShowInFolder(string path) => ShellExecute(0, "open", "explorer.exe", $"/select,\"{path}\"", null, 1);

    public void Dispose()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception) { }
        _registered = false;
    }
}
