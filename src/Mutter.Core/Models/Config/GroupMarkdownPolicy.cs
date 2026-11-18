using System.Collections.Frozen;

namespace Mutter.Core.Models.Config;

public enum GroupMarkdownMode { Inherit, Off, All, Selected }

public sealed record GroupMarkdownPolicy(GroupMarkdownMode Mode, FrozenSet<string> GroupIds)
{
    public static readonly GroupMarkdownPolicy Inherited = new(GroupMarkdownMode.Inherit, FrozenSet<string>.Empty);

    public bool Allows(string groupId) => Mode == GroupMarkdownMode.All
        || Mode == GroupMarkdownMode.Selected && GroupIds.Contains(groupId);
}
