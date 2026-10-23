using SideBar.Core;
using SideBar.Native;

namespace SideBar;

/// <summary>
/// Пока карточка скрыта и обрезку никто не держит — обрезает working set сразу и затем каждые 45 с.
/// Карточка: OnShown/OnHidden; окно настроек: Hold/Release (счётчик, см. <see cref="TrimGate"/>).
/// </summary>
sealed class MemoryTrimmer
{
    const int PeriodMs = 45_000;

    readonly Timer _timer;
    readonly Action? _afterTrim;
    readonly TrimGate _gate = new();

    public MemoryTrimmer(Action? afterTrim = null)
    {
        _afterTrim = afterTrim;
        _timer = new Timer(_ => Trim(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void OnHidden() => Resume(_gate.SetCardShown(false));

    public void OnShown() => Resume(_gate.SetCardShown(true));

    public void Hold() => Resume(_gate.Hold());

    public void Release() => Resume(_gate.Release());

    void Resume(bool allowed)
    {
        if (!allowed) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        Trim();
        _timer.Change(PeriodMs, PeriodMs);
    }

    void Trim()
    {
        if (!_gate.Allowed) return;   // таймер мог сработать одновременно с OnShown/Hold
        WorkingSet.Trim();
        _afterTrim?.Invoke();
    }
}
