using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SideBar.Core;
using SideBar.Core.Edge;
using SideBar.Core.Layout;
using SideBar.Native;
using WinRT.Interop;

namespace SideBar;

public partial class App : Application
{
    CardWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new CardWindow();
        var rc = DisplayArea.Primary.OuterBounds;
        var monitor = new PxRect(rc.X, rc.Y, rc.X + rc.Width, rc.Y + rc.Height);
        double scale = WindowFrame.GetDpi(WindowNative.GetWindowHandle(_window)) / 96.0;
        _window.Initialize(new CardLayout(monitor, scale), EdgeMode.Slide);
    }
}
