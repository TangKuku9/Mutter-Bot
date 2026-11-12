# 本地领先远端改动审查（2026-11-11）

本文记录 `codex/ai-settings-panels` 分支及其相邻本地提交的完整审查结论，供合并、发布和后续修复排期使用。审查阶段未修改业务代码；后续修复与验证记录见第 8 节。

## 1. 审查范围

以 `origin/main`（`d66bb13`，`v261110`）为基线，覆盖以下本地改动：

| 位置 | 提交 | 内容 |
|---|---|---|
| `codex/ai-settings-panels` | `c60cae3` | 聊天时间线轻量首屏、独立总数接口、实时普通消息增量合并 |
| 同上 | `d0e6cfe` | 实时补齐与分页快照一致性修复（含页面状态机测试） |
| 同上 | `aecf843` | 分群择机参数稀疏覆盖、参数预设、弹窗宽度修复、Playwright 接入 CI |
| 同上 | `18a3d06` | AI 设置分区界面重构、AI 配置预设库交互重做 |
| 本地 `main` | `6f86258` | MessageBox 被全局样式拉满整屏的修复 |
| 本地 `main` | `6953bf3` | Dashboard 窄屏数字断行修复、触屏帮助按钮命中区扩大 |
| 工作区 | 未跟踪 | `brand/concepts/`（3 张概念图 + `prompts.json`，约 3.3MB） |

共 41 个受跟踪文件、+3218/−702 行。当前没有 stash，也没有其他本地未推送提交。

基线关系：

- `6f86258` 已被 `aecf843` 逐字补回：两侧 `git patch-id --stable` 相同，`git diff main..HEAD -- web/src/styles/panel.css` 为空。
- `6953bf3` 不在当前分支；`git merge-tree --write-tree main HEAD` 双向无冲突并生成同一 tree（`29b0908`），合并 `main` 即可带入，无需单独 cherry-pick。
- `brand/concepts/` 没有被任何构建脚本、品牌生成脚本或页面引用；`prompts.json` 明确标注 `Preview concepts for discussion; not approved production assets.`

## 2. 结论摘要

- 未发现 P0（无迁移删数据、鉴权绕过、SQL 注入或可直接利用的 XSS）。
- 设计要求整体成立：时间线 `latest`/`count`/精确分页分层合理；分群覆盖在所有运行时决策点均已接入；AI 设置的分区、掩码 Key 与 `0`/`false` 保留处理正确。
- 建议合并前处理 5 个代码问题：聊天翻页取消静默校准导致记录静默缺失、AI 外搜隐藏字段阻塞保存、AI 保存成功但回读失败后的假脏、外搜服务商切换沿用旧 Key、群首次保存把无配置群变成停用。
- 发布前必须先升版本号：`261110` 已被 `v261110` 占用，release workflow 会做 tag/程序集/package.json 三处一致性校验。
- 构建、自检、前端单测与浏览器回归均通过；合并 `main` 无冲突。

## 3. 逐块 Review

### 3.1 聊天时间线（`c60cae3` + `d0e6cfe`）

设计要点：

- `/api/chat/timeline/latest` 只取最新一页（多取 1 条判断 `hasMore`），不再等待全量 `COUNT`。
- `/api/chat/timeline/count` 使用同一快照独立补齐精确总数；旧 `/api/chat/timeline` 保留“先统计、再按稳定快照取页”的协议。
- 实时普通消息用 `mergeTimelineMessage` 增量并入；Bot 回复、择机评估、`Resync` 仍触发整页校准，避免部分消息推进快照。
- 后端 `Links()` 从“全范围 bot 消息”收窄为“页内 messageIds”，群名也只查询页内相关群；页内 ID 最多 100，远低于 SQLite 参数上限。

验证到的收益：3 万消息 × 4 热群 × 48 次切换，p50 9ms、p95 11ms、max 14ms；精确总数 32001 条。热态 `latest`/`count` 均为毫秒级。

主要风险集中在前端状态机的交错场景，见 P1-1、P2 聊天组。

### 3.2 分群择机参数（`aecf843`）

设计要点：

- `ProactiveOverrides` 是真正的稀疏覆盖：`null` 表示继承，显式 `0`/`false` 不会被误判为继承。
- 覆盖接入调度（`IsDue`、`Take(WindowMaxMessages)`）、工作流（静默、冷却、日限、模型预算、联网、记忆、附图、历史）、最终投递（`ReplyDispatcher`）和主动通道配额（`ActiveMessageService`）。
- 排队任务在取观察窗口前重读群配置，避免按旧窗口条数/节奏执行；停用群会清理观察窗口。
- 前端 custom/inherit 切换、静默时段原子保存、保存失败保留草稿、群切换取消均经浏览器验证。

逐点核对 `GetProactiveConfigAsync` 的调用方后，没有发现“某个运行时决策点漏了 `WithOverrides`”的主干错误。风险主要在 API 契约、仓储全量覆盖语义和异常数据兼容，见 P1-5 与 P2 后端组。

### 3.3 AI 设置（`18a3d06`）

设计要点：

- 页面拆为模型连接、回复风格、上下文与记忆、联网与检索四个分区；`AiSettingField` 统一标签、提示和帮助入口。
- `normalizeAiSettings` 对 `0`、`false`、掩码 Key 的保留正确；`configPayload` 只提交服务端可写字段，不会回写 `availableProviders` 等。
- 首次读取失败会禁用表单，避免空表单覆盖服务器配置；保存失败会保留草稿。
- 320/390/1024/1440 与深浅色布局回归通过，弹窗宽度受 `calc(100vw - 32px)` 约束。

