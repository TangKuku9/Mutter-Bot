using Mutter.Core.Models.Config;

namespace Mutter.Infrastructure.Entities;

public sealed class GroupMarkdownSettingsEntity
{
    public int Id { get; set; } = 1;
    public GroupMarkdownMode Mode { get; set; }
    public string GroupIdsJson { get; set; } = "[]";
}
