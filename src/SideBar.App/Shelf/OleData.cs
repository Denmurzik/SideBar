using System.Runtime.InteropServices;
using System.Text;

namespace SideBar.Shelf;

/// <summary>Виртуальный файл из FileGroupDescriptorW: индекс в FileContents, имя и заявленный размер (-1, если не задан).</summary>
readonly record struct VirtualDescriptor(int Index, string Name, long Size);

sealed class TooLargeException(string what) : IOException(what + " exceeds the per-file limit");

/// <summary>Чтение нативного IDataObject (vtable-вызовы, AOT-безопасно). Всё — на потоке окна, в котором пришёл Drop.</summary>
static unsafe partial class OleData
{
    [StructLayout(LayoutKind.Sequential)] struct FORMATETC { public ushort cfFormat; public nint ptd; public int dwAspect; public int lindex; public int tymed; }
    [StructLayout(LayoutKind.Sequential)] struct STGMEDIUM { public int tymed; public nint pData; public nint pUnk; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string name);
    [LibraryImport("ole32.dll")] private static partial void ReleaseStgMedium(STGMEDIUM* m);
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint h);
    [LibraryImport("kernel32.dll")] private static partial nuint GlobalSize(nint h);

    [LibraryImport("kernel32.dll")] private static partial int MultiByteToWideChar(uint codePage, uint flags, byte* src, int srcLen, char* dst, int dstLen);

    const int TYMED_HGLOBAL = 1, TYMED_ISTREAM = 4;
    const uint CF_HDROP = 15, CF_DIB = 8, CF_DIBV5 = 17;
    const int Chunk = 1 << 20;
    const int DescriptorSize = 592;
    const int FD_FILESIZE = 0x40, FD_ATTRIBUTES = 0x4;
    const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
    /// <summary>DIB целиком читается в память для конвертации в PNG; больше — не картинка из мессенджера.</summary>
    const long MaxDibBytes = 128L << 20;

    static readonly uint CF_FILEDESCRIPTORW = RegisterClipboardFormat("FileGroupDescriptorW");
    static readonly uint CF_FILECONTENTS = RegisterClipboardFormat("FileContents");
    static readonly (uint Cf, string Format, string Ext)[] ImageFormats =
    [
        (RegisterClipboardFormat("image/png"), "png", ".png"),
        (RegisterClipboardFormat("image/jpeg"), "jpeg", ".jpg"),
        (RegisterClipboardFormat("image/webp"), "webp", ".webp"),
    ];

    // IDataObject vtable: 3 GetData, 5 QueryGetData
    static bool Query(nint dobj, uint cf, int lindex = -1)
    {
        var fe = new FORMATETC { cfFormat = (ushort)cf, dwAspect = 1, lindex = lindex, tymed = -1 };
        var f = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, int>)(*(nint**)dobj)[5];
        return f(dobj, &fe) == 0;
    }

    static bool GetData(nint dobj, uint cf, int lindex, int tymed, out STGMEDIUM m)
    {
        var fe = new FORMATETC { cfFormat = (ushort)cf, dwAspect = 1, lindex = lindex, tymed = tymed };
        STGMEDIUM sm = default;
        var f = (delegate* unmanaged[Stdcall]<nint, FORMATETC*, STGMEDIUM*, int>)(*(nint**)dobj)[3];
        bool ok = f(dobj, &fe, &sm) == 0;
        m = sm;
        return ok;
    }

    /// <summary>Есть ли хоть что-то, что стопка умеет принять (дёшево: только QueryGetData).</summary>
    public static bool HasDroppable(nint dobj) =>
        Query(dobj, CF_HDROP) || Query(dobj, CF_FILEDESCRIPTORW) || ImageFormat(dobj) != null;

    public static bool HasFormat(nint dobj, string name) => Query(dobj, RegisterClipboardFormat(name));

    /// <summary>Формат картинки без файлов: png | jpeg | webp | dib (null — нет).</summary>
    public static string? ImageFormat(nint dobj)
    {
        foreach (var (cf, format, _) in ImageFormats)
            if (Query(dobj, cf)) return format;
        return Query(dobj, CF_DIBV5) || Query(dobj, CF_DIB) ? "dib" : null;
    }

    public static string ExtensionOf(string imageFormat)
    {
        foreach (var (_, format, ext) in ImageFormats)
            if (format == imageFormat) return ext;
        return ".png";
    }

    public static List<string> ReadHDrop(nint dobj)
    {
        var res = new List<string>();
        if (!GetData(dobj, CF_HDROP, -1, TYMED_HGLOBAL, out var m)) return res;
        try
        {
            var b = ReadHGlobal(m.pData, 64L << 20);
            // DROPFILES: DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; затем список с двойным NUL
            if (b.Length < 20) return res;
            int off = BitConverter.ToInt32(b, 0);
            bool wide = BitConverter.ToInt32(b, 16) != 0;
            if (off < 20 || off > b.Length) return res;
            var text = wide ? Encoding.Unicode.GetString(b, off, b.Length - off) : DecodeAnsi(b, off);
            foreach (var part in text.Split('\0'))
                if (part.Length > 0) res.Add(part);
        }
        finally { ReleaseStgMedium(&m); }
        return res;
    }

    /// <summary>ANSI DROPFILES: системная кодовая страница (CP_ACP = 0), а не UTF-8 по умолчанию .NET.</summary>
    static string DecodeAnsi(byte[] b, int off)
    {
        int len = b.Length - off;
        if (len <= 0) return "";
        fixed (byte* src = &b[off])
        {
            int n = MultiByteToWideChar(0, 0, src, len, null, 0);
            if (n <= 0) return "";
            var chars = new char[n];
            fixed (char* dst = chars) n = MultiByteToWideChar(0, 0, src, len, dst, n);
            return new string(chars, 0, Math.Max(n, 0));
        }
    }

    /// <summary>FILEGROUPDESCRIPTORW: UINT cItems; FILEDESCRIPTORW[cItems] (592 байта: флаги 0, атрибуты 36, размер 64/68, имя 72). Каталоги пропускаются.</summary>
    public static List<VirtualDescriptor> ReadDescriptors(nint dobj)
    {
        var res = new List<VirtualDescriptor>();
        if (!GetData(dobj, CF_FILEDESCRIPTORW, -1, TYMED_HGLOBAL, out var gm)) return res;
        byte[] fgd;
        try { fgd = ReadHGlobal(gm.pData, 16L << 20); }
        finally { ReleaseStgMedium(&gm); }
        if (fgd.Length < 4) return res;
        int count = Math.Min(BitConverter.ToInt32(fgd, 0), (fgd.Length - 4) / DescriptorSize);
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * DescriptorSize;
            int flags = BitConverter.ToInt32(fgd, o);
            if ((flags & FD_ATTRIBUTES) != 0 && (BitConverter.ToInt32(fgd, o + 36) & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
            long size = (flags & FD_FILESIZE) != 0 ? ((long)BitConverter.ToUInt32(fgd, o + 64) << 32) | BitConverter.ToUInt32(fgd, o + 68) : -1;
            string name = Encoding.Unicode.GetString(fgd, o + 72, 520).Split('\0')[0];
            res.Add(new VirtualDescriptor(i, name, size));
        }
        return res;
    }

    /// <summary>
    /// Содержимое виртуального файла: IStream читается кусками по 1 МиБ прямо в <paramref name="dest"/>, HGLOBAL пишется кусками.
    /// Превышение <paramref name="limit"/> — <see cref="TooLargeException"/>. <paramref name="declaredSize"/> (>= 0) обрезает HGLOBAL:
    /// GlobalSize бывает больше данных, хвост был бы мусором.
    /// </summary>
    public static void CopyContents(nint dobj, int index, Stream dest, long limit, long declaredSize)
    {
        if (!GetData(dobj, CF_FILECONTENTS, index, TYMED_HGLOBAL | TYMED_ISTREAM, out var m))
            throw new IOException("FileContents is not available");
        Log.Write($"FileContents tymed {m.tymed}");
        try { CopyMedium(m, dest, limit, declaredSize); }
        finally { ReleaseStgMedium(&m); }
    }

    /// <summary>Картинка png|jpeg|webp как есть, потоково (для dib — см. <see cref="ReadDibAsBmp"/>).</summary>
    public static void CopyImage(nint dobj, string format, Stream dest, long limit)
    {
        foreach (var (cf, f, _) in ImageFormats)
        {
            if (f != format) continue;
            if (!GetData(dobj, cf, -1, TYMED_HGLOBAL | TYMED_ISTREAM, out var m)) throw new IOException("image data is not available");
            try { CopyMedium(m, dest, limit, -1); }
            finally { ReleaseStgMedium(&m); }
            return;
        }
        throw new ArgumentException("unknown image format", nameof(format));
    }

    /// <summary>CF_DIBV5/CF_DIB → содержимое BMP-файла (заголовок BITMAPFILEHEADER + DIB).</summary>
    public static byte[] ReadDibAsBmp(nint dobj)
    {
        foreach (var cf in new[] { CF_DIBV5, CF_DIB })
        {
            if (!GetData(dobj, cf, -1, TYMED_HGLOBAL, out var m)) continue;
            byte[] dib;
            try { dib = ReadHGlobal(m.pData, MaxDibBytes); }
            finally { ReleaseStgMedium(&m); }
            if (dib.Length < 40) continue;
            int hdr = BitConverter.ToInt32(dib, 0);
            int bpp = BitConverter.ToInt16(dib, 14);
            int comp = BitConverter.ToInt32(dib, 16);
            int clrUsed = BitConverter.ToInt32(dib, 32);
            int colors = clrUsed != 0 ? clrUsed : (bpp <= 8 ? 1 << bpp : 0);
            int masks = hdr == 40 && comp == 3 ? 12 : 0;
            long offBits = 14L + hdr + masks + colors * 4L;
            if (hdr < 40 || offBits > 14L + dib.Length) continue;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes((int)offBits).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            return bmp;
        }
        throw new IOException("DIB is not available");
    }

    /// <summary>Исходный http(s)-URL, если есть: UniformResourceLocatorW, UniformResourceLocator, text/x-moz-url, CF_UNICODETEXT, CF_TEXT.</summary>
    public static string? ReadUrl(nint dobj)
    {
        var cands = new List<string>();
        void Add(byte[]? b, bool wide)
        {
            if (b == null || b.Length == 0) return;
            var t = wide ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b);
            t = t.Split('\0')[0].Split('\n', '\r')[0].Trim();
            if (t.Length > 0) cands.Add(t);
        }
        Add(TryText(dobj, RegisterClipboardFormat("UniformResourceLocatorW")), true);
        Add(TryText(dobj, RegisterClipboardFormat("UniformResourceLocator")), false);
        Add(TryText(dobj, RegisterClipboardFormat("text/x-moz-url")), true);
        Add(TryText(dobj, 13), true);
        Add(TryText(dobj, 1), false);
        foreach (var text in cands)
            if (Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return u.ToString();
        return null;
    }

    static byte[]? TryText(nint dobj, uint cf)
    {
        if (!GetData(dobj, cf, -1, TYMED_HGLOBAL, out var m)) return null;
        try { return ReadHGlobal(m.pData, 1L << 20); }
        catch (TooLargeException) { return null; }
        finally { ReleaseStgMedium(&m); }
    }

    static byte[] ReadHGlobal(nint h, long limit)
    {
        long size = (long)GlobalSize(h);
        if (size > limit) throw new TooLargeException("HGLOBAL");
        var p = GlobalLock(h);
        if (p == 0) throw new IOException("GlobalLock failed");
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, data.Length);
            return data;
        }
        finally { GlobalUnlock(h); }
    }

    static void CopyMedium(STGMEDIUM m, Stream dest, long limit, long declaredSize)
    {
        if (m.tymed == TYMED_HGLOBAL)
        {
            long size = (long)GlobalSize(m.pData);
            if (declaredSize >= 0 && declaredSize < size) size = declaredSize;
            if (size > limit) throw new TooLargeException("HGLOBAL");
            var p = GlobalLock(m.pData);
            if (p == 0) throw new IOException("GlobalLock failed");
            try
            {
                for (long off = 0; off < size; off += Chunk)
                    dest.Write(new ReadOnlySpan<byte>((byte*)p + off, (int)Math.Min(Chunk, size - off)));
            }
            finally { GlobalUnlock(m.pData); }
            return;
        }
        if (m.tymed != TYMED_ISTREAM) throw new NotSupportedException("tymed " + m.tymed);

        // IStream vtable: 3 Read
        nint s = m.pData;
        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)(*(nint**)s)[3];
        // IStream vtable: 5 Seek — источник мог уже читать поток (позиция не в начале); сбой Seek игнорируем
        var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)s)[5];
        ulong newPos;
        _ = seek(s, 0, 0 /* STREAM_SEEK_SET */, &newPos);
        var buf = new byte[Chunk];
        long total = 0;
        fixed (byte* b = buf)
        {
            while (true)
            {
                uint n = 0;
                int hr = read(s, b, (uint)buf.Length, &n);
                if (hr < 0) throw new IOException($"IStream.Read failed 0x{hr:X}");
                if (n > 0)
                {
                    total += n;
                    if (total > limit) throw new TooLargeException("IStream");
                    dest.Write(buf, 0, (int)n);
                }
                if (hr != 0 || n == 0) break;   // S_FALSE / EOF
            }
        }
    }
}
