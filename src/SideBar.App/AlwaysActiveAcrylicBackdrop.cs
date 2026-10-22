using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace SideBar;

/// <summary>
/// Акрил, который остаётся «активным» (полупрозрачным) и у неактивного окна: карточка показывается без активации,
/// а штатный DesktopAcrylicBackdrop в неактивном окне подменяется сплошным цветом.
/// </summary>
sealed partial class AlwaysActiveAcrylicBackdrop : SystemBackdrop
{
    DesktopAcrylicController? _controller;
    SystemBackdropConfiguration? _config;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        if (xamlRoot.Content is FrameworkElement root) SetTheme(root.ActualTheme);
        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _config = null;
    }

    public void SetTheme(ElementTheme theme)
    {
        if (_config is null) return;
        _config.Theme = theme switch
        {
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            ElementTheme.Light => SystemBackdropTheme.Light,
            _ => SystemBackdropTheme.Default,
        };
    }
}
