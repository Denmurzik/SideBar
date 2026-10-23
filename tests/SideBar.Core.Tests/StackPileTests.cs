using SideBar.Core.Shelf;

namespace SideBar.Core.Tests;

public class StackPileTests
{
    const double Box = 96, MaxHalfSpan = 88;   // превью 96 epx; плитка 192 epx минус отступ 8 с каждой стороны

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(10, 10)]
    [InlineData(25, 10)]
    public void Visible_layers_are_capped_at_ten(int count, int expected) =>
        Assert.Equal(expected, StackPile.VisibleLayers(count));

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    public void Every_shown_layer_has_bounded_jitter_and_fits_the_tile(int count)
    {
        int n = StackPile.VisibleLayers(count);
        for (int rep = 0; rep < 50; rep++)
            for (int k = 0; k < n; k++)
            {
                var l = StackPile.Layer(k, n, Guid.NewGuid());
                Assert.InRange(l.X, -StackPile.MaxOffset, StackPile.MaxOffset);
                Assert.InRange(l.Y, -StackPile.MaxOffset, StackPile.MaxOffset);
                Assert.InRange(l.Angle, -StackPile.MaxAngle, StackPile.MaxAngle);
                Assert.InRange(l.Opacity, StackPile.MinOpacity, 1.0);
                var rad = Math.Abs(l.Angle) * Math.PI / 180;
                var half = Box / 2 * (Math.Cos(rad) + Math.Sin(rad));
                Assert.InRange(Math.Max(Math.Abs(l.X), Math.Abs(l.Y)) + half, 0, MaxHalfSpan);
            }
    }

    [Fact]
    public void Single_layer_is_unrotated_opaque_and_centred() =>
        Assert.Equal(new StackLayer(0, 0, 0, 1.0), StackPile.Layer(0, 1, Guid.NewGuid()));

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public void Front_layer_is_centred_unrotated_and_opaque(int n)
    {
        var front = StackPile.Layer(n - 1, n, Guid.NewGuid());
        Assert.Equal(new StackLayer(0, 0, 0, 1.0), front);
    }

    [Fact]
    public void Back_of_ten_is_dimmest_and_opacity_falls_with_depth()
    {
        Assert.Equal(StackPile.MinOpacity, StackPile.Layer(0, 10, Guid.NewGuid()).Opacity, 9);
        var o = Enumerable.Range(0, 10).Select(k => StackPile.Layer(k, 10, Guid.Empty).Opacity).ToList();
        Assert.True(o.Zip(o.Skip(1), (a, b) => a < b).All(x => x));
    }

    [Fact]
    public void Jitter_depends_only_on_the_id_not_on_the_depth_or_count()
    {
        var id = Guid.NewGuid();
        Assert.Equal(StackPile.Layer(0, 5, id).X, StackPile.Layer(3, 8, id).X);
        Assert.Equal(StackPile.Layer(0, 5, id).Y, StackPile.Layer(3, 8, id).Y);
        Assert.Equal(StackPile.Layer(0, 5, id).Angle, StackPile.Layer(3, 8, id).Angle);
        Assert.Equal(StackPile.Layer(1, 4, id), StackPile.Layer(1, 4, id));
    }

    [Fact]
    public void Different_ids_do_not_all_get_the_same_jitter()
    {
        var angles = Enumerable.Range(0, 50).Select(_ => StackPile.Layer(0, 2, Guid.NewGuid()).Angle).Distinct().Count();
        Assert.True(angles > 25);
    }

    [Fact]
    public void Jitter_does_not_accumulate_with_depth()
    {
        // слои из 10 не уезжают лесенкой: нижний тоже в пределах ±5, а не в десятки epx
        var back = StackPile.Layer(0, 10, Guid.NewGuid());
        Assert.InRange(Math.Abs(back.X), 0, StackPile.MaxOffset);
    }

    [Fact]
    public void Out_of_range_layer_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackPile.Layer(3, 3, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackPile.Layer(0, 0, Guid.Empty));
    }
}
