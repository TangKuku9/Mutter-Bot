# AI 与消息运行时

Mutter 保持单进程、SQLite 和现有配置接口。运行时采用会话调度、应用工作流、统一 AI 执行入口和协议适配器。配置预设、QQ 搜索形状和图片处理继续兼容。

```mermaid
flowchart TD
    Gateway[GatewayHostedService] --> Intake[消息解析、路由与必要的历史持久化]
    Intake --> Queue[ConversationWorkQueue]
    Scheduler[ProactiveChatHostedService] --> Queue
    Queue --> Reply[直接回复工作流]
    Queue --> Proactive[ProactiveWorkflow]
    Reply --> AI[AiChatService / StructuredAiService]
    Proactive --> AI
    AI --> Executor[AiModelExecutor + IChatClient 管线]
    Executor --> Providers[OpenAI 兼容 / Gemini 原生适配器]
    Reply --> Dispatcher[ReplyDispatcher]
    Proactive --> Dispatcher
    Dispatcher --> Ledger[SQLite 发送意图、配额与投递回执]
    Dispatcher --> QQ[QQ API]
```

`GatewayHostedService` 由 Generic Host 管理，网关异步事件可以等待。消息处理完成接纳后即返回，耗时生成交给 `ConversationWorkQueue`。同一群或私聊按顺序执行，不同会话可并行。队列最多接纳 256 项、单会话 16 项，包含正在执行的工作。执行槽为 3，主动工作最多占 1 个槽。用户工作在 2 分钟后过期并得到超时提示；主动工作等待超过 45 秒即跳过。同一会话仍遵守入队顺序。

停机先停止网关接入与主动调度，再限时排空会话队列，超时后取消剩余任务。队列满时明确拒绝接纳，不创建额外的后台任务。需要保存的用户消息在触发通知和生成前写入数据库；只允许观察的群消息仍只进入内存窗口。

**模型接入。** `IAiChatService` 继续作为业务外观。`AiModelExecutor` 使用 `Microsoft.Extensions.AI` 10.0.0 的稳定 `IChatClient` / `ChatClientBuilder` / `DelegatingChatClient`，统一消息、取消和调用记录。新增此依赖的目的，是复用标准调用契约及可组合管线，减少业务对具体客户端的耦合；不引入外部遥测上传或新的云服务。

现有 HTTP 序列化保留在 Infrastructure 的自定义适配器中，避免 SDK 丢失中转站搜索参数。每次调用绑定一份配置快照，不通过修改共享鉴权头实现模型切换；进行中的请求使用原快照，下一轮读取新配置。普通聊天、图片字节、JSON mode、厂商搜索参数、模型用量及 Gemini 截断原因均经过映射。

当前传输保持非流式行为，`IChatClient` 的流式入口返回一次完整更新。严格 JSON Schema、本地函数调用、URI 多模态内容和服务端有状态会话需要另行适配，当前会明确拒绝，避免静默忽略。接入 `IChatClient` 不代表所有 Provider 拥有相同能力。

**调用成本与容错。** 一条生成流程共享 120 秒截止时间和最多 4 次真实 HTTP 尝试；会话工作另有 3 分钟执行上限。结构化模式只在明确格式不支持或内容无法解析时降级。鉴权失败、网络中断、超时和 5xx 不因格式降级而重新生成；输出截断也不按相同输出上限再试一次。协议已知拒绝仍可按既有搜索策略降级。

结构化重试、图文两阶段成功返回的用量会累加。每次调用记录用途、耗时、尝试序号、已返回的 token 用量与失败类型，不新增内容遥测。上游没有返回的用量无法准确推算；生成流程失败之前已完成阶段的调用记录仍可在本地日志核查。生成完成后即使 QQ 发送失败，其已知用量仍留在发送账本并计入面板统计，不会与成功聊天记录重复统计。

**输入预算。** `MaxTokens` 继续控制输出。新增 `ContextWindowTokens`（默认 32768）与 `MaxInputTokens`（默认 16384）分别表示模型容量和本地输入上限，支持面板及预设保存。实际输入预算取本地上限与“模型容量减输出预留、1024 安全余量”中的较小值。最终检查包含系统词、人设、当前消息、历史、图片及 JSON mode 余量。优先逐条裁掉旧历史；当前消息与固定提示本身超限时，调用前报错。

这里仍采用估算：文本每个 UTF-16 字符按 1 token，图片每张预留 4096 token。它不能替代各模型的 tokenizer 或图片计价规则，切换模型时应按服务商规格校准容量和预算。历史条数仍独立限制输入，不会为了填满预算补入更多消息。

**投递与配额。** `OutgoingReplies` 将生成结果与平台发送分开，状态为 `pending / sent / failed / unknown / skipped`。先持久化发送意图，再调用平台；只有确认发送后才加入 `ChatMessages`。网络结果不确定时保留 `unknown`，不补发主动消息。`pending` 和 `unknown` 不会在启动时自动重放，需结合平台消息和本地日志核查。

平台确认成功后，回执或月度展示计数写入失败不会反转成发送失败。未完成回执的意图仍保留，并保守计入主动冷却及日配额。这种方式不承诺外部 QQ API 的 exactly-once 投递。

主动工作开始时检查内存及持久化配额；真正发送前重新读取全局开关、群启用状态、参与状态、静默时段和主动通道设置。SQLite 事务内预占群/全局日配额及冷却，防止并发群同时越过上限。升级前已有的主动聊天记录也参与配额计算。`ActiveMessageAttempts` 在发送前原子检查并持久化每次主动 HTTP 尝试的分钟/日限制，富消息降级的真实重发也计数。

新版本的尝试记录可在重启后恢复；旧版本只保留月累计，首次升级时无法精确还原升级前的主动发送日计数。观察窗口仍可随重启丢弃。

**数据库兼容与回退。** 升级只新增 `OutgoingReplies`、`ActiveMessageAttempts` 和 AI 配置的两个预算列，使用现有幂等升级流程，不删除业务表或历史记录。新表包含生成内容、投递状态和运行时计数，应按现有聊天数据库的隐私边界管理。回退程序版本时保留数据库及新增列/表，旧程序可忽略它们；无需执行 DROP 或删除数据。

验证命令：

```powershell
dotnet build Mutter.sln
dotnet run --project tools/ProactiveCheck -- --logic-only
dotnet run --project tools/ProactiveCheck -- --runtime-only
cd web
npm run build
```

`--logic-only` 包含原有纯函数检查与新的运行时回归；`--runtime-only` 单独执行后者。运行时检查使用合成标识符、临时 SQLite 和假 HTTP，不读取部署库、不调用真实 QQ 或付费模型。覆盖会话顺序、队列容量/优先级/过期/取消、最终请求预算、协议映射、重试与用量、动态配置、投递失败、回执失败、配额恢复、并发预占、发送前策略及旧模型升级。
