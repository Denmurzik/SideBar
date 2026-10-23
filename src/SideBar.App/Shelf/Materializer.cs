using System.Runtime.InteropServices.WindowsRuntime;
using SideBar.Core.Shelf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SideBar.Shelf;

/// <summary>Запись файлов в Shelf\&lt;guid&gt;\: поток → content.part → переименование. При любой ошибке каталог &lt;guid&gt; удаляется целиком.</summary>
static class Materializer
{
    public const long MaxFileBytes = 2L << 30;
    const int Chunk = 1 << 20;
    const int HeadBytes = 16;
    const string PartName = "content.part";

    /// <summary>
    /// <paramref name="write"/> наполняет поток; имя файла выбирается по его первым байтам. Возвращает полный путь итогового файла.
    /// </summary>
    public static string Materialize(string shelfRoot, Action<Stream> write, Func<byte[], string> chooseName)
    {
        var dir = Path.Combine(shelfRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var part = Path.Combine(dir, PartName);
            using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            var name = FileNaming.Sanitize(chooseName(ReadHead(part)));
            if (string.Equals(name, PartName, StringComparison.OrdinalIgnoreCase)) name = "file";
            var final = Path.Combine(dir, name);
            if (!IsInside(shelfRoot, final)) throw new IOException("final path escapes the shelf root");
            File.Move(part, final);
            return final;
        }
        catch
        {
            TryDeleteDir(dir);
            throw;
        }
    }

    /// <summary>Копия файла источника (временный путь CF_HDROP): открывается с FileShare.Delete, чтобы владелец мог удалить его параллельно.</summary>
    public static string CopyFile(string shelfRoot, string sourcePath)
    {
        var name = FileNaming.Sanitize(Path.GetFileName(sourcePath));
        return Materialize(shelfRoot, dest =>
        {
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (src.Length > MaxFileBytes) throw new TooLargeException("file");
            var buf = new byte[Chunk];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > MaxFileBytes) throw new TooLargeException("file");
                dest.Write(buf, 0, n);
            }
        }, _ => name);
    }

    /// <summary>BMP (из CF_DIB) → PNG через WinRT BitmapEncoder. Блокирует вызывающий поток; сама работа идёт в пуле.</summary>
    public static byte[] BmpToPng(byte[] bmp) => Task.Run(() => BmpToPngAsync(bmp)).GetAwaiter().GetResult();

    static async Task<byte[]> BmpToPngAsync(byte[] bmp)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bmp.AsBuffer());
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixels = await decoder.GetPixelDataAsync();
        using var output = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        enc.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
        await enc.FlushAsync();
        output.Seek(0);
        var buf = new byte[output.Size];
        await output.ReadAsync(buf.AsBuffer(), (uint)buf.Length, InputStreamOptions.None);
        return buf;
    }

    static bool IsInside(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    static byte[] ReadHead(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[HeadBytes];
        int n = fs.Read(head, 0, head.Length);
        return head[..n];
    }

    public static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) { Log.Write("cleanup failed: " + Log.Describe(e)); }
    }
}