主要风险：hidden 字段仍参与校验、保存后的 baseline 同步、外搜服务商切换的 Key 归属，见 P1-2/P1-3/P1-4。

### 3.4 `main` 的两个提交

- `6f86258` 已等价存在于当前分支；当前 `panel.css` 的 MessageBox 规则与 `main` 完全一致。
- `6953bf3` 缺失只影响 320px 下 Dashboard Token 数字断行和触屏帮助按钮命中区，不阻断构建或发布；合并 `main` 时自动带入。

### 3.5 工程与发布

- 新控制器由 `AddControllers().AddApplicationPart(...)` 自动发现；新组件会进入 `wwwroot` 发布包；`ProactivePresets` 建表与 EF 模型一致。
- CI 已加入 `npx playwright install --with-deps chromium` 与 `npm run test:ui`，Node 20 当前满足 Playwright 1.62.1 的 `>=20` 要求。
- 主要工程问题是版本号、release 不跑测试、本地 `test:ui` 依赖先 build、文档过时和未跟踪概念图。

## 4. 问题清单

### 4.1 P1：建议合并前修复

#### P1-1 翻页会取消进行中的静默校准，Bot/决策记录静默丢失

- **位置**：`web/src/views/ChatHistory.vue:205-209`（`onPage`）、`:158-168`（`loadLatest`）、`web/src/latestRequest.js:7-20`
- **触发**：第 1 页、跟随底部、实时开启；Bot 回复/择机评估/`Resync` 触发 1.5 秒防抖，`loadLatest({quiet:true})` 请求在途；用户此时点击“更早”。
- **原因**：`onPage` 只检查 `timer || queuedRefresh || pendingMessages.length`，没有检查 `request.pending`。`loadExact()` 的 `request.run` 会 abort 静默请求；`LatestRequest` 对 abort 是静默 resolve（不 reject），旧响应的 `apply` 不执行，`loadLatest` 的 catch 也不会跑，`hasNew` 保持 false。
- **后果**：这次校准本应带回的 Bot 消息/决策记录被丢弃；之后普通消息通过 `advanceMessageSnapshot` 把消息上界推过该记录，而它又不在第 2 页范围内，于是第 1/2 页都看不到，且没有“有新的消息”提示，直到手动刷新或下一次 Bot/决策/`Resync` 事件。已用真实 SFC setup 复现：快照 `150:0`，丢失 151，收到 152 后顶部为 `[152,150,149]`。
- **修复**：`onPage`/`markNewRecords` 把 `request.pending` 纳入条件；或维护 `autoRefreshInFlight` 标记，翻页前先 `markNewRecords()`。补一条“静默请求在途 + 翻页”回归用例。

#### P1-2 外搜数值校验对已关闭/隐藏的通道仍生效，整个 AI 配置无法保存

- **位置**：`web/src/aiSettings.js:25-28`、`web/src/views/AiSettings.vue:395`（字段仅 `externalSearch.enabled` 时渲染）、`:552`（保存按钮被 `formError` 禁用）
- **触发**：在“独立搜索服务”下清空“检索结果条数”或“检索超时”，再切回“模型内建搜索”；或旧数据里这两个值为 `null`/`0`。
- **后果**：`formError` 指向一个页面上根本不存在的字段，保存按钮禁用，保存预设/覆盖预设也一并被 `formError` 拦截；用户无法通过界面消除错误。旧版外搜字段始终可见且保存只校验 budget，这是本次 UI 重排引入的回归。
- **修复**：仅当 `externalSearch.enabled` 时校验外搜数值；或切换通道时把非法值收敛到合法范围。对应单测 `web/tests/aiSettings.test.mjs:46-51` 目前把“关闭也校验”固化成预期，需要一起改。

#### P1-3 保存成功但回读失败后，表单永久显示“有修改未保存”

- **位置**：`web/src/views/AiSettings.vue:1736-1748`（`save`）、`:1509-1526`（`load`）
- **触发**：`PUT /config/ai` 成功（204），随后 `save()` 内的 `GET /config/ai` 因网络/5xx/401 失败。`load()` 自己 catch 掉异常，不向 `save()` 抛错，`baseline` 未更新。
- **后果**：服务端实际已保存，但 UI 和路由离开守卫都认为未保存；用户会重复保存或看到“放弃修改”。若用户把表单改回旧 baseline，`isDirty` 会变 false，但服务端仍是新值。属于存量实现方式，本次重构后仍存在，且新增的 `loadError` 容易让人误以为已处理。
- **修复**：`PUT` 成功后先用本次 `configPayload()` 更新 baseline，再 best-effort 回读；回读失败显示“已保存，但回读失败”，不要用回读结果决定脏状态。

#### P1-4 切换独立搜索服务商时沿用旧 Key，旧密钥会被发往新服务商

- **位置**：`web/src/views/AiSettings.vue:404`（服务商下拉没有 `@change`，Key/地址共用同一 model）、`src/Mutter.Web/Controllers/ConfigController.cs:495-516`（`ApplyExternalSearch`）、`:528-535`（`ResolveSubmittedApiKey`）
- **触发**：已配置 Tavily Key，切到 Brave/custom（或反方向），不修改 Key/地址直接保存或测试。
- **后果**：后端把旧服务商的真实 Key 解析出来，按新 provider 落库；运行时以新 provider 的鉴权头发送，等于把旧密钥交给新服务商。旧 `baseUrl` 也会保留，商用服务会请求错误的网关。**这是存量问题，不是本分支引入**，但本次 UI 重排没有处理，且“留空或保持打码值表示不修改”的提示没有说明 Key 是服务商专属。
- **修复**：provider 变更时清空 Key/BaseUrl 或弹出确认“当前 Key 属于旧服务商”；更彻底的做法是记录 Key 所属 provider，跨 provider 时强制重填。

