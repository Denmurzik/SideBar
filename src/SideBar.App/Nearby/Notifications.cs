using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using SideBar.Core.Nearby;
using SideBar.Nearby.Session;

namespace SideBar.Sharing;

/// <summary>
/// Приём внутри карточки: запросы согласия и короткие заметки попадают в <see cref="ReceiveTileModel"/>, плитка Quick Share
/// их рисует. Никаких отдельных окон и уведомлений Windows. В логи попадает только вид заметки.
/// </summary>
sealed partial class Notifications
{
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ShellExecute(nint hwnd, string verb, string file, string? parameters, string? directory, int show);

    readonly DispatcherQueue _ui;
    readonly nint _clipboardOwner;
    readonly ReceiveTileModel _model;
    readonly Dictionary<int, TaskCompletionSource<bool>> _pending = [];   // UI-поток

    public Notifications(DispatcherQueue ui, nint clipboardOwner, ReceiveTileModel model)
    {
        _ui = ui;
        _clipboardOwner = clipboardOwner;
        _model = model;
    }

    /// <summary>Кнопка «Принять»/«Отклонить» в плитке (UI-поток).</summary>
    public void Decide(int consentId, bool accept)
    {
        if (!_pending.Remove(consentId, out var tcs)) return;
        _model.RemoveConsent(consentId, DateTimeOffset.UtcNow);
        tcs.TrySetResult(accept);
    }

    /// <summary>
    /// Запрос «Принять/Отклонить». Завершается true/false по кнопке; по отмене <paramref name="ct"/> (таймаут решения, пир ушёл,
    /// политика сети) запрос убирается из плитки и результат false. Вызывается с любого потока.
    /// </summary>
    public Task<bool> AskAccept(string line, string pin, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _ui.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetResult(false); return; }
            int id = _model.AddConsent(line, pin);
            _pending[id] = tcs;
            Log.Write("notify consent");
            ct.Register(() => _ui.TryEnqueue(() => Decide(id, false)));
        });
        return queued ? tcs.Task : Task.FromResult(false);
    }

    void Note(string kind, string[] lines, params TileButton[] buttons)
    {
        void Add()
        {
            _model.AddNote(lines, buttons, DateTimeOffset.UtcNow);
            Log.Write("notify " + kind);
        }
        if (_ui.HasThreadAccess) Add();
        else _ui.TryEnqueue(Add);
    }

    public void Received(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string path = paths[0];
        // «Открыть» только для известных безопасных типов; остальное (исполняемые, ярлыки, скрипты, неизвестное) — лишь показать в папке
        TileButton[] buttons = paths.Count == 1 && DisplayText.IsSafeToOpen(path)
            ? [new(Strings.Open, () => Launch(path)), new(Strings.ShowInFolder, () => ShowInFolder(path))]
            : [new(Strings.ShowInFolder, () => ShowInFolder(path))];
        Note("received", [Strings.Received(paths.Count)], buttons);
    }

    /// <summary>Текст/ссылка уже в буфере обмена; заметка с превью и кнопками.</summary>
    public void SharedText(string device, TextKind kind, string value)
    {
        string preview = DisplayText.Preview(value);
        TileButton copy = new(Strings.Copy, () => ClipboardText.Set(value, _clipboardOwner));
        if (kind == TextKind.Url && DisplayText.TryHttpUri(value, out var uri))
            Note("link", [Strings.SharedLink(device, uri.IdnHost), preview], new TileButton(Strings.OpenLink, () => Launch(uri.AbsoluteUri)), copy);
        else
            Note("text", [Strings.SharedText(device), preview], copy);
    }

    public void Info(string kind, string text) => Note(kind, [text]);

    static void Launch(string target) => ShellExecute(0, "open", target, null, null, 1);

    static void ShowInFolder(string path) => ShellExecute(0, "open", "explorer.exe", $"/select,\"{path}\"", null, 1);
}
