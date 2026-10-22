using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace S10Spring;

// Window-wide backdrop that is a fully transparent brush: lets per-pixel alpha of the XAML content reach DWM.
// The Windows.UI.Composition compositor needs a Windows.System.DispatcherQueue, which the UI thread (Microsoft.UI) lacks,
// so the compositor is created on a helper thread that owns one.
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out nint controller);

    public static Action<string>? Log;
    Windows.UI.Composition.CompositionBrush? _brush;
    Thread? _thread;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        try
        {
            if (_brush == null)
            {
                var ready = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    int hr = CreateDispatcherQueueController(new DispatcherQueueOptions { Size = 12, ThreadType = 2, ApartmentType = 0 }, out _);
                    var comp = new Windows.UI.Composition.Compositor();
                    _brush = comp.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    Log?.Invoke($"backdrop compositor created on helper thread (hr=0x{hr:X})");
                    ready.Set();
                    Thread.Sleep(Timeout.Infinite);
                }) { IsBackground = true };
                _thread.Start();
                ready.Wait();
            }
            connectedTarget.SystemBackdrop = _brush;
            Log?.Invoke("backdrop brush set");
        }
        catch (Exception ex) { Log?.Invoke("backdrop failed: " + ex); }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
    }
}