#### P1-5 群参数首次保存可能把“无 GroupConfig 行”的群静默停用

- **位置**：`src/Mutter.Web/Controllers/ProactiveParametersController.cs:56-62`（新建 `new GroupConfigEntity { GroupId = id }`）、`src/Mutter.Infrastructure/Entities/GroupConfigEntity.cs:6`（`IsEnabled` 默认 false）、`src/Mutter.Core/Services/MessageRouter.cs:33-49`
- **触发**：`KnownGroups` 有行但 `GroupConfigs` 无行。正常启动有 backfill（`AppDbContext.cs:669-675`），入群事件也会自动创建 `IsEnabled=true` 的配置；因此可达性低，只在“入群后配置写入失败/中断”或手工库状态下命中。
- **后果**：保存前该群无配置，`MessageRouter` 对 @ 消息默认放行；保存后插入 `IsEnabled=false` 的行，连普通 @ 回复也停止，但面板显示“参与择机发言已开启”。
- **修复**：新建实体时显式 `IsEnabled = true`（与 backfill/入群自动配置一致）；或返回 409 要求先走群管理启用。补 known-without-config 回归用例。

#### P1-6 发布版本号仍是 261110，tag 已被占用（发布阻断）

- `Directory.Build.props:16`、`web/package.json:3`、`web/package-lock.json` 根版本均为 `261110`，而 `v261110` 已指向 `d66bb13`；`.github/workflows/release.yml:40-41` 要求 tag、程序集、package.json 三处相等。
- 发布前必须统一升到下一个日期版本（按当前日期应为 `261112`；同日再次发布用 `.1`），再打 tag。否则 release 校验失败或更新器认为没有新版本。

### 4.2 P2：值得排期修，不阻断合并

#### 聊天时间线

| 问题 | 位置 / 触发 / 后果 |
|---|---|
| 丢失 Bot/决策推送后增量合并会跨过缺口 | `ChatHistory.vue:233-246`、`MessageEventHandler.cs:397-402`：`AiResponseCompleted`/`ProactiveEvaluated` 发送失败只记 warning；之后普通消息推进上界，Bot 消息既不在第 1 页也不在第 2 页，直到手动刷新。建议增加周期性校准或让 latest 返回上界校验。 |
| 搜索失败后“正在查找…/统计中”永久停留 | `ChatHistory.vue:23、34、169-186`：带关键词的 `loadExact` 失败只设 `loadError`，`total` 仍为 null，footer 不会进入“查找失败”。 |
| 精确端点把未知总数当 0 | `ChatHistory.vue:177`：`Number.isInteger(data.totalCount) ? data.totalCount : 0`，与 DTO “null 不能当 0”的注释相反。 |
| `advanceMessageSnapshot` 接受 `id=0` | `web/src/chatHistory.js:44`（`id < 0`）；服务端重复消息不入库时仍会推送 `Id=0`（`MessageEventHandler.cs:494-500`）。会误置 `hasMore`、触发多余 count，但页面没有新消息。 |
| 每条实时消息都把 `total` 打回 null | `ChatHistory.vue:105-110`：消息间隔小于约 650ms 时长期显示“总数统计中…”，分页器被“更早”替代；count 失败后无自动重试。 |
| 同毫秒排序精度差异 | 后端按完整 `DateTime` tick 排序，前端按毫秒 + ID；极端竞态下页边界顺序可能与刷新后不一致。建议后端也按毫秒对齐或返回高精度排序键。 |
| 留痕被清理时 `events[k.Id]` 可能抛 `KeyNotFound` | `ChatTimelineReader.cs:170、178`：深分页命中 90 天前记录时，`DataRetentionService` 若在两次查询之间删除行，时间线请求 500。既有模式，`/latest` 高频调用放大了暴露面。建议 `TryGetValue` 并跳过。 |

#### 分群择机参数

| 问题 | 位置 / 触发 / 后果 |
|---|---|
| 预设创建/删除与并发 `loadPresets` 互相覆盖 | `ProactiveParameters.vue:177-186、219-241`：首次读取失败后“重试 + 立即存为预设”，旧 GET 响应会覆盖新列表，刚保存的预设从下拉消失但服务端已存在。创建/删除应在 `presetsLoading` 时禁用，或成功后重新加载并按 id 合并。 |
| 清空群级自定义数值会静默变回“跟随全局” | `ProactiveParameters.vue:145-148`、`proactiveParameters.js:23/26-28/47`：清空后来源跳回 inherit、草稿变“已保存”，用户没有提示。建议“清空=继承”显式提示，或保留来源并报校验错误。 |
| 预设空状态/错误状态/排序不一致 | `ProactiveParameters.vue:71-72、181-182、225-231`：读取失败时“内置预设”显示“还没有自定义预设”；创建成功后 `presetsError` 仍为 true；新预设追加在末尾，与后端 `UpdatedAt` 倒序不一致。 |
| 并发删除共用 `deletingPreset` | `ProactiveParameters.vue:232-241`：第一个请求的 finally 会清掉第二个的 loading；重复确认可能发两次 DELETE。 |
| 刷新 spinner 提前结束 / 草稿搁浅 | `ProactiveParameters.vue:161-176、187-191`：刷新中切群会取消群请求，父页 `refreshing` 先复位；`loadGroups` 整体替换列表后已删除群的草稿仍留在 `groupStates`，用户无入口丢弃。 |
| 未知识别键会被旧前端丢弃 | `proactiveParameters.js:18-28`：`onlyParameters` 只保留固定白名单；后端未来新增覆盖字段时，旧前端保存会把未知字段永久删掉。建议透传未知键或后端合并。 |
| 半个静默区间前后端语义不一致 | 前端逐键合并显示“半覆盖”，后端 `ApplyTo` 只有两个键都有才生效且保存返回 400；异常数据下用户看到可保存但实际被拒。 |
| 全局保存无取消/卸载保护 | `Proactive.vue:210-226`：PUT 无 signal，离开后仍可能成功并弹提示；群保存有 `disposed`/abort，全局保存没有。 |

