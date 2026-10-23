using System.Runtime.InteropServices;

namespace SideBar.Edge;

/// <summary>Состояние основной кнопки мыши с учётом «поменять кнопки» (SM_SWAPBUTTON): тогда основная — физическая правая.</summary>
static partial class PrimaryButton
{
    const int SM_SWAPBUTTON = 23, VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vk);

    /// <summary>Настройка меняется на лету, поэтому читается при каждом вызове (оба вызова дешёвые).</summary>
    public static bool IsDown() => (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;
}
