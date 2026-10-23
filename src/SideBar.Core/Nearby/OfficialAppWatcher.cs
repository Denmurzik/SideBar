using System.Diagnostics;

namespace SideBar.Core.Nearby;

/// <summary>Опрос процесса официального Quick Share (nearby_share.exe) раз в 5 с; любой пользователь и сессия.</summary>
public sealed class OfficialAppWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    readonly Func<bool> _probe;
    readonly object _gate = new();
    Timer? _timer;
    volatile bool _running;

    public OfficialAppWatcher(Func<bool>? probe = null) => _probe = probe ?? IsOfficialAppRunning;

    public bool Running => _running;

    public event Action<bool>? RunningChanged;

    /// <summary>Один опрос (сразу при старте, чтобы первое решение приёма учитывало процесс).</summary>
    public void Poll()
    {
        bool now;
        try { now = _probe(); }
        catch (Exception) { return; }   // сбой опроса не равен «закрыли»: состояние не меняется
        lock (_gate)
        {
            if (now == _running) return;
            _running = now;
        }
        RunningChanged?.Invoke(now);
    }

    public void Start()
    {
        lock (_gate) _timer ??= new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    public static bool IsOfficialAppRunning()
    {
        var found = Process.GetProcessesByName("nearby_share");
        try { return found.Length > 0; }
        finally { foreach (var p in found) p.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }
}
