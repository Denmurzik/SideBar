using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;

namespace S8Edge;

public sealed partial class MainWindow : Window
{
    readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "s8.log");
    readonly object _logLock = new();
    readonly EdgeWatcher _edge = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueue _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    readonly ObservableCollection<string> _names = new();
    readonly List<(string Path, StorageFile File)> _items = new();
    readonly string _shelfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SideBar", "Shelf");
    nint _hwnd;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowRgn(nint hwnd, nint rgn, bool redraw);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    bool _dragActive;
    List<DropProxy> _proxies = new();
    List<VirtualFile>? _nativeVirtual;

    public MainWindow()
    {
        InitializeComponent();
        Shelf.ItemsSource = _names;
        Directory.CreateDirectory(_shelfDir);
    }

    [System.Runtime.InteropServices.DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint h, nint proc, nuint id, nuint data);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint h, uint msg, nint w, nint l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(nint h, nint a, int x, int y, int cx, int cy, uint f);

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static nint NcProc(nint h, uint msg, nint w, nint l, nuint id, nuint data) => msg == 0x83 && w != 0 ? 0 : DefSubclassProc(h, msg, w, l);

    // WM_NCCALCSIZE -> 0: the 3 px non-client border of the borderless presenter would inset the content
    unsafe void StripFrame()
    {
        SetWindowSubclass(_hwnd, (nint)(delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint>)&NcProc, 1, 0);
        SetWindowPos(_hwnd, 0, 0, 0, 0, 0, 0x37);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
    }

    public async void Start()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false);
        p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;

        _edge.Log += Log;
        _edge.Start();
        double dpi = _edge.Dpi / 96.0;
        int w = (int)Math.Round(216 * dpi), h = (int)Math.Round(469 * dpi), gap = (int)Math.Round(8 * dpi);
        int top = (_edge.MonitorTop + _edge.MonitorBottom) / 2 - h / 2;
        _edge.PanelPx = w + gap; _edge.CardTop = top; _edge.CardBottom = top + h;
        Card.BorderThickness = new Thickness(1.0 / dpi);
        AppWindow.MoveAndResize(new(_edge.MonitorRight - gap - w, top, w, h));
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, (int)(16 * dpi), (int)(16 * dpi)), false);
        // The window must be shown once so XAML registers its drop targets; then the proxies are installed.
        StripFrame();
        AppWindow.Show(false);
        await Task.Delay(800);
        _proxies = OleData.InstallProxies(_hwnd, Log, d => _dq.TryEnqueue(() => HandleNativeDrop(d)), active => _dragActive = active);
        AppWindow.Hide();

        _edge.StateChanged += (s, why) => _dq.TryEnqueue(() =>
        {
            Log($"EDGE -> {s} ({why})");
            if (s == EdgeState.Open) AppWindow.Show(false);
            else if (s == EdgeState.Hidden && !_dragActive) AppWindow.Hide();   // Peek: no window change in this spike (S10 animates it)
        });
        var stats = _dq.CreateTimer();
        stats.Interval = TimeSpan.FromSeconds(1);
        long lastMoves = 0;
        stats.Tick += (_, _) =>
        {
            long m = _edge.Moves;
            if (m != lastMoves) Log($"hook moves total={m} (+{m - lastMoves}/s) duringLButton={_edge.MovesDuringButton}");
            lastMoves = m;
        };
        stats.Start();
        Log("ready; edge watcher running");
    }

    static readonly string[] EphemeralRoots =
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"),
    };

    static bool IsEphemeral(string path) =>
        EphemeralRoots.Any(r => path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n.Length == 0 ? "file" : n;
    }

    static string? SniffExt(byte[] d) =>
        d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8 ? ".jpg" :
        d.Length >= 4 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 ? ".png" :
        d.Length >= 4 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' ? ".gif" :
        d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' ? ".webp" : null;

    // Rule: virtual files win (CF_HDROP is ignored: Chrome's one points to a temp file that disappears) and are written to
    // Shelf\<guid>\name; a CF_HDROP-only item from a temp/cache folder is copied; ordinary paths are kept by reference.
    async void HandleNativeDrop(NativeDrop d)
    {
        var paths = d.Paths; var virt = d.Virtual; var url = d.Url;
        try
        {
            if (virt.Count > 0)
            {
                Log($"drop policy: virtual files win, ignoring {paths.Count} CF_HDROP path(s)");
                foreach (var vf in virt)
                {
                    var name = SafeName(vf.Name);
                    if (url != null && virt.Count == 1 && Uri.TryCreate(url, UriKind.Absolute, out var u))
                    {
                        var seg = Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
                        if (seg.Length > 0) name = SafeName(seg);
                    }
                    var sniff = SniffExt(vf.Data);
                    if (sniff != null && Path.GetExtension(name).Length == 0) name += sniff;
                    AddFile(await Materialize(name, vf.Data), "virtual");
                }
            }
            else if (d.Image != null)
            {
                var ext = d.ImageExt!;
                var data = d.Image;
                if (ext == ".bmp") { data = await BmpToPng(data); ext = ".png"; }
                AddFile(await Materialize($"Telegram {DateTime.Now:yyyy-MM-dd HH-mm-ss}{ext}", data), "image data");
            }
            else
            {
                foreach (var path in paths)
                {
                    if (!File.Exists(path)) { Log($"skip (not a file): {path}"); continue; }
                    if (IsEphemeral(path))
                        AddFile(await Materialize(Path.GetFileName(path), await File.ReadAllBytesAsync(path)), "copied from temp");
                    else
                        AddFile(await StorageFile.GetFileFromPathAsync(path), "reference");
                }
            }
            DropInfo.Text = $"native drop: {paths.Count} paths, {virt.Count} virtual";
        }
        catch (Exception ex) { Log("native drop failed: " + ex); }
    }

    static async Task<byte[]> BmpToPng(byte[] bmp)
    {
        using var input = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await input.WriteAsync(bmp.AsBuffer());
        input.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
        var pixels = await decoder.GetPixelDataAsync();
        using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
        enc.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await enc.FlushAsync();
        output.Seek(0);
        var buf = new byte[output.Size];
        await output.ReadAsync(buf.AsBuffer(), (uint)buf.Length, Windows.Storage.Streams.InputStreamOptions.None);
        return buf;
    }

    // Display names on the shelf must be unique: "name (2).ext"
    string UniqueName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        var candidate = name; int n = 2;
        while (_names.Contains(candidate)) candidate = $"{stem} ({n++}){ext}";
        return candidate;
    }

    async Task<StorageFile> Materialize(string name, byte[] data)
    {
        name = UniqueName(name);
        var dir = Path.Combine(_shelfDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, data);
        return await StorageFile.GetFileFromPathAsync(path);
    }

    // ---- drop target ----
    void OnDragOver(object sender, DragEventArgs e)
    {
        _dragActive = true;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to shelf";
    }

    void OnDragLeave(object sender, DragEventArgs e) => _dragActive = false;

    async void OnDrop(object sender, DragEventArgs e)
    {
        _dragActive = false;
        var def = e.GetDeferral();
        try
        {
            var view = e.DataView;
            Log("WinUI Drop formats: " + string.Join(", ", view.AvailableFormats));
            bool hasItems = view.Contains(StandardDataFormats.StorageItems);
            Log($"  StorageItems={hasItems}  nativeVirtualFiles={(_nativeVirtual?.Count ?? 0)}");
            if (hasItems)
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var it in items) if (it is StorageFile f) AddFile(f);
                DropInfo.Text = $"StorageItems: {items.Count}";
            }
            if (_nativeVirtual is { Count: > 0 } virt)
            {
                foreach (var vf in virt)
                {
                    var path = Path.Combine(_shelfDir, vf.Name);
                    await File.WriteAllBytesAsync(path, vf.Data);
                    AddFile(await StorageFile.GetFileFromPathAsync(path));
                }
                DropInfo.Text = $"Virtual files via native IDataObject: {virt.Count}";
                _nativeVirtual = null;
            }
            foreach (var fmt in new[] { "FileGroupDescriptorW", "FileContents" })
            {
                if (!view.AvailableFormats.Contains(fmt)) continue;
                try
                {
                    var o = await view.GetDataAsync(fmt);
                    Log($"  WinUI GetDataAsync({fmt}) -> {o?.GetType().FullName}");
                }
                catch (Exception ex) { Log($"  WinUI GetDataAsync({fmt}) failed: {ex.GetType().Name} {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log("Drop failed: " + ex); }
        finally { def.Complete(); }
    }

    void AddFile(StorageFile f, string how = "")
    {
        if (_items.Any(i => i.Path == f.Path)) return;
        _items.Add((f.Path, f));
        _names.Add(f.Name);
        Log($"shelf += {f.Name} ({_items.Count} items) [{how}] where={(f.Path.StartsWith(_shelfDir, StringComparison.OrdinalIgnoreCase) ? "Shelf" : "reference")}");
    }

    // ---- drag out ----
    void OnDragAllStarting(UIElement sender, DragStartingEventArgs e)
    {
        e.Data.SetStorageItems(_items.Select(i => (IStorageItem)i.File).ToList());
        e.AllowedOperations = DataPackageOperation.Copy;
        e.Data.RequestedOperation = DataPackageOperation.Copy;   // never Move: Explorer would move the user's originals
        _dragActive = true;
        Log($"drag-out ALL starting, {_items.Count} items");
    }

    void OnDragAllCompleted(UIElement sender, DropCompletedEventArgs e)
    {
        _dragActive = false;
        Log($"drag-out ALL completed, DropResult={e.DropResult}");
        if (e.DropResult != DataPackageOperation.None) { _items.Clear(); _names.Clear(); }
        Log($"shelf count={_items.Count}");
    }

    // Single items are dragged through UIElement.DragStarting (DragItemsStarting has no AllowedOperations).
    void OnItemDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        var name = (sender as FrameworkElement)?.DataContext as string;
        var files = _items.Where(i => i.File.Name == name).Select(i => (IStorageItem)i.File).ToList();
        e.Data.SetStorageItems(files);
        e.AllowedOperations = DataPackageOperation.Copy;
        e.Data.RequestedOperation = DataPackageOperation.Copy;   // never Move: Explorer would move the user's originals
        _dragActive = true;
        Log($"drag-out item starting: {name}");
    }

    void OnItemDropCompleted(UIElement sender, DropCompletedEventArgs e)
    {
        _dragActive = false;
        var name = (sender as FrameworkElement)?.DataContext as string;
        Log($"drag-out item completed, DropResult={e.DropResult}");
        if (e.DropResult != DataPackageOperation.None && name != null)
        {
            _names.Remove(name);
            _items.RemoveAll(i => i.File.Name == name);
        }
        Log($"shelf count={_items.Count}");
    }

    void Log(string s)
    {
        lock (_logLock) File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
    }
}
