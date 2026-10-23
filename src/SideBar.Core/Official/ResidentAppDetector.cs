namespace SideBar.Core.Official;

/// <summary>
/// Официальный Quick Share «живёт» в трее постоянно: пока он запущен, наш стек на паузе и цикл обновления не идёт.
/// Детектор срабатывает один раз за непрерывный запуск, если он длится дольше порога; наш собственный цикл не считается.
/// </summary>
public sealed class ResidentAppDetector(TimeSpan threshold)
{
    DateTimeOffset? _since;
    bool _noted;

    /// <param name="running">Процесс официального приложения запущен.</param>
    /// <param name="ownCycle">Запущен нашим циклом обновления (не пользователем).</param>
    /// <returns>true — показать заметку (единожды за запуск).</returns>
    public bool Observe(bool running, DateTimeOffset now, bool ownCycle)
    {
        if (!running || ownCycle)
        {
            _since = null;
            if (!running) _noted = false;
            return false;
        }
        _since ??= now;
        if (_noted || now - _since < threshold) return false;
        _noted = true;
        return true;
    }
}
