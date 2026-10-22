using SideBar.Native;

namespace SideBar;

/// <summary>Пока карточка скрыта — обрезает working set сразу и затем каждые 45 с.</summary>
sealed class MemoryTrimmer
{
    const int PeriodMs = 45_000;

    readonly Timer _timer;
    readonly Action? _afterTrim;

    public MemoryTrimmer(Action? afterTrim = null)
    {
        _afterTrim = afterTrim;
        _timer = new Timer(_ => Trim(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void OnHidden()
    {
        Trim();
        _timer.Change(PeriodMs, PeriodMs);
    }

    public void OnShown() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    void Trim()
    {
        WorkingSet.Trim();
        _afterTrim?.Invoke();
    }
}
