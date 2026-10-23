namespace SideBar.Core.Nearby;

public enum ReceiveBlock { None, PublicNetwork, NoLan }

/// <summary>Why a PC that is visible to everyone cannot listen, for the one-time note to the user.</summary>
public static class ReceiveBlockReason
{
    public static ReceiveBlock Of(Visibility visibility, bool hasRoute, NetworkCategory? category)
    {
        if (visibility != Visibility.Everyone) return ReceiveBlock.None;
        if (!hasRoute) return ReceiveBlock.NoLan;
        return category is NetworkCategory.Private or NetworkCategory.Domain ? ReceiveBlock.None : ReceiveBlock.PublicNetwork;
    }
}