#### AI 设置

| 问题 | 位置 / 触发 / 后果 |
|---|---|
| 应用预设成功后 `loadError` 不清除 | `AiSettings.vue:1675-1681`：配置 GET 失败后成功应用预设，顶部“暂时无法读取配置”横幅仍显示。 |
| `selectedPresetId` 与列表/加载不同步 | `AiSettings.vue:1509-1516`（load 不清）、`:1652-1655`（列表缺 ID 时静默 return）、`:1686-1687`：取消应用确认后仍显示已选中；重新加载后选择残留；列表缺 ID 时按钮可点但无反应。 |
| 删除当前全局人设后假脏 | `AiSettings.vue:1729-1733`：本地把 `form.personality=''` 但 baseline 未更新；前端 `===` 比较、后端 `OrdinalIgnoreCase`，大小写不同会保留已删除人设并写回。 |
| input-number 二次归一化与 baseline 不一致 | `AiSettings.vue:182-191、229-331`：服务端 `temperature=0.75` 显示为 `0.8` 但 PUT 仍是 `0.75`；越界旧值（2.5/20/10）加载后被静默 clamp 并立即标脏。建议 load 阶段按后端规则先收敛再建 baseline，并提示修正。 |
| 空值/畸形响应健壮性 | `aiSettings.js:4-11`：`normalizeAiSettings(null)` 抛 TypeError；`AiSettings.vue:1512-1514` 对 `{}` 会当成功加载；`loadPresets` 不校验数组；外搜 `baseUrl/apiKey` 为 null 时“测试搜索”在 `.trim()` 处抛异常。 |
| “测试搜索”拒绝空 Key，但后端会沿用已保存 Key | `AiSettings.vue:1213-1216`：提示说“留空表示不修改”，前端却直接拦截，与后端 `ResolveSubmittedApiKey` 语义不一致。 |
| “选用”在保存失败时仍显示“已选用” | `AiSettings.vue:1140-1155、814`：先改表单再 `await save()`，`save` 失败返回 false 但外层不看返回值；按钮仍进入禁用“已选用”状态，实际未保存。 |
| 内建探测藏在“模型内建搜索”分支，选用会顺带关闭独立搜索 | `AiSettings.vue:507-524、1140-1151`：用户想选内建策略，必须先切路线，而“选用”保存整张表单会把 `externalSearch.enabled=false` 一并落库。 |
| 验证状态不随参数变更失效 | `AiSettings.vue:404-436、442、510-520`：改 provider/baseUrl/model 后仍显示旧的“最近验证/文字已验证”，直到保存或重测。 |
| 预设只包含主模型 Key，不含外搜 Key | `AiSettings.vue:58、637-640、1663`，`ConfigController.cs:599-604`：UI 的“包含密钥/沿用当前密钥”容易被理解为包含所有 Key；应用带外搜的预设可能 400 或沿用错误环境的 Key。 |
| `maxInputTokens` 在小容量下出现无法修复的提示 | `AiSettings.vue:326`、`tokenBudget.js:13-14`：`availableInput < 512` 时输入框 min=max=512，却要求 `input <= 72`，用户只能自己去降 output/升 context。 |
| 可访问性 | `AiSettingField.vue:4`：radio-group 的 `<label for>` 指向 div，点击 label 不聚焦；hint 未绑定 `aria-describedby`。 |

#### 后端契约与数据边界

