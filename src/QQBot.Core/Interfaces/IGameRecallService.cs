namespace QQBot.Core.Interfaces;

/// <summary>
/// 修仙消息延迟撤回调度器。发送成功后登记平台消息 id，到点后调用官方撤回接口
/// （DELETE /v2/groups/{group_openid}/messages/{message_id}，2 分钟内有效），
/// 避免修仙系统消息刷屏。尽力而为：失败仅记日志，不影响业务。
/// </summary>
public interface IGameRecallService
{
    /// <summary>登记一条待撤回的群消息。messageId 为空时忽略。</summary>
    void Schedule(string groupOpenId, string? messageId);
}
