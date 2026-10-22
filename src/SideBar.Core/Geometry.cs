namespace SideBar.Core;

public readonly record struct PxRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public enum PanelState { Hidden, Peek, Open }
