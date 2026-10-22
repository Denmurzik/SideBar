using Microsoft.UI.Xaml;

namespace S10Spring;

public partial class App : Application
{
    public static string[] Args = Environment.GetCommandLineArgs();
    private MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Start();
    }
}