| 问题 | 位置 / 触发 / 后果 |
|---|---|
| `PUT /proactive/groups/{id}/config` 省略字段会清空 | `ProactiveParametersController.cs:19-20、63-67`：`Enabled` 非空 bool、`Overrides` 默认 `new()`，省略 `overrides` 会写成 `{}`，省略 `enabled` 会写成 false。当前 UI 总是发全量，属于 API 契约风险。 |
| `SaveGroupConfigAsync` 全量覆盖，partial `GroupConfig` 会清空覆盖 | `ConfigRepository.cs:149-160`：已有行的 `ProactiveOverridesJson` 无条件重写；当前调用方都会先读全量，但任何未来 partial 调用（或 `MessageEventHandler` 的 TOCTOU）都会丢配置。 |
| `Decode` 对单个坏字段“全有或全无” | `ProactiveOverridesCodec.cs:14-19`：JSON 里一个 `1.5` 或溢出整数会让整个覆盖回退为 `{}`，群静默继承全局且无日志。 |
| 半个静默时段在读路径保留 | `ProactiveOverrides.cs:28、54-58`、`ProactiveParametersController.cs:64/88`：读 GET 返回半区间，前端混合显示后保存被 400，用户无法判断是哪一项坏了。 |
| 静默小时归一化不一致 | `ProactiveChatConfig.cs:147` 越界映射为 0，`ProactiveOverrides.cs:36-37` 映射为 23；`WithOverrides` 先 Apply 再 Normalize。正常路径都会先 Decode/Controller 归一化，主要影响直接调用。 |
| `EvalMaxTokens` 上下文钳制只散落在 Controller | `ProactiveParametersController.cs:115-123`、`ProactiveController.cs:162-165`；仓储/Decode 只做 Normalize。绕过 Controller 的写路径会出现“库里超限、运行时再钳”的不一致。 |
| 群记忆双开关 AND，但 API 字段各反映一半 | `GroupConfig.cs:30`、`ProactiveOverrides.cs:17`、`ProactiveWorkflow.cs:337-343`、`ProactiveParametersController.cs:43-47`：`MemoryAllowed` 只看群管理，`EffectiveConfig` 只看覆盖+全局；UI 有提示，但 API 消费者容易误判。 |
| `IsDue` 不检查静默/日限，窗口会被“跳过”消费 | `ProactiveObserveService` 的 `IsDue` 只看窗口/冷却；`ProactiveWorkflow` 在 Take 之后才检查静默/日限。静默期话题不会在结束后补评，且会增加 skipped 留痕噪声。既有设计，群级静默覆盖会放大可见度。 |
| `_scheduled` 清理防御性缺口 | `ProactiveChatHostedService.cs:79-80`：`TryAdd` 在 `TryEnqueue` 之外，若同步抛异常该群会永久跳过；`latestScope` 跨整个 workflow 持有。 |
| 热路径 JSON 解码 / 全量列加载 | `ConfigRepository.cs:63` 每条消息路由都 `Decode`；`:165-182` 的 `GetAllGroupConfigsAsync` 加载全部列。高消息量/多群下有可观测开销。 |
| 预设无数量上限、无更新接口、控制字符未清洗 | `ProactiveParametersController.cs:80-113`：与 AI 预设的 50 条上限不一致；名称/说明只 Trim；`UpdatedAt` 未用注入时钟。当前 Vue 插值无 XSS，但属于纵深防御缺口。 |
| `/api/proactive/status` 仍展示全局值，待评估列表含停用群 | `ProactiveController.cs:293-313`：群级有效值与全局值混用，`PendingWindows` 在停用后到下一 Tick 前仍列出。 |

#### 工程与发布

| 问题 | 位置 / 触发 / 后果 |
|---|---|
| release workflow 不跑测试 | `.github/workflows/release.yml:43-45` 的 panel job 只 `npm ci && npm run build`；tag push 不触发 CI。 |
| `test:ui` 本地依赖先 build，复用 4179 有误测风险 | `web/playwright.config.mjs:16-20`、`web/package.json:13`：直接 `npm run test:ui` 可能跑旧构建；建议加 `pretest:ui` 或 `reuseExistingServer:false`。 |
| Node 20 已 EOL；Playwright 无缓存、失败无 trace 上传 | `.github/workflows/ci.yml:44、69-71`：每次下载浏览器；`trace: retain-on-failure` 但 artifacts 未上传。 |
| 文档过时 | `docs/panel-improvements.md:3` 仍写 `acfabca`/`codex/performance-panel-experience`；未覆盖 AI 设置重构；性能数字与本次复跑不一致。 |
| `brand/concepts/` 3.3MB 未跟踪 | `prompts.json` 明确标注为讨论稿、非生产资产；没有被任何构建/品牌脚本引用。已加入 `.gitignore`。 |

### 4.3 低风险记录

- `/api/chat/timeline/latest` 不接收 `q`，静默忽略搜索参数；当前无调用方传 `q`，建议文档化或显式拒绝。
- `ChatTimelineReader.ReadLatestAsync` 不自带 `pageSize` 下限，直接调用传 0 会返回空页 + `hasMore=true`；控制器已 clamp。
- `ChatHistory.vue` 的 `countKey` 不含 `q`，`loadCount` 却保留 `q` 参数；当前 count 只在无关键词时执行，属于死参数。
- 初次 `subscribeRealtime` 成功会发 `Resync`，进入页面约 1.5 秒后会多一次 quiet latest + count。
- 移动端群选择器 `@change="resetView"` 不判同值，重复选择同群会多一次加载。
- `ActiveMessageService` 本身不检查 `AllowActiveChannel`，当前唯一调用方 `ReplyDispatcher` 有检查，属于 API 边界风险。
- `GetGroup.EffectiveConfig` 不反映总开关、群响应开关和记忆 AND 条件，名称容易误导。
- 掩码 Key 对 9 位 Key 只隐藏 1 位；既有行为，建议提高阈值。
- 外搜结果 URL 直接绑定 `href`，没有 http/https 白名单；Chromium 实测 `javascript:` + `target=_blank` + `noopener` 未在面板上下文执行，未发现可利用 XSS。
- `tools/ProactiveCheck` 对 2026-08-05 的旧库副本会在迁移前插入含 `IsProactive` 的夹具而失败；CI 走 `--logic-only` 不受影响，属于既有工具限制。

## 5. 验证记录

