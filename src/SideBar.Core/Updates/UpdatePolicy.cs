namespace SideBar.Core.Updates;

public static class UpdatePolicy
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    /// <summary>Не чаще раза в 24 ч и не раньше 2 мин после старта. lastCheck в будущем (часы ушли назад) — проверяем, иначе блокировка навсегда.</summary>
    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset startedAt, DateTimeOffset? lastCheck)
    {
        if (startedAt > now) startedAt = now; // часы ушли назад: отсчёт задержки от текущего момента
        if (now - startedAt < StartupGrace) return false;
        return lastCheck is not { } last || last > now || now - last >= MinInterval;
    }
}
