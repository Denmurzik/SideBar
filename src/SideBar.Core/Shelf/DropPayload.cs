namespace SideBar.Core.Shelf;

public sealed record VirtualFileInfo(string Name, long DeclaredSize);

/// <summary>Что лежит в IDataObject: пути CF_HDROP, виртуальные файлы, URL и формат картинки (png|jpeg|webp|dib).</summary>
public sealed record DropPayload(
    IReadOnlyList<string> Paths,
    IReadOnlyList<VirtualFileInfo> Virtual,
    string? Url,
    string? ImageFormat,
    bool OnlyTdForward);