| 命令 / 检查 | 结果 |
|---|---|
| `dotnet build Mutter.sln -c Release` | 0 warning / 0 error |
| `dotnet run --project tools/ProactiveCheck -c Release --no-build -- --logic-only` | 全部通过；含群级参数/预设、群级调度与投递限制、10 万消息时间线去重 |
| 3 万消息 × 4 热群 × 48 次切换 | p50 9ms / p95 11ms / max 14ms；精确总数 32001 |
| `cd web && npm test` | 审查时 43/43；修复并补用例后 64/64（见 8.6） |
| `cd web && npm run build` | 成功（约 7.4 秒） |
| `cd web && npx playwright test` | 23/23 通过 |
| `git merge-tree --write-tree main HEAD` | exit 0，同一 tree `29b0908`，合并无冲突 |
| `git show 6f86258 \| git patch-id --stable` 对比 aecf843 的 panel.css | 相同 patch-id，确认已逐字补回 |
| 较新库副本的完整 ProactiveCheck | 第 1~6 段全部通过 |

## 6. 合并与发布建议

1. 先把 `main` 合入当前分支，带入 `6953bf3`；`6f86258` 已等价存在，不要重复 cherry-pick。合并后重跑全套检查。
2. 修 P1-1/P1-2/P1-3，补三条回归用例；确认 P1-5 是否在本次一并修。
3. 处理 P1-4 的外搜 Key 跨服务商问题：至少在 provider 变更时清空 Key/地址或弹确认；如果本次不修，应在发布说明中明确。
4. P2 优先修：聊天“搜索失败状态”、`total` null→0、`id=0` 守卫；择机预设加载竞态；AI `applyPreset` 的 `loadError`/`selectedPresetId`/删人设假脏。
5. 发布前统一升版本：`Directory.Build.props`、`web/package.json`、`web/package-lock.json` 三处改为 `261112`（或发布当天日期），再打 tag。
6. release 的 panel job 跑 `npm test` + `npm run test:ui`，或让 release 依赖 CI 成功；CI 加 Playwright 缓存和失败 trace 上传。
7. 保持 `brand/concepts/` 不入库；如需保留生成过程，只提交压缩预览和 prompts，并在品牌 README 说明其非生产资产属性。

## 7. 审查方法

- 以 `origin/main` 为基线逐提交阅读 diff，并追踪当前完整文件的调用链。
- 5 个并行子代理分别负责：分群择机后端、聊天时间线前后端、AI 设置前端、择机参数前端、跨切面与 `main` 提交。
- 关键 P1 均由本人复核代码路径与行号；聊天静默校准丢失另用真实 SFC setup 复现，`LatestRequest` 的 abort 语义用独立脚本验证。
- 构建、自检、单测和浏览器回归均实际执行；临时验证脚本与基准均放在仓库外，未修改业务代码。

## 8. 修复记录（2026-11-12）

第 4 节列出的 P1/P2 与工程项已全部处理，并补充了对应回归。分支已合并本地 `main`（`6953bf3`），版本统一升至 `261112`。

### 8.1 P1

| 编号 | 修复内容 | 回归 |
|---|---|---|
| P1-1 | `ChatHistory.onPage` 把 `request.pending` 纳入判断，翻页前先 `markNewRecords()`；带未读提醒回到第 1 页且无关键词时改走 `loadLatest()`（不带旧快照），真正取回缺口；新增 60 秒递归静默校准兜底丢失的 Bot/决策推送。 | `chatHistoryState.test.mjs` 覆盖“静默请求在途 + 翻页”“暂停/重连/校准中缓冲”“60 秒周期校准不叠加请求”等场景。 |
| P1-2 | `aiSettingsError` 只在 `externalSearch.enabled` 时校验检索条数与超时；缺失/非对象外搜回退默认值。 | `aiSettings.test.mjs` 覆盖关闭时不校验、开启时校验及 null 容错。 |
| P1-3 | `save()` 先快照 `payload`，PUT 成功后立即用提交值建立 baseline，再在 `saving=true` 期间 best-effort 回读；回读失败只提示，不把已保存配置误报成未保存；回读响应畸形也提示而不再静默。 | 页面级用例“PUT 成功但回读失败后不再永久假脏”“畸形响应不建立基线”。 |
| P1-4 | 前端切换服务商清空 Key/地址并提示重填；后端新增 `ResolveProviderAwareApiKey` / `ClearStaleExternalSearchSubmit`，跨服务商时空值、旧打码值和旧网关一律不沿用；探测与落库共用同一判据。 | `ProactiveCheck` 新增“外搜 Key 服务商归属与旧打码兼容”；前端服务商切换用例断言 Key 为空且保存前提示补填。 |
| P1-5 | `SaveGroup` 新建 `GroupConfigEntity` 时显式 `IsEnabled = true`；`GetGroup` 对已知但无配置行的群按参与响应展示，不再显示为停用。 | `ProactiveGroupContractAsync` 覆盖 known-without-config 首次保存与部分更新。 |
| P1-6 | `Directory.Build.props`、`web/package.json`、`web/package-lock.json` 全部升到 `261112`；release 的三处版本校验可继续通过。 | `dotnet build` 与前端构建均使用新版本号。 |

### 8.2 聊天时间线 P2

