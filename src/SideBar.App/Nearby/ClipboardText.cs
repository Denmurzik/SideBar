using System.Runtime.InteropServices;

namespace SideBar.Sharing;

/// <summary>Положить юникод-текст в буфер обмена через Win32 (без WinRT DataPackage). Вызывать из UI-потока.</summary>
static unsafe class ClipboardText
{
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;

    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint mem);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern void* GlobalLock(nint mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint mem);

    public static bool Set(string text, nint owner)
    {
        nuint bytes = (nuint)((text.Length + 1) * 2);
        nint mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (mem == 0) return false;
        var p = (char*)GlobalLock(mem);
        if (p is null) { GlobalFree(mem); return false; }
        text.AsSpan().CopyTo(new Span<char>(p, text.Length));
        p[text.Length] = '\0';
        GlobalUnlock(mem);

        // буфер может быть занят другим процессом: несколько коротких попыток
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(owner))
            {
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CF_UNICODETEXT, mem) != 0) return true;   // владельцем стала система
                }
                finally { CloseClipboard(); }
                break;
            }
            Thread.Sleep(20);
        }
        GlobalFree(mem);
        return false;
    }
}
