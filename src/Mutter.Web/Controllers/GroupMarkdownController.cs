using System.Collections.Frozen;
using Microsoft.AspNetCore.Mvc;
using Mutter.Core.Models.Config;
using Mutter.Core.Services;
using Mutter.Infrastructure.Repositories;

namespace Mutter.Web.Controllers;

[ApiController]
[Route("api/groups/markdown-settings")]
public sealed class GroupMarkdownController(GroupMarkdownSettingsStore store, RichUiCapability richUi) : ControllerBase
{
    public sealed record SettingsRequest(string Mode, string[]? GroupIds);

    [HttpGet]
    public IActionResult Get() => Ok(Settings());

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SettingsRequest request, CancellationToken ct)
    {
        var mode = request.Mode switch
        {
            "inherit" => GroupMarkdownMode.Inherit,
            "off" => GroupMarkdownMode.Off,
            "all" => GroupMarkdownMode.All,
            "selected" => GroupMarkdownMode.Selected,
            _ => (GroupMarkdownMode?)null
        };
        if (mode is null) return BadRequest(new { error = "请选择 QQ Markdown 的发送范围" });

        var ids = FrozenSet<string>.Empty;
        if (mode == GroupMarkdownMode.Selected)
        {
            if (request.GroupIds is not { Length: > 0 } groupIds || groupIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128))
                return BadRequest(new { error = "请至少选择一个有效的群" });
            ids = groupIds.Select(id => id.Trim()).ToFrozenSet(StringComparer.Ordinal);
        }
        await store.SaveAsync(new GroupMarkdownPolicy(mode.Value, ids), ct);
        return Ok(Settings());
    }

    private object Settings()
    {
        var policy = richUi.GroupMarkdown;
        var options = richUi.Options;
        var state = richUi.Snapshot();
        bool inherited = policy.Mode == GroupMarkdownMode.Inherit;
        var effectiveMode = !inherited ? policy.Mode
            : !options.Enabled || !options.Markdown ? GroupMarkdownMode.Off
            : options.GroupAllowList.Count == 0 ? GroupMarkdownMode.All : GroupMarkdownMode.Selected;
        return new
        {
            Mode = policy.Mode.ToString().ToLowerInvariant(),
            EffectiveMode = effectiveMode.ToString().ToLowerInvariant(),
            GroupIds = (inherited ? options.GroupAllowList.AsEnumerable() : policy.GroupIds).Order(StringComparer.Ordinal),
            PlatformState = state.MarkdownState,
            PlatformDeniedCode = state.MarkdownDeniedCode
        };
    }
}
