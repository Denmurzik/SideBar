using SideBar.Core.Localization;

namespace SideBar.Core.Nearby;

public enum ShareTileState { Idle, Searching, Choosing, Sending, Done, Failed, OfficialAppRunning }

/// <summary>Тип устройства для глифа плитки; Core не зависит от протокольной библиотеки, App отображает DeviceKind сюда.</summary>
public enum ShareDeviceKind { Other, Phone, Tablet, Laptop }

/// <param name="IsOwn">The device's certificate was found among ours (an own device); the tile marks it.</param>
public sealed record DeviceView(string Id, string Name, ShareDeviceKind Kind, bool IsOwn = false);

/// <summary>
/// Состояния плитки Quick Share: покой, поиск, выбор, отправка, итог. Чистая модель без UI и сети, только UI-поток.
/// Время приходит снаружи (<see cref="BeginShare"/>, <see cref="Tick"/>).
/// </summary>
public sealed class ShareTileModel
{
    public const int MaxDevices = 4;
    public static readonly TimeSpan ChooseTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ResultDuration = TimeSpan.FromSeconds(1.5);

    readonly UiText _text;
    readonly List<DeviceView> _found = [];
    IReadOnlyList<string> _paths = [];
    DateTimeOffset _startedAt, _finishedAt, _clock;

    /// <param name="text">Язык подсказок плитки; по умолчанию русский.</param>
    public ShareTileModel(UiText? text = null) => _text = text ?? UiTexts.Ru;

    public ShareTileState State { get; private set; }

    /// <summary>Не больше <see cref="MaxDevices"/> первых найденных; остальные ждут, пока освободится место.</summary>
    public IReadOnlyList<DeviceView> Devices => _found.Count <= MaxDevices ? _found : _found.GetRange(0, MaxDevices);

    public IReadOnlyList<string> Paths => _paths;
    public string? ChosenId { get; private set; }
    public double Fraction { get; private set; }
    public string? FailReason { get; private set; }

    /// <summary>Пометка к успешному итогу (например, отправлена только часть файлов).</summary>
    public string? Note { get; private set; }

    /// <summary>Карточка удерживается открытой, пока плитка не вернулась в покой, чтобы итог (галочка/крестик/подсказка) был виден.</summary>
    public bool HoldsCardOpen => State != ShareTileState.Idle;

    /// <summary>Карточка не прячется, пока идёт поиск, выбор или передача.</summary>
    public bool KeepsCardOpen => State is ShareTileState.Searching or ShareTileState.Choosing or ShareTileState.Sending;

    /// <summary>A consent covers the tile: the choose timeout does not run meanwhile (the time is given back).</summary>
    public bool Covered { get; set; }

    public event Action? Changed;

    public void BeginShare(IReadOnlyList<string> paths, DateTimeOffset now)
    {
        if (paths.Count == 0 || State == ShareTileState.Sending) return;
        Reset();
        _paths = [.. paths];
        _startedAt = _clock = now;
        State = ShareTileState.Searching;
        Changed?.Invoke();
    }

    /// <summary>Отправка заблокирована: работает официальный Quick Share. Подсказка видна <see cref="ResultDuration"/>; идущий поиск или передачу не трогает.</summary>
    public void ShowOfficialAppRunning(DateTimeOffset now)
    {
        if (State is ShareTileState.Searching or ShareTileState.Choosing or ShareTileState.Sending) return;
        Reset();
        _finishedAt = _clock = now;
        FailReason = _text.OfficialAppHint;
        State = ShareTileState.OfficialAppRunning;
        Changed?.Invoke();
    }

    public void DeviceFound(DeviceView d)
    {
        if (State is not (ShareTileState.Searching or ShareTileState.Choosing)) return;
        int i = _found.FindIndex(x => x.Id == d.Id);
        if (i >= 0) _found[i] = d; else _found.Add(d);
        State = ShareTileState.Choosing;
        Changed?.Invoke();
    }

    public void DeviceLost(string id)
    {
        if (State is not (ShareTileState.Searching or ShareTileState.Choosing)) return;
        if (_found.RemoveAll(x => x.Id == id) == 0) return;
        if (_found.Count == 0) State = ShareTileState.Searching;
        Changed?.Invoke();
    }

    public void Choose(string id)
    {
        if (State != ShareTileState.Choosing || !Devices.Any(x => x.Id == id)) return;
        ChosenId = id;
        Fraction = 0;
        State = ShareTileState.Sending;
        Changed?.Invoke();
    }

    public void Progress(double fraction)
    {
        if (State != ShareTileState.Sending) return;
        Fraction = Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1);
        Changed?.Invoke();
    }

    public void Finished(bool ok, string? reason)
    {
        // неуспех возможен и до выбора устройства (нечего отправлять, не запустился поиск)
        bool valid = State == ShareTileState.Sending || (!ok && State is ShareTileState.Searching or ShareTileState.Choosing);
        if (!valid) return;
        _finishedAt = _clock;
        FailReason = ok ? null : reason;
        Note = ok ? reason : null;
        State = ok ? ShareTileState.Done : ShareTileState.Failed;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        if (State == ShareTileState.Idle) return;
        Reset();
        Changed?.Invoke();
    }

    public void Tick(DateTimeOffset now)
    {
        var elapsed = now - _clock;
        _clock = now;
        if (Covered && State is ShareTileState.Searching or ShareTileState.Choosing && elapsed > TimeSpan.Zero) _startedAt += elapsed;
        switch (State)
        {
            case ShareTileState.Searching or ShareTileState.Choosing when now - _startedAt >= ChooseTimeout:
                _finishedAt = now;
                State = ShareTileState.Failed;
                FailReason = _text.NoDevicesReason;
                Changed?.Invoke();
                break;
            case ShareTileState.Done or ShareTileState.Failed or ShareTileState.OfficialAppRunning when now - _finishedAt >= ResultDuration:
                Reset();
                Changed?.Invoke();
                break;
        }
    }

    void Reset()
    {
        State = ShareTileState.Idle;
        _found.Clear();
        _paths = [];
        ChosenId = null;
        Fraction = 0;
        FailReason = null;
        Note = null;
    }
}
