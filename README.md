# QQBot-Linux

基于 **QQ 官方机器人 API**（WebSocket 接入）的群聊修仙 RPG + AI 聊天机器人，附带 Web 管理面板。

> 仅供学习交流，请遵守 QQ 机器人平台规则与相关法律法规。

## 功能概览

**AI 聊天**
- 群聊/私聊 AI 对话，上下文记忆、群级人设与自定义人设（`/char`）
- 多模态：消息带图自动附图（GIF 嗅探剔除，防上游 500）
- 择机主动发言（Proactive）：观察群内消息窗口，由 AI 判定是否值得接话，静默时段/冷却/日配额多重门禁，全部留痕
- 上游 CJK 断行修复、JSON 结构化输出自动修复

**修仙 RPG（群内文字游戏）**
- 修炼体系：境界突破、功法、灵根、炼丹、探索、挂机结算、每日任务、切磋
- 群世界状态：天地灵气、AI 生成随机事件、月度排行榜推送
- 富交互 UI：Markdown 面板 + 按钮键盘（能力探测与多级降级），游戏消息自动撤回防刷屏
- 反刷机制：内容级去重、种子混入状态指纹防重掷复现、EF 乐观并发令牌防并发覆写

**Web 管理面板**（`web/`，Vue 3 + Element Plus）
- 仪表盘、聊天历史、AI 用量统计、AI/群/私聊配置、修仙存档管理、择机发言管理与决策留痕
- 会话登录：PBKDF2 口令哈希、登录失败限流、会话列表与远程注销

## 技术栈

- 后端：C# / .NET 9（ConsoleHost 进程托管 + ASP.NET Core 面板 API + SignalR 推送）
- 数据库：SQLite（EF Core）
- 前端：Vue 3 + Vite + Element Plus，构建产物输出至 `src/QQBot.Web/wwwroot`
- 接入：QQ 官方机器人 WebSocket 网关（群全量消息 / @ 消息 / 互动事件）

## 目录结构

```
src/
  QQBot.Core/            # 领域层：AI 聊天、修仙游戏引擎、Proactive 决策、富 UI
  QQBot.Infrastructure/  # EF Core（SQLite）、QQ API 客户端、WebSocket 网关、托管服务
  QQBot.Web/             # 面板 API（ASP.NET Core）
  QQBot.ConsoleHost/     # 进程宿主（生产以 systemd 运行）
web/                    # 面板前端源码（Vite）
tools/                  # 自检工具（ProactiveCheck / SmokeCheck / UiGolden）
deploy/                 # install.sh 与 systemd 单元
```

## 快速开始

1. 在 QQ 开放平台创建机器人，取得 `AppId` / `ClientSecret`，并申请 needed intents。
2. 复制 `src/QQBot.Web/appsettings.example.json` 为 `appsettings.json`，填写凭据与面板口令。
3. 构建并运行：

```bash
dotnet build QQBot-Linux.sln
dotnet run --project src/QQBot.ConsoleHost
```

4. 面板前端（可选，仓库已含构建产物）：

```bash
cd web && npm ci && npm run build   # 输出到 ../src/QQBot.Web/wwwroot
```

数据库为 SQLite，首次启动自动建表；已有库由 `EnsureSchemaUpgradedAsync` 幂等升级（含历史主键类型修复）。

## 配置说明

- 运行时配置只读取 `appsettings.json`；带环境后缀的配置（如 `appsettings.Development.json`）仅本机使用，不入库。
- 面板口令支持 PBKDF2 哈希格式，旧明文口令在首次登录成功后自动升级为哈希。
- 机器人发消息受平台频控约束，程序内置分钟/日双重配额并钳制在官方上限内。

## 自检工具

```bash
dotnet run --project tools/ProactiveCheck -- <sqlite-db-path>   # 择机发言门禁/迁移幂等/DI 装配
dotnet run --project tools/SmokeCheck                           # 切磋/每日任务/文案池行为冒烟
dotnet run --project tools/UiGolden                             # 纯文本渲染黄金基线
```
