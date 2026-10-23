namespace SideBar.Core;

/// <summary>
/// Когда можно обрезать working set: карточка скрыта и никто не держит обрезку (окно настроек). Счётчик удержаний, а не флаг:
/// закрытие окна настроек при открытой карточке (и наоборот) не включает обрезку раньше времени. Потокобезопасен.
/// </summary>
public sealed class TrimGate
{
    readonly object _gate = new();
    bool _cardShown;
    int _holds;

    public bool Allowed { get { lock (_gate) return !_cardShown && _holds == 0; } }

    /// <summary>Карточка показана/скрыта; возвращает <see cref="Allowed"/> после изменения.</summary>
    public bool SetCardShown(bool shown) { lock (_gate) { _cardShown = shown; return !_cardShown && _holds == 0; } }

    public bool Hold() { lock (_gate) { _holds++; return false; } }

    public bool Release() { lock (_gate) { if (_holds > 0) _holds--; return !_cardShown && _holds == 0; } }
}
