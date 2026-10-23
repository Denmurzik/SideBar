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
        catch (Exception e) { _accepts = false; Log.Write("DragEnter failed: " + Log.Describe(e)); }
        effect = _accepts ? effect & Copy : 0;
        return 0;
    }

    public int DragOver(int keyState, POINTL pt, ref int effect)
    {
        bool own = false;
        try { own = isOwnDragActive(); }
        catch (Exception e) { Log.Write("DragOver failed: " + Log.Describe(e)); }
        effect = _accepts && !own ? effect & Copy : 0;
        return 0;
    }

    public int DragLeave()
    {
        _accepts = false;
        try { onDragActive(false); }
        catch (Exception e) { Log.Write("DragLeave failed: " + Log.Describe(e)); }
        return 0;
    }

    public int Drop(nint dataObj, int keyState, POINTL pt, ref int effect)
    {
        int result = ShelfService.DropEffectNone;
        try
        {
            if (_accepts && !isOwnDragActive() && (effect & Copy) != 0) result = shelf.AcceptDrop(dataObj);
        }
        catch (Exception e) { Log.Write("Drop failed: " + Log.Describe(e)); }
        _accepts = false;
        try { onDragActive(false); }
        catch (Exception e) { Log.Write("Drop failed: " + Log.Describe(e)); }
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
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint hwnd);

    static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");

    /// <summary>
    /// Регистрирует IDropTarget на верхнем окне и всех его дочерних окнах (WinUI сама регистрирует свои цели на дочерних,
    /// мы их заменяем: карточке нужен только наш приём). Вызывать на UI-потоке после создания содержимого окна.
    /// </summary>
    public static DropRegistration Register(nint topHwnd, ShelfService shelf, Func<bool> isOwnDragActive, Action<bool> onDragActive)
    {
        int hrInit = OleInitialize(0);   // S_OK / S_FALSE (уже инициализировано) — оба годятся
        var wrappers = new StrategyBasedComWrappers();
        var target = new ShelfDropTarget(shelf, isOwnDragActive, onDragActive);
        nint unk = wrappers.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
        Marshal.QueryInterface(unk, in IID_IDropTarget, out nint dt);
        var reg = new DropRegistration(topHwnd, unk, dt, target);
        reg.Refresh();
        Log.Write($"OleInitialize 0x{hrInit:X}");
        return reg;
    }

    internal sealed class DropRegistration(nint top, nint unk, nint dt, ShelfDropTarget keepAlive) : IDisposable
    {
        readonly HashSet<nint> _registered = [];
        bool _disposed;

        /// <summary>
        /// Идемпотентно: окна, уже занятые нами, не трогаем; новые (WinUI пересоздаёт дочерние HWND при смене DPI/дисплея)
        /// получают нашу цель вместо чужой. Мёртвые HWND забываются.
        /// </summary>
        public void Refresh()
        {
            if (_disposed) return;
            _registered.RemoveWhere(h => !IsWindow(h));
            var windows = new List<nint> { top };
            var handle = GCHandle.Alloc(windows);
            try { EnumChildWindows(top, (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&EnumCb, GCHandle.ToIntPtr(handle)); }
            finally { handle.Free(); }

            int added = 0;
            foreach (var h in windows)
            {
                if (_registered.Contains(h)) continue;
                RevokeDragDrop(h);
                int hr = RegisterDragDrop(h, dt);
                if (hr == 0) { _registered.Add(h); added++; }
                else Log.Write($"RegisterDragDrop failed: 0x{hr:X}");
            }
            Log.Write($"drop target: +{added} window(s), {_registered.Count} of {windows.Count} registered");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var h in _registered) RevokeDragDrop(h);
            _registered.Clear();
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
