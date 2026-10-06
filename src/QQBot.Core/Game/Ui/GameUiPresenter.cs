using Microsoft.Extensions.Logging;
using QQBot.Core.Game.Models;
using QQBot.Core.Game.Services;

namespace QQBot.Core.Game.Ui;

/// <summary>
/// 游戏回复呈现器（唯一出口）。把"业务文案"与"怎么发"彻底分开：
/// - 纯文本正文始终产出并经 <see cref="TextLayout"/> 折行，作为落库内容与降级兜底；
/// - 群开了富交互且有 markdown 能力时，额外产出 markdown 正文与按钮/文字链。
/// 任何一层能力缺失都只是少发一点，不会让消息发不出去。
/// </summary>
public sealed class GameUiPresenter
{
    private readonly RichUiCapability _cap;
    private readonly ILogger<GameUiPresenter> _logger;

    public GameUiPresenter(RichUiCapability cap, ILogger<GameUiPresenter> logger)
    {
        _cap = cap;
        _logger = logger;
    }

    public RichUiCapability Capability => _cap;

    /// <param name="plain">业务文案（纯文本，未经折行）。</param>
    /// <param name="panel">可选：结构化面板。给出时 markdown 用其原生渲染，否则由纯文本保守转换。</param>
    public GameUiReply Compose(
        string? groupId,
        GameAction action,
        string plain,
        MenuContext ctx,
        string? nickname,
        GamePanel? panel = null)
    {
        // 被动回复时 QQ 会自动渲染 @发送者，首行需先扣除其占宽
        var plainOut = TextLayout.Flow(plain, GameTextFormatter.MentionReserve(nickname));

        if (!_cap.UseMarkdown(groupId))
            return new GameUiReply(plainOut);

        _cap.CountRichSent();

        var menu = GameMenuCatalog.For(action, ctx);
        var keyboard = GameKeyboard.From(menu, _cap.EffectiveKeyboardMode(groupId), _cap.Options, _logger);

        var markdown = panel is not null
            ? panel.Render(PanelMode.Markdown)
            : MarkdownText.FromPlainText(plain);   // 用未折行的原文：Flow 造出的断行与悬挂缩进是为 14 当量气泡准备的，
                                                    // 再转 markdown 会把装饰线切成半截（如出现 "### ═" 这样的垃圾行）

        if (keyboard is null && menu.Count > 0 && _cap.UseCmdInputLinks(groupId))
        {
            var links = GameKeyboard.RenderInputLinks(menu);
            if (links.Length > 0)
                markdown = markdown.Length > 0 ? markdown + "\n" + links : links;
        }

        markdown = ClampMarkdown(markdown, _cap.Options.MaxMarkdownChars);

        if (keyboard is not null) _cap.CountKeyboardSent();
        else if (menu.Count > 0 && _cap.UseCmdInputLinks(groupId)) _cap.CountLinkOnlySent();

        return new GameUiReply(plainOut, new PanelPayload(plainOut, markdown, keyboard, menu));
    }

    /// <summary>供非面板路径（AI 叙事等）复用：只加 markdown 外壳，不挂菜单。</summary>
    public GameUiReply ComposePlainOnly(string? groupId, string plain, string? nickname) =>
        Compose(groupId, GameAction.Help, plain, MenuContext.Empty, nickname);

    private static string ClampMarkdown(string md, int maxChars)
    {
        if (maxChars <= 0 || md.Length <= maxChars) return md;
        var cut = md.LastIndexOf('\n', Math.Min(maxChars, md.Length - 1));
        if (cut < maxChars / 2) cut = maxChars;
        return md[..cut] + "\n…（内容过长已截断）";
    }
}
