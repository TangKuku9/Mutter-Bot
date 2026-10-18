# 贡献指南

感谢你愿意为 Mutter-Bot 出力！Mutter 是一个基于 **.NET 9** 与现代 Web 技术的 QQ 机器人与智能会话服务。

在参与代码贡献前，建议具备以下技术基础：
- 熟悉 **C# / .NET 9**，理解依赖注入（DI）、异步编程与 ASP.NET Core Web API / SignalR 机制。
- 熟悉 **Vue 3** + **Vite** 前端开发（用于管理面板交互）。
- 了解 **Entity Framework Core** 与 SQLite 的基本操作及迁移机制。
- 了解 [QQ 机器人官方开放平台](https://bot.q.qq.com/wiki/) 的基础通信协议（WebSocket Gateway、Intents 事件与频控规范）。
- 新增代码须严格满足本地构建 **0 Warning / 0 Error** 与自检测试。

> ⚠️ **合规声明**：本项目仅供学习交流与个人研究使用，任何贡献均须遵守 QQ 机器人平台规则与相关法律法规。

---

## 目录

- [1. 搭建开发环境](#1-搭建开发环境)
  - [1.1 工具依赖](#11-工具依赖)
  - [1.2 推荐 IDE 与插件](#12-推荐-ide-与插件)
  - [1.3 获取代码与本地配置](#13-获取代码与本地配置)
  - [1.4 启动后端服务](#14-启动后端服务)
  - [1.5 启动管理面板前端 (Dev Server)](#15-启动管理面板前端-dev-server)
  - [1.6 数据库说明](#16-数据库说明)
- [2. 项目架构与模块划分](#2-项目架构与模块划分)
- [3. 开发指引：修改与新增](#3-开发指引修改与新增)
  - [3.1 修改现有逻辑](#31-修改现有逻辑)
  - [3.2 新增核心能力 / 指令](#32-新增核心能力--指令)
  - [3.3 扩展管理面板 API 与界面](#33-扩展管理面板-api-与界面)
- [4. 可用核心资源与服务 API](#4-可用核心资源与服务-api)
- [5. 代码检查与自检](#5-代码检查与自检)
  - [5.1 构建检查 (0 Warning / 0 Error)](#51-构建检查-0-warning--0-error)
  - [5.2 逻辑自检 (ProactiveCheck)](#52-逻辑自检-proactivecheck)
  - [5.3 前端构建自检](#53-前端构建自检)
  - [5.4 提交前自查清单](#54-提交前自查清单)
- [6. 提交规范 (Commit)](#6-提交规范-commit)
- [7. 分支模型与 PR 流程](#7-分支模型与-pr-流程)
  - [7.1 分支规划](#71-分支规划)
  - [7.2 维护与合并权限](#72-维护与合并权限)
  - [7.3 标准 PR 步骤](#73-标准-pr-步骤)
  - [7.4 分支提升与同步（维护者）](#74-分支提升与同步维护者)
  - [7.5 CI 自动化流水线](#75-ci-自动化流水线)
- [8. 项目红线（不可协商）](#8-项目红线不可协商)
- [9. 安全漏洞反馈与开源许可](#9-安全漏洞反馈与开源许可)

---

## 1. 搭建开发环境

### 1.1 工具依赖

- **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)**（`net9.0`；依赖采用浮动版本号：`Microsoft.Extensions.*` 为 `10.0.*`，`EntityFrameworkCore.*` / `Microsoft.Data.Sqlite` 仍为 `9.0.*`——库版本线独立于框架版本）
- **[Node.js](https://nodejs.org/)**（`^18.0.0 || ^20.0.0 || >=22.0.0`，推荐使用 LTS 20.x，Vite 6 编译前端面板所需）
- **[Git](https://git-scm.com/)**

### 1.2 推荐 IDE 与插件

- **Visual Studio 2022**（17.12+）或 **JetBrains Rider** / **Visual Studio Code**
- 若使用 **VS Code**，建议安装以下插件：
  - [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) / [C#](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp)：提供 C# 语言高亮、补全与调试支持。
  - [Vue Language Features (Volar)](https://marketplace.visualstudio.com/items?itemName=Vue.volar)：为 `web/` 目录下的 Vue 3 组件提供支持。
  - [Prettier](https://marketplace.visualstudio.com/items?itemName=esbenp.prettier-vscode)：前端代码格式化。

### 1.3 获取代码与本地配置

1. 将本仓库 Fork 至你的 GitHub 个人账户。
2. 克隆本地并初始化配置文件：

```bash
git clone https://github.com/<your-username>/Mutter-Bot.git
cd Mutter-Bot

# 从模板创建本地配置文件（该文件已被 .gitignore 忽略，绝不要提交真实凭据）
cp src/Mutter.Web/appsettings.example.json src/Mutter.Web/appsettings.json
```

3. 编辑 `src/Mutter.Web/appsettings.json`，填入在 [QQ 开放平台](https://bot.q.qq.com/) 申请的机器人测试凭据（`AppId`、`ClientSecret`）以及 AI 服务商的配置。

### 1.4 启动后端服务

本仓库提供两种宿主运行模式，按需选择：

- **方式 1：TUI 终端控制台宿主（调试机器人主要逻辑常用）**
  ```bash
  dotnet run --project src/Mutter.ConsoleHost
  ```
  控制台包含 Dashboard、日志监视器与配置看板等 Terminal.Gui 界面。

- **方式 2：Web 宿主（提供管理面板 API 与 SignalR Hub）**
  ```bash
  dotnet run --project src/Mutter.Web
  ```
  默认监听 `http://127.0.0.1:5188`。

### 1.5 启动管理面板前端 (Dev Server)

面板前端工程位于 `web/` 目录，基于 Vite + Vue 3：

```bash
cd web
npm install
npm run dev
```

启动后访问提示的本地地址（通常为 `http://localhost:5173`），Vite 已配置反向代理，会自动将 `/api` 和 `/hub` 请求转发给后端的 `http://127.0.0.1:5188`。

> 💡 **构建产物说明**：生产构建产物 `src/Mutter.Web/wwwroot/` 被 `.gitignore` 忽略，**严禁将构建产物提交入库**。

### 1.6 数据库说明

- 本地开发采用 **SQLite**，首次启动程序会自动完成建表（`AppDbContext.EnsureSchemaUpgradedAsync` 执行幂等初始化与升级）。
- 项目**不使用 EF Core 迁移**。`src/Mutter.Infrastructure/Migrations/` 下只有一份 2026-07-30 的旧基线，已冻结，与当前模型早已不符。加表、加列、改列请直接改 `AppDbContext.EnsureSchemaUpgradedAsync()`，写成可在已有库上重复执行的幂等 SQL；不要执行 `dotnet ef migrations add`，原因见该目录下的 `README.md`。
- 运行时生成的数据库文件存放在 `data/` 或根目录，均已被 `.gitignore` 忽略，不得提交到 Git。

---

## 2. 项目架构与模块划分

仓库采用分层与职责分离架构，各工程目录划分如下：

```
Mutter-Bot/
├── src/
│   ├── Mutter.Core/            # 核心业务逻辑（无 UI / 无外部具体协议绑定的纯领域与服务层）
│   ├── Mutter.Infrastructure/  # 基础设施层（EF Core 数据库、QQ 网关与 HTTP 客户端、AI 接入）
│   ├── Mutter.Web/             # Web API 宿主（Controller、SignalR Hub、面板鉴权与会话）
│   └── Mutter.ConsoleHost/     # 控制台/终端 TUI 宿主（基于 Terminal.Gui 的运行容器）
├── web/                        # 管理面板前端工程（Vue 3 + Vite + Element Plus）
├── tools/
│   └── ProactiveCheck/         # 独立自检工具（门禁断言、择机发言逻辑回归、迁移验证）
├── deploy/                     # 部署脚本与 systemd 配置模板
└── Directory.Build.props       # 全局 MSBuild 规则（0 Warning / 0 Error 强制配置）
```

- **`Mutter.Core`**：包含消息解析（`MessageParser`）、主动择机发言大脑（`Proactive` 状态机与 Prompt）、上下文规模剪裁（`ContextBuilder`）、指令处理（`BotCommandService`）以及实体与接口契约。
- **`Mutter.Infrastructure`**：包含 QQ WebSocket 客户端（`QqGatewayWebSocketClient`）、QQ 官方 API 实现、SQLite 上下文（`AppDbContext`）与仓储实现。
- **`Mutter.Web`**：对外暴露 RESTful API 与实时日志 Bus，提供轻量安全鉴权与配置写入。
- **`web/`**：独立的 SPA 前端工程，构建后静态输出至 `Mutter.Web/wwwroot`。

---

## 3. 开发指引：修改与新增

### 3.1 修改现有逻辑

1. **AI 对话上下文与提示词**：
   - 关注 `src/Mutter.Core/Services/ContextBuilder.cs` 与 `AiChatService.cs`。
   - 保证上下文构建不超出 `MaxTokens` / `MaxContextMessages`。
2. **主动发言（择机发言）规则**：
   - 核心决策入口位于 `src/Mutter.Core/Proactive/ProactiveSpeaker.cs`。
   - 门禁逻辑与去重判定位于 `GroupMessageGate.cs` 与 `ProactiveVerdict.cs`。
   - **注意**：修改判定分支必须在 `tools/ProactiveCheck` 中补充或更新相应断言！

### 3.2 新增核心能力 / 指令

1. 若需要新增机器人指令，在 `src/Mutter.Core/Services/BotCommandService.cs` 中增加解析与路由分支。
2. 新增领域接口定义在 `src/Mutter.Core/Interfaces/`，对应实现置于 `Mutter.Infrastructure/Services/`。
3. 在 `src/Mutter.Core/ServiceCollectionExtensions.cs` 或 `Mutter.Infrastructure/ServiceCollectionExtensions.cs` 中完成 DI 依赖注入注册。

### 3.3 扩展管理面板 API 与界面

1. **后端 API**：
   - 在 `src/Mutter.Web/Controllers/` 新增或修改控制器方法。
   - 涉及修改配置的接口，必须在服务端对参数范围进行严格收敛（参考 [6.6 安全底线](#66-安全底线)）。
2. **前端页面**：
   - 进入 `web/src/views/` 增加页面或修改组件，使用 Element Plus 保持 UI 风格统一。
   - 运行 `npm run build` 确保无打包错误。

---

## 4. 可用核心资源与服务 API

在编写业务逻辑时，尽量复用既有组件与接口，避免自行造轮子：

| 接口 / 服务类 | 命名空间 | 核心职责说明 |
| --- | --- | --- |
| `IQqBotApiService` | `Mutter.Core.Interfaces` | QQ 官方 OpenAPI 交互封装（发送群消息、主动消息、获取群资料等） |
| `IQqGatewayConnection` | `Mutter.Core.Interfaces` | QQ WebSocket 网关连接管理与事件流订阅 |
| `IAiChatService` | `Mutter.Core.Interfaces` | 通用大模型流式/单次对话抽象 |
| `IStructuredAiService` | `Mutter.Core.Interfaces` | 带有 JSON Schema 结构化提取能力的大模型接口 |
| `IContextBuilder` | `Mutter.Core.Interfaces` | 群聊/私聊多轮上下文剪裁、Token 控制与提示词拼接 |
| `IMessageRepository` | `Mutter.Core.Interfaces` | 聊天记录、活跃消息与日志的持久化检索存储 |
| `IConfigRepository` | `Mutter.Core.Interfaces` | 机器人全局及各群差异化配置存取 |
| `GroupMessageGate` | `Mutter.Core.Services` | 群消息门禁（频率限制、去重过滤、触发概率计算） |
| `ReplySeqTracker` | `Mutter.Core.Services` | 官方 API 消息 reply_seq 回复序号安全追踪 |

---

## 5. 代码检查与自检

发起 Pull Request 前，请确保以下所有本地检查均已全绿通过。

### 5.1 构建检查 (0 Warning / 0 Error)

仓库根目录由 [`Directory.Build.props`](Directory.Build.props) 全局开启了 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。任何警告在本地编译时都将视为错误中断构建：

```bash
dotnet build Mutter.sln
```
*（提示：NuGet 依赖已知漏洞告警 NU190x 已豁免，交由 Dependabot 统一处理）*

### 5.2 逻辑自检 (ProactiveCheck)

项目提供了专用的回归自检工具：

```bash
# 运行纯逻辑回归段（1~3 段，无任何数据库依赖，CI 必跑项）
dotnet run --project tools/ProactiveCheck -- --logic-only

# 全量自检（额外测试 SQLite 迁移幂等与 DI 装配，需传入指定测试库路径）
dotnet run --project tools/ProactiveCheck -- <sqlite-db-path>
```

> **注意**：若改动涉及择机发言判定、去重、上下文窗口等逻辑，**必须**在 `ProactiveCheck` 中补充对应的断言逻辑！

### 5.3 前端构建自检

若修改了 `web/` 下的代码，必须验证前端能够正常构建成功：

```bash
cd web
npm run build
```

### 5.4 提交前自查清单

- [ ] `dotnet build Mutter.sln` 报告 **0 warning / 0 error**
- [ ] `dotnet run --project tools/ProactiveCheck -- --logic-only` 运行全绿通过
- [ ] 若修改了 `web/` 前端，`npm run build` 打包通过
- [ ] `git status` 检查：没有误加入 `wwwroot/`、`*.db`、`appsettings.json` 等生成物或敏感文件
- [ ] 提交历史干净，没有混杂无意义的代码格式化或与本 PR 目标无关的改动

---

## 6. 提交规范 (Commit)

采用 [Conventional Commits](https://www.conventionalcommits.org/) 规范，简述部分**推荐使用中文**：

```
<type>: <简述说明>

[可选详细正文：修改背景、动机与设计]
```

常用 `type` 类别：
- `feat`: 新增功能特性
- `fix`: 修复缺陷或 Bug
- `docs`: 仅文档更新（如 README、CONTRIBUTING 等）
- `refactor`: 代码重构（未改变对外行为）
- `perf`: 性能优化
- `test`: 测试用例或自检工具改动
- `build`: 构建工程、依赖项或 CI 工作流改动
- `chore`: 辅助工具、杂项脚本

破坏性变更在 type 后附带感叹号（例如 `refactor!: 重构配置模型结构`），并在正文中说明破坏性原因及升级指引。

---

## 7. 分支模型与 PR 流程

### 7.1 分支规划

| 分支 | 用途 | 推送权限 | 接收 PR 规则 |
| --- | --- | --- | --- |
| `main` | 稳定主线，生产发布版本 | 仅维护者 | 仅接收极微小的文档/错别字修复 |
| `preview-features` | **新功能特性** PR 目标分支 | 仅维护者 | **接受** 新功能特性 PR |
| `preview-fixes` | **缺陷修复** PR 目标分支 | 仅维护者 | **接受** Bug 修复 PR |
| `xianxia` | 历史修仙 RPG 功能只读存档 | 无人（冻结） | **不接受任何 PR** |

开发分支从它将要提 PR 的那个分支切出，不从 `main` 切。

### 7.2 维护与合并权限

- **写权限隔离**：外部贡献者无主仓库直推权限，必须在自己的 Fork 仓库中工作并提 PR。
- **合并由维护者裁决**：即便获得协作者写权限，也不要自行合并 PR。
- **CI 绿灯是必要非充分条件**：CI 检查全部通过后，维护者将按[项目红线](#8-项目红线不可协商)与代码质量进行人工 Review。

### 7.3 标准 PR 步骤

1. **Fork 仓库**到个人空间，从 **PR 的目标分支**切出开发分支（不是 `main`）：

   ```bash
   # 缺陷修复 -> 从 preview-fixes 切，PR 也提给 preview-fixes
   git switch -c fix/my-bugfix origin/preview-fixes

   # 新功能 -> 从 preview-features 切，PR 也提给 preview-features
   git switch -c feat/my-new-feature origin/preview-features

   git push -u origin fix/my-bugfix
   ```

   切点与落点一致，PR 的提交列表里就只有你自己的提交。从 `main` 切会把 `main` 上的合并提交一并列进去——内容差异为零，纯属历史噪音。
2. 保持原子性：**一个 PR 专注于解决一件事**，改动越聚焦，合并越高效。
3. 提交 PR 时正确选择目标分支：
   - 新功能特性 -> 目标分支选择 `preview-features`
   - 缺陷与问题修复 -> 目标分支选择 `preview-fixes`
4. 认真填写 PR 描述：说明改动动机、核心变化点及验证方法（建议附带自检输出或复现截图）。

### 7.4 分支提升与同步（维护者）

`preview-*` 上的改动按发布节奏攒批提升到 `main`：

1. 开 PR：`preview-fixes → main`（新功能则 `preview-features → main`），让改动先过 CI。
2. 合并时把合并提交标题写成 `Merge branch 'preview-fixes': <一句话>`，与 `main` 既有历史一致。
3. 等 `main` 上新合并提交的 CI 全绿。`preview-*` 的直推要求被推提交已有全绿检查，这一步不能跳。
4. 把两个 `preview-*` 分支快进到 `main` 并推送：

   ```bash
   git fetch origin
   git switch preview-fixes && git merge --ff-only origin/main && git push origin preview-fixes:preview-fixes
   git switch preview-features && git merge --ff-only origin/main && git push origin preview-features:preview-features
   ```

   两步指向同一个新提交，推第一个会按分支重新触发一次 CI；推第二个时可能撞上 `required status checks are in progress`，等那一轮跑完再重推即可。

若某个 `preview-*` 分支还有未提升的独有提交，`--ff-only` 会拒绝：先把它提升到 `main`，再回来对齐。

### 7.5 CI 自动化流水线

每个向本仓库发起的 PR 及推送均会自动执行 GitHub Actions [`.github/workflows/ci.yml`](.github/workflows/ci.yml)：
1. **构建与自检 (.NET)**：在 Linux 环境下验证 `dotnet restore` + `dotnet build`（含警告即错误）+ `ProactiveCheck --logic-only`。
2. **面板前端构建**：在 Node 20 环境下执行 `npm ci && npm run build`，并校验 `.NET` 与前端版本号一致。
3. **Release**：推送 `v*` 标签时执行 [`.github/workflows/release.yml`](.github/workflows/release.yml)，流程见 7.6。

### 7.6 版本号与 Release（维护者）

- **版本号唯一来源**：仓库根目录 `Directory.Build.props` 的 `<Version>`。`web/package.json` 的 `version` 必须同步，CI 会校验两者一致；不要在代码中另写版本常量。
- **发布标签**：在 `main` 的发布提交上打 `vX.Y.Z` 标签并推送。标签必须与 `<Version>` 完全对应（`v2.0.0` ↔ `2.0.0`），发布流水线会先校验再打包。
- **产物**：工作流在 Windows 与 Linux runner 上分别构建 `win-x64`、`linux-x64` 的 self-contained 与 framework-dependent 包，附带 `SHA256SUMS.txt`，并创建 **draft** Release。
- **发布前检查**：在 GitHub Release 页面确认四个压缩包、校验和与发布说明无误后，再手动点击 Publish release。

---

## 8. 项目红线（不可协商）

> ⚠️ 以下红线规则不可协商。违反任一条的 PR 将被直接关闭，已有合并将被回退。

### 8.1 凭据与密钥安全
- **严禁提交任何真实密钥**：包括 `AppId`、`ClientSecret`、大模型 `ApiKey`、面板 Token 等。
- `src/Mutter.Web/appsettings.json` 在仓库中仅允许保留占位符（如 `YOUR_APP_ID`）。真实凭证请使用本地环境变量或本地 `appsettings.Development.json`。
- 若不慎误提交凭证：**必须立即在对应平台重置轮换凭据**。仅删除 Git Commit 无法清除历史泄漏风险。

### 8.2 群成员隐私保护
- **严禁**在代码、注释、测试数据、PR 截图或 Issue 中包含真实群号、OpenID、用户昵称、头像或私有链接。
- 所有单元测试与 Mock 数据一律使用合成虚拟标识符（例如 `11112222333344445555666677778888`）。
- 部署产生的真实聊天记录绝对不可上传至公共代码仓库。

### 8.3 运行时数据与生成文件防泄露
以下内容**绝对不入库**（已被 `.gitignore` 保护，严禁使用 `git add -f`）：
- 数据库文件：`*.db`、`*.db-wal`、`*.db-shm`、`*.sqlite*`
- 运行时日志：`logs/`、`*.log`
- 构建中间与发布文件：`bin/`、`obj/`、`publish/`、`src/Mutter.Web/wwwroot/`
- 本地私密配置文件：`appsettings.Development.json`、`secrets.json`

### 8.4 数据库兼容性与不可逆操作
- **严禁删除或重构现有业务数据表**：已有部署运行中存有生产数据，删表不可逆。
- 迁移逻辑必须保证**幂等执行**：重复运行不报错、不丢失历史行。
- 涉及 Schema 结构变动的 PR 必须提供明确的数据影响面与回滚说明。

### 8.5 官方平台合规要求
- **严禁破坏或绕过 QQ 官方频控限制**：发送调用必须受控于分钟/天双重配额。
- 严禁实现自动批量加群、批量骚扰私聊、爬取敏感群员名单等违反平台规则的行为。
- 严禁加入任何旨在规避平台内容审核或封禁对抗的绕行机制。

### 8.6 服务端安全防护
- 管理面板口令必须使用 **PBKDF2 等加盐强哈希算法**，严禁明文入库。
- **可配置字段必须在服务端进行硬性边界收敛**：前端表单校验可被绕过，因此后端对 `MaxTokens`、`MaxContextMessages` 等数值写入时必须钳制在合法安全区间。
- 新增 Controller API 端点必须正确集成鉴权中间件。

### 8.7 依赖与范围控制
- **严禁修改 `xianxia` 分支**：该分支为只读冻结存档。
- 严禁擅自引入未经验证的遥测、用户跟踪或外部数据上报模块。
- 引入新的第三方 NuGet 或 npm 依赖项须在 PR 中充分说明必要性。

---

## 9. 安全漏洞反馈与开源许可

- **报告安全漏洞**：如果你发现了 Mutter 的潜在安全漏洞（如鉴权绕过、凭据风险或注入攻击），**请勿提交公开 Issue**。请通过 GitHub 仓库顶部的 **Security → Report a vulnerability** 开启私密安全通报，维护团队将及时评估与响应。详情参阅 [SECURITY.md](SECURITY.md)。
- **开源协议**：本项目基于 [MIT 许可证](LICENSE) 开源。你向本项目提交的任何代码，均默认视为同意以 MIT 许可授权给本项目及所有使用者。
