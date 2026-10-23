namespace SideBar.Core.Official;

public enum RefreshOutcome
{
    Succeeded, NotInstalled,
    /// <summary>Официальное приложение уже запущено (пользователем): не запускали и не трогали.</summary>
    OfficialAlreadyRunning,
    /// <summary>Наш цикл уже идёт (параллельный вызов).</summary>
    CycleInProgress,
    MarkerTimeout, UserTookOver, DidNotExit,
}

/// <summary>Какую заметку показать после ручного обновления (тексты — в Strings приложения).</summary>
public enum RefreshNote { None, Done, NotInstalled, OfficialOpen, Deferred }

public static class RefreshNotes
{
    public static RefreshNote For(RefreshOutcome outcome) => outcome switch
    {
        RefreshOutcome.Succeeded => RefreshNote.Done,
        RefreshOutcome.NotInstalled => RefreshNote.NotInstalled,
        RefreshOutcome.OfficialAlreadyRunning => RefreshNote.OfficialOpen,
        RefreshOutcome.CycleInProgress => RefreshNote.None,   // цикл уже идёт и сам покажет итог тому, кто его запустил
        _ => RefreshNote.Deferred,                            // MarkerTimeout / DidNotExit / UserTookOver
    };
}
