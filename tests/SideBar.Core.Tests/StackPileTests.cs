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
    public void Layers_stay_inside_the_tile_and_are_centred(int n)
    {
        var layers = Enumerable.Range(0, n).Select(k => StackPile.Layer(k, n, Guid.NewGuid())).ToList();
        foreach (var l in layers)
        {
            var rad = Math.Abs(l.Angle) * Math.PI / 180;
            var half = Box / 2 * (Math.Cos(rad) + Math.Sin(rad));
            Assert.InRange(Math.Abs(l.X) + half, 0, MaxHalfSpan);
            Assert.InRange(Math.Abs(l.Y) + half, 0, MaxHalfSpan);
            Assert.InRange(l.Opacity, StackPile.MinOpacity, 1.0);
        }
        Assert.Equal(0, layers[0].X + layers[^1].X, 9);   // симметрично относительно центра
        Assert.Equal(0, layers[0].Y + layers[^1].Y, 9);
    }

    [Fact]
    public void Single_layer_is_unrotated_opaque_and_centred() =>
        Assert.Equal(new StackLayer(0, 0, 0, 1.0), StackPile.Layer(0, 1, Guid.NewGuid()));

    [Fact]
    public void Front_layer_is_unrotated_and_opaque_and_back_layer_is_dimmest()
    {
        var front = StackPile.Layer(9, 10, Guid.NewGuid());
        var back = StackPile.Layer(0, 10, Guid.NewGuid());
        Assert.Equal(0, front.Angle);
        Assert.Equal(1.0, front.Opacity);
        Assert.Equal(StackPile.MinOpacity, back.Opacity, 9);
    }

    [Fact]
    public void Opacity_falls_with_depth()
    {
        var o = Enumerable.Range(0, 10).Select(k => StackPile.Layer(k, 10, Guid.Empty).Opacity).ToList();
        Assert.True(o.Zip(o.Skip(1), (a, b) => a < b).All(x => x));
    }

    [Fact]
    public void Rotation_depends_only_on_the_id_and_stays_small()
    {
        var id = Guid.NewGuid();
        // тот же элемент на глубине 1 в стопке из 3 и из 10 слоёв: угол одинаков
        Assert.Equal(StackPile.Layer(0, 2, id).Angle, StackPile.Layer(8, 10, id).Angle);
        for (int i = 0; i < 200; i++) Assert.InRange(StackPile.Layer(0, 10, Guid.NewGuid()).Angle, -3, 3);
    }

    [Fact]
    public void Offsets_step_four_epx_per_layer()
    {
        var a = StackPile.Layer(3, 10, Guid.Empty);
        var b = StackPile.Layer(4, 10, Guid.Empty);
        Assert.Equal(4, b.X - a.X, 9);
        Assert.Equal(4, b.Y - a.Y, 9);
    }

    [Fact]
    public void Out_of_range_layer_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StackPile.Layer(3, 3, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackPile.Layer(0, 0, Guid.Empty));
    }
}
