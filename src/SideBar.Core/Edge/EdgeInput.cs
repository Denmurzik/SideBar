namespace SideBar.Core.Edge;

/// <summary>Снимок ввода для автомата края: курсор в физических пикселях, время в миллисекундах.</summary>
public readonly record struct EdgeInput(int X, int Y, bool LeftButton, bool FullscreenBusy, long NowMs);

public enum EdgeMode { Slide, InPlace }
