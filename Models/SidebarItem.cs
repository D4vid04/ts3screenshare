using System.Windows;

namespace TS3ScreenShare.Models;

public sealed class SidebarItem
{
    public bool IsChannel { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsSelf { get; init; }
    public Thickness Margin { get; init; }
}