| 问题 | 处理 |
|---|---|
| Bot/决策推送丢失后增量合并跨过缺口 | 每 60 秒静默校准；请求在途时不叠加；用户上翻/打开详情时只记新记录提醒。 |
| 搜索失败状态永久停留 | 搜索摘要与页脚显示“查找失败，请重试”，提供重试按钮；主错误横幅保留。 |
| 精确端点把未知总数当 0 | `totalCount` 非整数时保持 `null`，不再伪装成 0。 |
| `advanceMessageSnapshot` 接受 `id=0` | 改为 `id <= 0` 拒绝；后端重复事件不再推送 `Id=0`（`SaveIncomingAsync` 返回是否真正入库）。 |
| 每条实时消息把总数打回 null | 已有精确总数时直接 +1；无总数时才调度 count。 |
| count 失败无重试 | 5s/10s 两次退避重试，仍失败才显示“总数暂无法统计”；视图重置后重算重试预算。 |
| 同毫秒排序精度差异 | 前端解析 `createdAt` 小数秒到 7 位 tick，按“毫秒 + tick + kind + id”排序，与后端 `Time → Kind → Id` 一致。 |
| 留痕被清理时 `events[k.Id]` 抛异常 | `MaterializeAsync` 改用 `TryGetValue` 跳过已删除行；`/latest` 自带 `pageSize` 钳制。 |
| 轻量首屏静默忽略 `q` | `/timeline/latest` 收到非空 `q` 返回 400，明确要求使用精确查询。 |

保留项：初次 `Resync` 仍会在进入页面约 1.5 秒后多一次 quiet latest + count；这是建立实时基线的一部分，请求发生在第 1 页且不改变用户正在阅读的旧页，未再压缩。

### 8.3 分群择机 P2

| 问题 | 处理 |
|---|---|
| 预设并发加载/创建/删除互相覆盖 | `presetLoadToken` 代次守卫，旧 GET 响应作废；创建/删除成功清理错误态并按后端顺序重排。 |
| 预设空状态/错误状态/排序不一致 | 读取失败与“没有自定义预设”分开显示；`sortPresets` 保持内置顺序、自定义按 `UpdatedAt` 倒序 / `Id` 升序。 |
| 并发删除共用 loading | 删除进行中忽略新请求，`finally` 只清理自己的标记。 |
| 刷新 spinner 提前结束 / 草稿搁浅 | `refresh` 等待当前 `groupLoadTask`；群列表刷新后删除已消失群的草稿，当前群消失则回全局。 |
| 清空数值静默变继承 | 清空已有覆盖时提示“已恢复为跟随全局”。 |
| 未知识别键被旧前端删除 | `onlyParameters` 排除已知全局字段、透传其余非 null 键；后端 `[JsonExtensionData] Extra` 原样保存；`draftSignature` 按键名排序，避免插入顺序误报脏。 |
| 半个静默区间语义不一致 | 读取路径 `Normalize` 清空半区间；群参数与预设写入路径在 `Normalize` 前返回 400；全局小时越界统一钳到 0–23。 |
| 全局保存无取消/卸载保护 | `Proactive.vue` 增加 `AbortController`、卸载检查和 `disposed` 守卫。 |
| 群记忆双开关 AND 但 API 各反映一半 | `MemoryAllowed` 保留“群管理是否允许”；`EffectiveConfig.MemoryInjectionEnabled` 与 `/status` 的 `Quotas[].Effective` 都计算全局 + 群管理 + 覆盖的最终 AND。 |
| `IsDue` 不检查静默/日限 | 静默期或群/全局日限达到时清空缓冲并返回 false；不再产生无意义评估与 skipped 留痕。 |
| `_scheduled` 清理缺口 / scope 过长 | `TryAdd`/`TryEnqueue` 分别 try/catch 并清理；队列回调的 scope 只覆盖读配置与取窗口，health lease 覆盖整个工作流。 |
| 预设无上限/无更新/控制字符 | 50 条上限；新增 `PUT /proactive/presets/{id:int}`；名称与说明去控制字符；`UpdatedAt` 使用注入时钟；覆盖 JSON 16KiB 上限。 |
| 状态接口混用全局值、待评估含停用群 | `PendingWindows` 过滤全局开关 + 群停用 + 未参与择机；配额行增加 `Effective`。 |
| `ActiveMessageService` 不检查 `AllowActiveChannel` | 服务层按生效配置检查，返回 `active_channel_disabled`。 |
| `EvalMaxTokens` 只在 Controller 钳制 | `ConfigRepository` 在群覆盖与全局保存时按当前 `AiConfigs.ContextWindowTokens` 统一 `ClampOutputTokens`。 |
| `GetAllGroupConfigsAsync` 全量列加载 | 保持现状：现有实现已加载全部列且每一列都被 `GroupConfig` 消费，投影无法减少列；作为已知热路径记录。 |

保留项：`ProactiveCheck` 对 2026-08-05 旧库副本仍会在迁移前插入含 `IsProactive` 的夹具而失败；CI 使用 `--logic-only` 合成库，不受影响。

### 8.4 AI 设置 P2

