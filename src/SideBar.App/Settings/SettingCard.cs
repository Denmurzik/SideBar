using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SideBar.Settings;

/// <summary>Карточка настройки в духе Windows 11: заголовок, описание, необязательная строка-предупреждение и элементы управления справа. Стили берёт из ресурсов окна.</summary>
sealed class SettingCard
{
    readonly TextBlock _desc, _warn;
    readonly StackPanel _right;

    public Border Root { get; }

    public SettingCard(FrameworkElement owner, string title, string? description = null, params UIElement[] controls)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        _desc = new TextBlock { Style = (Style)owner.Resources["SettingDescStyle"], Visibility = Visibility.Collapsed };
        _warn = new TextBlock { Style = (Style)owner.Resources["SettingWarnStyle"], Visibility = Visibility.Collapsed };
        left.Children.Add(_desc);
        left.Children.Add(_warn);
        _right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        foreach (var c in controls) _right.Children.Add(c);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_right, 1);
        grid.Children.Add(left);
        grid.Children.Add(_right);
        Root = new Border { Style = (Style)owner.Resources["SettingCardStyle"], Child = grid };
        Description = description;
    }

    public string? Description
    {
        set
        {
            _desc.Text = value ?? "";
            _desc.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string? Warning
    {
        set
        {
            _warn.Text = value ?? "";
            _warn.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
