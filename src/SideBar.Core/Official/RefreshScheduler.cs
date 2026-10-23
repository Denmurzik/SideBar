namespace SideBar.Core.Official;

public enum RefreshDecision
{
    /// <summary>Пока не нужно (или нельзя: приложения нет / оно уже запущено).</summary>
    Wait,
    /// <summary>Пора, но пользователь не простаивает ≥ 5 мин: проверить позже.</summary>
    RunWhenIdle,
    /// <summary>Запускать цикл обновления сейчас.</summary>
    RunNow,
}

/// <summary>Чистая логика «пора ли обновлять сертификаты официальным Quick Share»: ни часов, ни вызовов ОС.</summary>
public sealed class RefreshScheduler
{
    public static readonly TimeSpan DueAfter = TimeSpan.FromHours(20);
    public static readonly TimeSpan IdleRequired = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ForceAfter = TimeSpan.FromHours(48);
    public static readonly TimeSpan WarnAfter = TimeSpan.FromHours(72);

    /// <summary>Нет приложения или оно запущено — Wait. Ни одного успеха — RunNow. ≥ 20 ч — RunNow при простое ≥ 5 мин, иначе RunWhenIdle; &gt; 48 ч — RunNow сразу.</summary>
    public RefreshDecision Decide(DateTimeOffset now, DateTimeOffset? lastSuccess, TimeSpan idle, bool officialInstalled, bool officialRunning)
    {
        if (!officialInstalled || officialRunning) return RefreshDecision.Wait;
        if (lastSuccess is null) return RefreshDecision.RunNow;
        var age = now - lastSuccess.Value;
        if (age > ForceAfter) return RefreshDecision.RunNow;
        if (age < DueAfter) return RefreshDecision.Wait;
        return idle >= IdleRequired ? RefreshDecision.RunNow : RefreshDecision.RunWhenIdle;
    }

    /// <summary>Без запоминания предупреждения: «&gt; 72 ч без успеха». Для «один раз» см. перегрузку с <c>lastWarned</c>.</summary>
    public bool ShouldWarn(DateTimeOffset now, DateTimeOffset? lastSuccess) => ShouldWarn(now, lastSuccess, null);

    /// <summary>
    /// &gt; 72 ч с последнего успеха и в этом периоде без успеха ещё не предупреждали (<paramref name="lastWarned"/> раньше последнего успеха или null).
    /// Без <paramref name="lastSuccess"/> опоры для отсчёта нет — false.
    /// </summary>
    public bool ShouldWarn(DateTimeOffset now, DateTimeOffset? lastSuccess, DateTimeOffset? lastWarned)
    {
        if (lastSuccess is null || now - lastSuccess.Value <= WarnAfter) return false;
        return lastWarned is null || lastWarned.Value < lastSuccess.Value;
    }
}
