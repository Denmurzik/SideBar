namespace SideBar.Core.Nearby;

/// <summary>Пауза своего стека Quick Share (приём и отправка): Pause/Resume вызывает и M5 (скрытый запуск nearby_share.exe).</summary>
public interface IStackPause
{
    bool IsPaused { get; }
    void Pause();

    /// <summary>Снимает только свою паузу: пока жив официальный процесс, стек остаётся на паузе.</summary>
    void Resume();

    event Action? Changed;
}

/// <summary>Пауза = работает официальное приложение (авторитет: <see cref="OfficialAppWatcher"/>) или кто-то явно вызвал <see cref="Pause"/>.</summary>
public sealed class StackPause : IStackPause
{
    readonly object _gate = new();
    bool _official, _manual;

    public bool IsPaused
    {
        get { lock (_gate) return _official || _manual; }
    }

    public event Action? Changed;

    public void SetOfficialRunning(bool running) => Set(ref _official, running);
    public void Pause() => Set(ref _manual, true);
    public void Resume() => Set(ref _manual, false);

    void Set(ref bool field, bool value)
    {
        lock (_gate)
        {
            bool before = _official || _manual;
            field = value;
            if (before == (_official || _manual)) return;
        }
        Changed?.Invoke();
    }
}
