using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SideBar.Shelf;

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL { public int X, Y; }

[GeneratedComInterface, Guid("00000122-0000-0000-C000-000000000046")]
internal partial interface IDropTarget
{
    [PreserveSig] int DragEnter(nint dataObj, int keyState, POINTL pt, ref int effect);
    [PreserveSig] int DragOver(int keyState, POINTL pt, ref int effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(nint dataObj, int keyState, POINTL pt, ref int effect);
}

/// <summary>Один IDropTarget на все окна карточки (верхнее и дочерние окна WinUI). Колбэки приходят на UI-поток.</summary>
[GeneratedComClass]
internal sealed partial class ShelfDropTarget(ShelfService shelf, Func<bool> isOwnDragActive, Action<bool> onDragActive) : IDropTarget
{
    const int Copy = ShelfService.DropEffectCopy;
    bool _accepts;

    public int DragEnter(nint dataObj, int keyState, POINTL pt, ref int effect)
    {
        try
        {
            onDragActive(true);
            _accepts = !isOwnDragActive() && OleData.HasDroppable(dataObj);
        }
        catch (Exception e) { _accepts = false; Log.Write("DragEnter failed: " + e.GetType().Name); }
        effect = _accepts ? effect & Copy : 0;
        return 0;
    }

    public int DragOver(int keyState, POINTL pt, ref int effect)
    {
        bool own = false;
        try { own = isOwnDragActive(); }
        catch (Exception e) { Log.Write("DragOver failed: " + e.GetType().Name); }
        effect = _accepts && !own ? effect & Copy : 0;
        return 0;
    }

    public int DragLeave()
    {
        _accepts = false;
        try { onDragActive(false); }
        catch (Exception e) { Log.Write("DragLeave failed: " + e.GetType().Name); }
        return 0;
    }

    public int Drop(nint dataObj, int keyState, POINTL pt, ref int effect)
    {
        int result = ShelfService.DropEffectNone;
        try
        {
            if (_accepts && !isOwnDragActive() && (effect & Copy) != 0) result = shelf.AcceptDrop(dataObj);
        }
        catch (Exception e) { Log.Write("Drop failed: " + e.GetType().Name); }
        _accepts = false;
        try { onDragActive(false); }
        catch (Exception e) { Log.Write("Drop failed: " + e.GetType().Name); }
        effect = result;
        return 0;
    }
}

static unsafe partial class OleDropTarget
{
    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, nint target);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EnumChildWindows(nint hwnd, nint proc, nint lparam);

    static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");

    /// <summary>
    /// Регистрирует IDropTarget на верхнем окне и всех его дочерних окнах (WinUI сама регистрирует свои цели на дочерних,
    /// мы их заменяем: карточке нужен только наш приём). Вызывать на UI-потоке после создания содержимого окна.
    /// </summary>
    public static IDisposable Register(nint topHwnd, ShelfService shelf, Func<bool> isOwnDragActive, Action<bool> onDragActive)
    {
        var windows = new List<nint> { topHwnd };
        var handle = GCHandle.Alloc(windows);
        try { EnumChildWindows(topHwnd, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumCb, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }

        int hrInit = OleInitialize(0);   // S_OK / S_FALSE (уже инициализировано) — оба годятся
        var wrappers = new StrategyBasedComWrappers();
        var target = new ShelfDropTarget(shelf, isOwnDragActive, onDragActive);
        nint unk = wrappers.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
        Marshal.QueryInterface(unk, in IID_IDropTarget, out nint dt);

        var registered = new List<nint>();
        foreach (var h in windows)
        {
            RevokeDragDrop(h);
            int hr = RegisterDragDrop(h, dt);
            if (hr == 0) registered.Add(h);
            else Log.Write($"RegisterDragDrop failed: 0x{hr:X}");
        }
        Log.Write($"drop target registered on {registered.Count} of {windows.Count} window(s), OleInitialize 0x{hrInit:X}");
        return new Registration(registered, unk, dt, target);
    }

    sealed class Registration(List<nint> windows, nint unk, nint dt, ShelfDropTarget keepAlive) : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var h in windows) RevokeDragDrop(h);
            Marshal.Release(dt);
            Marshal.Release(unk);
            GC.KeepAlive(keepAlive);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    static int EnumCb(nint hwnd, nint lp)
    {
        ((List<nint>)GCHandle.FromIntPtr(lp).Target!).Add(hwnd);
        return 1;
    }
}
