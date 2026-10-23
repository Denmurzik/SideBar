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
    volatile bool _userResident;

    /// <summary>Чужой (не наш оставшийся) экземпляр работает непрерывно дольше порога: он сам обновляет сертификаты, 72-часовое предупреждение не нужно.</summary>
    public bool UserAppResident => _userResident;

    /// <param name="running">Процесс официального приложения запущен.</param>
    /// <param name="ownCycle">Идёт наш цикл обновления.</param>
    /// <param name="ourLeftover">Живой процесс — тот, что запустили мы (DidNotExit / им начали пользоваться в нашем экземпляре).</param>
    public ResidentNote Observe(bool running, DateTimeOffset now, bool ownCycle, bool ourLeftover = false)
    {
        if (!running || ownCycle)
        {
            _since = null;
            _userResident = false;
            if (!running) _noted = false;
            return ResidentNote.None;
        }
        _since ??= now;
        _userResident = !ourLeftover && now - _since >= threshold;
        if (_noted || now - _since < threshold) return ResidentNote.None;
        _noted = true;
        return ourLeftover ? ResidentNote.OurLeftover : ResidentNote.UserApp;
    }
}
