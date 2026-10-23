namespace SideBar.Core.Official;

public enum ResidentNote { None, UserApp, OurLeftover }

/// <summary>
/// Официальный Quick Share «живёт» в трее постоянно: пока он запущен, наш стек на паузе и цикл обновления не идёт.
/// Детектор срабатывает один раз за непрерывный запуск, если он длится дольше порога; идущий цикл не считается.
/// Если живой процесс — наш, оставшийся после неудачного цикла, заметка другая.
/// </summary>
public sealed class ResidentAppDetector(TimeSpan threshold)
{
    DateTimeOffset? _since;
    bool _noted;

    /// <param name="running">Процесс официального приложения запущен.</param>
    /// <param name="ownCycle">Идёт наш цикл обновления.</param>
    /// <param name="ourLeftover">Живой процесс — тот, что запустили мы (DidNotExit / им начали пользоваться в нашем экземпляре).</param>
    public ResidentNote Observe(bool running, DateTimeOffset now, bool ownCycle, bool ourLeftover = false)
    {
        if (!running || ownCycle)
        {
            _since = null;
            if (!running) _noted = false;
            return ResidentNote.None;
        }
        _since ??= now;
        if (_noted || now - _since < threshold) return ResidentNote.None;
        _noted = true;
        return ourLeftover ? ResidentNote.OurLeftover : ResidentNote.UserApp;
    }
}
