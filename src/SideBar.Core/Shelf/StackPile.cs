namespace SideBar.Core.Shelf;

/// <summary>Положение одного слоя стопки: сдвиг от центра, поворот и прозрачность.</summary>
public readonly record struct StackLayer(double X, double Y, double Angle, double Opacity);

/// <summary>Раскладка стопки превью: до <see cref="MaxLayers"/> слоёв, сверху последний добавленный. Чистая функция, без UI.</summary>
public static class StackPile
{
    public const int MaxLayers = 10;
    /// <summary>Сдвиг каждого следующего слоя вниз-вправо, epx.</summary>
    public const double Step = 4;
    /// <summary>Прозрачность самого дальнего (10-го) слоя; передний — 1.0.</summary>
    public const double MinOpacity = 0.55;

    // Небольшой знакопеременный разброс; угол слоя выбирается по Id элемента, поэтому при перестройке ничего не дёргается.
    static readonly double[] Angles = [-3, 2, -1, 3, -2, 1, -3, 2, -1, 3];

    /// <summary>Сколько слоёв показывать для <paramref name="count"/> элементов.</summary>
    public static int VisibleLayers(int count) => Math.Clamp(count, 0, MaxLayers);

    /// <summary>
    /// Слой <paramref name="k"/> из <paramref name="n"/> показанных (k = 0 — самый нижний, n − 1 — передний). Передний не повёрнут,
    /// прозрачность падает линейно с глубиной (1.0 → <see cref="MinOpacity"/> на 10-м), вся стопка центрована.
    /// </summary>
    public static StackLayer Layer(int k, int n, Guid id)
    {
        if (n < 1 || k < 0 || k >= n) throw new ArgumentOutOfRangeException(nameof(k));
        int depth = n - 1 - k;
        double center = (n - 1) * Step / 2;
        double angle = depth == 0 ? 0 : Angles[(id.ToByteArray().Sum(b => b)) % Angles.Length];
        double opacity = 1.0 - depth * (1.0 - MinOpacity) / (MaxLayers - 1);
        return new StackLayer(k * Step - center, k * Step - center, angle, opacity);
    }
}