| 问题 | 处理 |
|---|---|
| 应用预设后 `loadError` 不清 | 成功应用后清零；响应异常时置顶提示重新读取。 |
| `selectedPresetId` 与列表/加载不同步 | 拆分“选择待应用”和“已应用”两个标记；加载/手动保存/产生未保存修改时清空；列表缺 ID 时提示并重新加载；取消确认保留选择便于重试。 |
| 删除当前人设后假脏 | 前端大小写不敏感比较，删除前若表单干净则同步 baseline；后端也按 `OrdinalIgnoreCase` 清理群引用。 |
| input-number 二次归一化 | 新增 `sanitizeAiSettingsForUi`，在建立 baseline 前按后端顺序收敛容量/输出/输入/温度/外搜数值；发生收敛时提示用户确认。 |
| 空值/畸形响应健壮性 | `normalizeAiSettings` 容忍 null；外搜字符串 null → `''`；`ingestConfig` 要求可用字段数组；预设列表校验数组；探测前 `String(...).trim()`。 |
| “测试搜索”空 Key 被前端拦截 | 移除前端 Key 必填拦截（保留地址必填）；同服务商空值/打码由后端沿用已保存 Key，跨服务商由后端提示重填。 |
| 选用保存失败仍显示“已选用” | `applyProbe` 检查 `save()` 返回值，失败回滚 endpoint/shape/enabled 并提示。 |
| 内建探测藏在独立搜索分支 | 探测按钮移到搜索卡片常显行；选用内建策略不再改动 `externalSearch.enabled`；外搜开启时提示内建策略仅在关闭或兜底时生效。 |
| 验证状态不随参数变更失效 | 基于 baseline 计算 `builtinTargetStale` / `esTargetStale`，地址、模型、外搜 provider/baseUrl/Key 变化后显示“需重新检测/测试”。 |
| 预设只含主模型 Key 容易误解 | 标签与帮助文案统一为“主模型密钥”；预设摘要新增外搜开关/provider/baseUrl，应用确认时提示目标变化需重填搜索 Key。应用预设允许先落库再补填 Key（普通保存仍强制要求）。 |
| 小容量下 `maxInputTokens` 提示无解 | `tokenBudget` 在可用输入 < 512 时给出“模型上下文容量不足……请提高上下文容量或降低输出上限”的可执行提示。 |
| 可访问性 | `AiSettingField` 保留 `label[for]`，点击标签聚焦控件；hint 生成稳定 id 并绑定到控件容器。 |

保留项：`MaskApiKey` 短 Key 阈值从 8 提到 12 后，升级前打开的旧页面若提交 9–12 位 Key 的旧格式打码值，后端会识别为“未修改”（兼容旧 `4+****+4` 打码），不会把打码文本写成新 Key。

### 8.5 工程与发布

- 版本三处统一为 `261112`；`docs/panel-improvements.md` 更新分支说明、补 AI 设置分区章节、运行时回归组数改为 41。
- CI/Release 使用 Node 22；两者都执行 `npm test` 与 `npm run test:ui`，缓存 Playwright 浏览器，并在失败时上传 `artifacts/panel-browser-tests`。
- `package.json` 增加 `pretest:ui`，`playwright.config.mjs` 设 `reuseExistingServer: false`，避免本地误跑旧构建。
- `brand/concepts/` 已加入 `.gitignore`，仍保持讨论稿属性，不进入发布包。

### 8.6 修复后验证（2026-11-12）

| 命令 / 检查 | 结果 |
|---|---|
| `dotnet build Mutter.sln -c Release` | 0 warning / 0 error |
| `dotnet run --project tools/ProactiveCheck -c Release --no-build -- --logic-only` | 41/41 组通过（含聊天、择机覆盖/预设/调度、外搜 Key 归属、时间线规模） |
| `cd web && npm test` | 64/64 通过 |
| `cd web && npm run build` | 成功（约 6.8 秒） |
| `cd web && npx playwright test` | 23/23 通过 |
| 3 万消息 × 4 热群 × 48 次切换 | p50 9ms / p95 11ms / max 13ms；精确总数 32001 |
| `git merge main` | 无冲突，带入 `6953bf3` |
| 合成 10 万消息 + 1 万留痕 | 首页 54ms；单群 11ms；来源 45–51ms；查找 92ms |

未执行项：真实部署数据库上的完整 ProactiveCheck（第 4~6 段）与真实 QQ/付费模型联调；本次全部使用合成临时库和假 HTTP，不读取部署数据。

## 9. 二次复核修复（2026-11-12）

针对 `4977624` 之后的复核，补齐以下四处边界；本节更新第 8 节对应条目的最终行为。

| 问题 | 修复与回归 |
|---|---|
| AI 预设换搜索地址仍继承旧 Key | 预设恢复按服务商与地址共同判断是否继承，落库明确区分预设恢复与普通表单保存，禁止空值再次补回旧 Key。后端自检走真实创建、序列化、应用、落库与回读，覆盖同目标、同服务商换地址、换服务商、免 Key 以及普通保存打码值。 |
| 界面收敛值被当作已经保存 | `ingestConfig` 保留服务器原值作为基线，精度或范围调整成为待确认草稿，允许保存并触发离开提醒；保存成功后才清除修改状态。真实 SFC setup 与浏览器回归覆盖 `0.75 → 0.8`、失败重试和保存后的状态。 |
| 群记忆生效状态与运行时不同 | 全局记忆是可覆盖的默认值；先应用群级覆盖，再与群管理权限取 AND。后端自检穷举全局默认、群管理权限和三态覆盖的 12 种组合。 |
| 快照内延迟消息重复增加总数 | 总数单独维护最近精确统计的快照与其后已计数的消息 ID，避免用当前页判断是否计过数；不推进快照的推送不取消或延后统计。状态测试覆盖旧消息、实时消息滑出当前页、乱序新消息和统计回读。 |

验证：`dotnet build Mutter.sln --no-restore` 为 0 warning / 0 error；`ProactiveCheck --logic-only` 41/41 组通过；`npm test` 67/67；`npm run test:ui`（含构建）24/24。验证使用合成库与假 HTTP。
