using System.Runtime.InteropServices;

namespace SideBar.Sharing;

/// <summary>Положить юникод-текст в буфер обмена через Win32 (без WinRT DataPackage). Вызывать из UI-потока.</summary>
static unsafe partial class ClipboardText
{
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;
    const int OpenAttempts = 8, OpenRetryMs = 5;   // буфер может быть занят другим процессом: не дольше ~40 мс на UI-потоке

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] private static partial nint SetClipboardData(uint format, nint mem);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")] private static partial void* GlobalLock(nint mem);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint mem);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalFree(nint mem);

    public static bool Set(string text, nint owner)
    {
        if (!Open(owner)) return false;
        try
        {
            // память готовим до очистки: сбой выделения не должен оставить буфер пользователя пустым
            nint mem = Prepare(text);
            if (mem == 0) return false;
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, mem) != 0) return true;   // владельцем памяти стала система
            GlobalFree(mem);
            return false;
        }
        finally { CloseClipboard(); }
    }

    static bool Open(nint owner)
    {
        for (int i = 0; i < OpenAttempts; i++)
        {
            if (OpenClipboard(owner)) return true;
            if (i < OpenAttempts - 1) Thread.Sleep(OpenRetryMs);
        }
        return false;
    }

    static nint Prepare(string text)
    {
        nuint bytes = (nuint)((text.Length + 1) * 2);
        nint mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (mem == 0) return 0;
        var p = (char*)GlobalLock(mem);
        if (p is null) { GlobalFree(mem); return 0; }
        text.AsSpan().CopyTo(new Span<char>(p, text.Length));
        p[text.Length] = '\0';
        GlobalUnlock(mem);
        return mem;
    }
}
