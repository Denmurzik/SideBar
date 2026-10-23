namespace SideBar.Core.Shelf;

/// <summary>Положение одного слоя стопки: сдвиг от центра, поворот и прозрачность.</summary>
public readonly record struct StackLayer(double X, double Y, double Angle, double Opacity);

/// <summary>
/// Раскладка стопки превью: до <see cref="MaxLayers"/> слоёв лежат друг под другом вокруг общего центра, у каждого свой небольшой
/// разброс сдвига и поворота (не накапливается с глубиной), сверху последний добавленный. Чистая функция, без UI.
/// </summary>
public static class StackPile
{
    public const int MaxLayers = 10;
    /// <summary>Максимальный разброс сдвига по каждой оси, epx.</summary>
    public const double MaxOffset = 5;
    /// <summary>Максимальный разброс поворота, градусы.</summary>
    public const double MaxAngle = 6;
    /// <summary>Прозрачность самого дальнего (10-го) слоя; передний — 1.0.</summary>
    public const double MinOpacity = 0.55;

    /// <summary>Сколько слоёв показывать для <paramref name="count"/> элементов.</summary>
    public static int VisibleLayers(int count) => Math.Clamp(count, 0, MaxLayers);

    /// <summary>
    /// Слой <paramref name="k"/> из <paramref name="n"/> показанных (k = 0 — самый нижний, n − 1 — передний). Передний лежит ровно по центру
    /// и не повёрнут, остальные получают разброс из Id элемента (один и тот же всегда), прозрачность падает линейно с глубиной (1.0 → <see cref="MinOpacity"/> на 10-м).
    /// </summary>
    public static StackLayer Layer(int k, int n, Guid id)
    {
        if (n < 1 || k < 0 || k >= n) throw new ArgumentOutOfRangeException(nameof(k));
        int depth = n - 1 - k;
        double opacity = 1.0 - depth * (1.0 - MinOpacity) / (MaxLayers - 1);
        if (depth == 0) return new StackLayer(0, 0, 0, opacity);
        var b = id.ToByteArray();
        return new StackLayer(Unit(b[0], b[1]) * MaxOffset, Unit(b[2], b[3]) * MaxOffset, Unit(b[4], b[5]) * MaxAngle, opacity);
    }

    // Два байта → число в [-1, 1].
    static double Unit(byte hi, byte lo) => ((hi << 8 | lo) / 65535.0) * 2 - 1;
}
