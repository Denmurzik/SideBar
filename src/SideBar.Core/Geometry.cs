namespace SideBar.Core;

public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Левая/верхняя граница включена, правая/нижняя — нет.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public enum PanelState { Hidden, Peek, Open }
