# QQBot-Linux

基于 **QQ 官方机器人 API**（WebSocket 接入）的群聊 AI 聊天机器人，附带 Web 管理面板。

> 仅供学习交流，请遵守 QQ 机器人平台规则与相关法律法规。

## 分支说明（重要）

| 分支 | 内容 | 维护状态 |
| --- | --- | --- |
| `main` | AI 聊天 + 择机发言 + 管理面板 | **唯一在维护的分支**，所有改动提交到这里 |
| `xianxia` | 上述内容 + 修仙 RPG（群内文字游戏） | **冻结的备份**，不再更新 |

修仙功能（境界/突破/炼丹/探索/挂机结算/每日任务/切磋/群世界/AI 随机事件/月榜/富交互按钮面板）
已于某次重构中从 `main` 剥离，成套保留在 `xianxia` 分支，相当于给这个玩法留了一份完整存档。

两点需要知道：

- **两边已各自演进。** 剥离之后 `main` 上的共享基础设施修正在 `xianxia` 上**不存在**，
  例如上下文条数保证、token 输入预算、择机去重与图片张数可配等。
  若将来要恢复修仙玩法，建议以 `main` 为基线重新并入，而不是直接切过去用。
- **数据表仍保留在 `main` 的库里。** `PlayerProfiles` / `GroupWorlds` / `PlayerInventories` /
  `GameCooldowns` / `CultivationEventLogs` 五张表的建表与补列迁移仍在
  `AppDbContext.EnsureSchemaUpgradedAsync` 中，但 `main` 不对它们建实体、不做读写。
  这是刻意的：已有部署里可能存着玩家数据，删表不可逆。

## 功能概览

**AI 聊天**
- 群聊/私聊 AI 对话，上下文记忆、群级人设与自定义人设（`/char`）
- 多模态：消息带图自动附图，张数可在面板调整（GIF 嗅探剔除，防上游 500）
- 上游 CJK 断行修复、JSON 结构化输出自动修复

**择机主动发言（Proactive）**
- 观察群内消息窗口，由 AI 判定是否值得接话；窗口内图片随文字一起交给模型读
- 多重门禁：静默时段、同群冷却、单群/全局日配额、主动消息频控，全部留痕可查
- 判定规模与 @ 回复解耦：背景历史条数与判定输出上限在择机面板单独设置
- 发言倾向三档（克制/正常/主动），改的是判定标准本身而非数值阈值

**Web 管理面板**（`web/`，Vue 3 + Element Plus）
- 仪表盘、聊天历史、AI 用量统计、AI/群/私聊配置、择机发言管理与决策留痕
- 会话登录：PBKDF2 口令哈希、登录失败限流、会话列表与远程注销

**指令**
- `/help` `/status` `/groups` `/group` `/private` `/char`（`/ui` 与 `/quiz` 已随各自功能移除）

## 技术栈

- 后端：C# / .NET 9（ConsoleHost 进程托管 + ASP.NET Core 面板 API + SignalR 推送）
- 数据库：SQLite（EF Core）
- 前端：Vue 3 + Vite + Element Plus，构建产物输出至 `src/QQBot.Web/wwwroot`
- 接入：QQ 官方机器人 WebSocket 网关（群全量消息 / @ 消息 / 互动事件）

## 目录结构

```
src/
  QQBot.Core/            # 领域层：AI 聊天、上下文构建、Proactive 决策、富交互能力门控
  QQBot.Infrastructure/  # EF Core（SQLite）、QQ API 客户端、WebSocket 网关、托管服务
  QQBot.Web/             # 面板 API（ASP.NET Core）
  QQBot.ConsoleHost/     # 进程宿主（生产以 systemd 运行）
web/                    # 面板前端源码（Vite）
tools/                  # 自检工具（ProactiveCheck）
deploy/                 # install.sh 与 systemd 单元
```

## 快速开始

1. 在 QQ 开放平台创建机器人，取得 `AppId` / `ClientSecret`，并申请所需 intents。
2. 复制 `src/QQBot.Web/appsettings.example.json` 为 `appsettings.json`，填写凭据与面板口令。
3. 构建并运行：

```bash
dotnet build QQBot-Linux.sln
dotnet run --project src/QQBot.ConsoleHost
```

4. 面板前端：构建产物不入库，需自行构建（不构建则只有 API 与网关，没有面板页面）：

```bash
cd web && npm ci && npm run build   # 输出到 ../src/QQBot.Web/wwwroot
```

数据库为 SQLite，首次启动自动建表；已有库由 `EnsureSchemaUpgradedAsync` 幂等升级（含历史主键类型修复）。

## 配置说明

- 运行时配置只读取 `appsettings.json`；带环境后缀的配置（如 `appsettings.Development.json`）仅本机使用，不入库。
- 面板口令支持 PBKDF2 哈希格式，旧明文口令在首次登录成功后自动升级为哈希。
- 机器人发消息受平台频控约束，程序内置分钟/日双重配额并钳制在官方上限内。
- 面板字段在服务端统一钳制：前端输入框的 min/max 可被 API 直连绕过，
  因此 `MaxTokens`、`MaxContextMessages`、`MaxAttachedImages` 等在写入前都会收敛到合法区间。

## 自检工具

```bash
dotnet run --project tools/ProactiveCheck -- <sqlite-db-path>   # 择机发言门禁/去重/上下文规模/迁移幂等/DI 装配
```

该工具的迁移段（第 4 段）需要一个**处于特定中间版本**的库作为基准：直接拿最新库或过旧的库运行，
这一段可能因环境不匹配而失败。该段不写死任何具体群号，按传入库内的实际数据自校验。
纯逻辑段（1~3）不依赖数据库，任何情况下都应全绿。
