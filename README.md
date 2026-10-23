<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="brand/logo-lockup-dark.svg" />
    <img src="brand/logo-lockup.svg" alt="Mutter" width="320" />
  </picture>
</p>

<p align="center"><em>会看气氛的 QQ 群聊智能助手</em></p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/Vue-3.5-4FC08D?logo=vuedotjs" alt="Vue 3" />
  <img src="https://img.shields.io/badge/Vite-6.0-646CFF?logo=vite" alt="Vite" />
  <img src="https://img.shields.io/badge/SQLite-EF%20Core-003B57?logo=sqlite" alt="SQLite" />
  <img src="https://img.shields.io/badge/QQ%20Bot-Official%20Gateway-12B7F5?logo=tencentqq" alt="QQ Bot" />
  <img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License" />
  <a href="CONTRIBUTING.md"><img src="https://img.shields.io/badge/PRs-Welcome-brightgreen.svg" alt="PRs Welcome" /></a>
</p>

Mutter 是一个基于 **.NET 9** 与 **QQ 官方机器人 API**（WebSocket Gateway 接入）构建的群聊智能助手与会话服务。自带富交互终端控制台（TUI）、Web 管理控制面板、多模态图文识别，以及创新的**群聊择机主动发言（Proactive）机制**。

> ⚠️ **声明**：本项目仅供学习交流与个人研究使用，运行与二次开发请严格遵守 [QQ 机器人平台规则](https://bot.q.qq.com/) 与相关法律法规。

---

## 目录

- [分支说明](#分支说明)
- [核心特性](#核心特性)
- [技术栈与架构](#技术栈与架构)
- [目录结构](#目录结构)
- [下载即用版本 (Releases)](#下载即用版本-releases)
  - [面板内一键更新](#面板内一键更新)
- [快速起步 (Quick Start)](#快速起步-quick-start)
  - [1. 准备环境与凭据](#1-准备环境与凭据)
  - [2. 配置说明](#2-配置说明)
  - [3. 构建与本地运行](#3-构建与本地运行)
- [管理面板开发 (Web Panel)](#管理面板开发-web-panel)
- [生产部署 (Linux & systemd)](#生产部署-linux--systemd)
- [自检工具与质量保证](#自检工具与质量保证)
- [贡献与安全](#贡献与安全)
- [品牌资产](#品牌资产)

---

## 分支说明

| 分支 | 定位 | 维护状态 | 接收 PR |
| --- | --- | --- | --- |
| `main` | 稳定主线，生产版本发布基线 | 维护中，仅维护者合并 | 仅极轻量文档修复 |
| `preview-features` | **新功能特性** 目标分支 | 维护中 | **✓ 接收** 新功能 PR |
| `preview-fixes` | **缺陷修复** 目标分支 | 维护中 | **✓ 接收** Bug 修复 PR |
| `xianxia` | 历史文字 RPG（修仙游戏）只读归档 | **永久冻结** | ✗ 不接受任何 PR |

开发分支从对应的目标分支切出（新功能从 `preview-features`，缺陷修复从 `preview-fixes`），不从 `main` 切。

> 贡献代码前请务必仔细阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。任何安全漏洞请通过 [SECURITY.md](SECURITY.md) 私密提交，请勿公开开启 Issue。

---

## 核心特性

### 1. 智能会话与上下文管理
- **多模型兼容**：支持 OpenAI 标准兼容接口，流式/单次生成响应。
- **群级与专属人设**：支持群聊独立提示词、个性化人设切换（`/char`）。
- **多模态图文识别**：群消息图片自动下载转存并送入视觉模型，内置 GIF 动图嗅探剥离与张数上限保护。
- **容错与修复机制**：内置 CJK 异常断行修复器与 JSON 结构化输出容错提取器。

### 2. 择机主动发言 (Proactive Speak)
- **群语境动态观察**：滑动窗口监听群聊消息，交由 AI 分析研判当前讨论焦点与接话价值。
- **严密多重门禁**：内置静默时段、发言 CD 冷却、单群/全局日配额与 QQ 官方主动消息频控，决策日志全面留痕可溯。
- **判定尺度可调**：提供克制、正常、主动三档研判准则，针对不同活跃度的群自适应介入。
- **按需联网核实**：判定阶段先看图并给出检索词，只有涉及时效事实时才进入第二阶段联网核实并润色；闲聊不额外调用，改写失败自动退回草稿。

### 3. 双运行模式宿主 (ConsoleHost)
- **TUI 终端交互模式**：基于 `Terminal.Gui`，在终端内提供可视化运行面板、连接状态看盘与实时日志滚动。
- **无头服务模式 (`--headless`)**：用于 Linux systemd 等后台服务守护，轻量高效。

### 4. 现代化 Web 管理面板
- **Vue 3 + Element Plus** 构建的单页管理后台（SPA）。
- 提供系统监控、AI 与群配置的可视化编辑、会话记录查询、Token 用量统计与主动发言决策审计。
- **安全鉴权机制**：管理口令 PBKDF2 加盐哈希认证、初始口令首次登录强制修改、暴力破解频率限制、远程 Session 会话吊销。

---

## 技术栈与架构

```
┌────────────────────────────────────────────────────────┐
│                      Mutter 进程                        │
│                                                        │
│  ┌───────────────────┐        ┌─────────────────────┐  │
│  │   Terminal GUI    │  <-->  │  Mutter.ConsoleHost │  │
│  │  (Dashboard/Logs) │        │  (Service Manager)  │  │
│  └───────────────────┘        └──────────┬──────────┘  │
│                                          │             │
│  ┌───────────────────┐        ┌──────────┴──────────┐  │
│  │   Vue 3 Panel     │  HTTP  │      Mutter.Web     │  │
│  │    (SPA 前端)      │ <====> │   (API & SignalR)   │  │
│  └───────────────────┘        └──────────┬──────────┘  │
│                                          │             │
│                               ┌──────────┴──────────┐  │
│                               │     Mutter.Core     │  │
│                               │ (Proactive/Context) │  │
│                               └──────────┬──────────┘  │
│                                          │             │
│                               ┌──────────┴──────────┐  │
│                               │Mutter.Infrastructure│  │
│                               │(EF Core/QQ Gateway) │  │
│                               └─────────────────────┘  │
└────────────────────────────────────────────────────────┘
```

- **语言框架**：C# 13 / .NET 9.0
- **持久化层**：SQLite + Entity Framework Core（启动时自动幂等升级表结构，不走 EF 迁移）
- **通信接口**：QQ 官方机器人 WebSocket 网关 + OpenAPI Client
- **管理前端**：Vue 3 + Vite 6 + Element Plus + SignalR Client

---

## 目录结构

```
Mutter-Bot/
├── src/
│   ├── Mutter.ConsoleHost/    # 宿主进程：集成 TUI 终端界面、服务编排与无头模式
│   ├── Mutter.Web/            # ASP.NET Core API 服务层、静态站点映射与 SignalR Hub
│   ├── Mutter.Core/           # 核心领域层：Prompt 模板、择机判定、指令系统与上下文构建
│   └── Mutter.Infrastructure/ # 基础设施：EF Core、QQ 机器人网关与通信实现
├── web/                       # Web 管理面板前端工程（Vue 3 + Vite）
├── tools/
│   └── ProactiveCheck/        # 自动化回归自检工具（门禁与状态机自测）
├── deploy/                    # Linux 安装脚本 (install.sh) 与 systemd 服务模板
└── Directory.Build.props      # 全局强制构建规则（TreatWarningsAsErrors=true）
```

---

## 下载即用版本 (Releases)

不想从源码构建，可直接从 [GitHub Releases](https://github.com/TangKuku9/Mutter-Bot/releases) 下载对应平台的压缩包：

| 平台 | 推荐包（无需 .NET） | 已装 .NET 运行时 |
| --- | --- | --- |
| Windows x64 | `mutter-bot-vX.Y.Z-win-x64-self-contained.zip` | `mutter-bot-vX.Y.Z-win-x64-framework-dependent.zip` |
| Linux x64 | `mutter-bot-vX.Y.Z-linux-x64-self-contained.tar.gz` | `mutter-bot-vX.Y.Z-linux-x64-framework-dependent.tar.gz` |

- **self-contained**：内置 .NET 9 运行时，解压即用；**framework-dependent**：体积更小，需预装 [ASP.NET Core 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)。
- **Windows**：解压后编辑同目录的 `appsettings.json`（填入 `Mutter:AppId`、`Mutter:ClientSecret` 与面板口令），运行 `mutter-bot.exe` 进入 TUI；后台运行用 `mutter-bot.exe --headless`。
- **Linux**：解压后进入目录执行 `sudo ./install.sh .`（压缩包已内置 `install.sh` 与 systemd 模板），首次安装后编辑 `/opt/mutter-bot/appsettings.json` 并执行 `sudo systemctl restart mutter-bot`。
- Linux 自包含包仍依赖系统 ICU，精简发行版请先安装 `libicu`。
- 校验下载：`sha256sum -c SHA256SUMS.txt`（Windows 可用 `Get-FileHash` 对照文件中的 SHA256）。

> 版本号唯一来源是仓库根目录 [`Directory.Build.props`](Directory.Build.props) 的 `<Version>`。程序集、前端构建与 `/api/status` 都从这里取值；发布标签必须与之一致（例如 `2.0.0` 对应 `v2.0.0`），CI 会校验版本一致性。

### 面板内一键更新

从正式 Releases 解压安装并满足下列条件时，「关于」页会显示 **检查更新 → 立即更新**。点击后先下载并校验最新正式版，完成后弹窗询问是否安装；确认后才会替换文件并重启。选择稍后则保留 **立即安装** 入口，下次点击仍会先弹窗确认。

| 环境 | 一键更新 | 说明 |
| --- | --- | --- |
| Windows x64（TUI 或 `--headless`） | 支持 | 更新助手等待主进程退出后替换文件并重新拉起 |
| Linux x64（systemd 服务） | 支持 | 服务停止后替换文件，由 systemd 拉起新版本 |
| Linux 非 systemd、开发运行、非 x64 | 不支持 | 显示「手动更新」，跳转 GitHub Releases |

- 仅 **publish 产物**（压缩包解压后的安装目录）支持一键更新；从源码直接运行不会执行替换。包内的 `install.json` 是安装标记，请勿删除。
- **分层更新**：自包含包会把 .NET 运行时与程序文件分开。运行时版本一致时只下载约 **5–6MB** 的 `*-app.zip` 应用层包；运行时版本变化时才使用完整包。框架依赖包本身只有 5MB 左右，直接走完整包。
- 「关于 → 更新设置」可选更新方式：**自动**（优先应用层）、**仅完整包**、**仅检查**；也可切换更新源：GitHub 官方、镜像或自定义清单。镜像/自定义源必须配置清单签名公钥，否则无法防止镜像篡改更新包。
- 更新清单是 Release 里的 `update.json`，签名文件是 `update.json.sig`。旧版本客户端仍按 `SHA256SUMS.txt` 下载完整包，不受影响。
- 更新偏好保存在安装目录的 `update-settings.json`，它和 `appsettings*.json`、数据库、日志一样不会被更新覆盖或删除。
- 下载完成后不会自动安装；只有确认安装时才写入待更新标记并触发重启。下载期间面板会显示实时进度条与已下载大小。
- **下载在后台进行**：切换面板页面、刷新或关闭浏览器都不会中断；回到「关于」页会恢复显示当前进度与暂存状态。
- **支持断点续传**：网络中断会从已下载位置自动重试；服务重启或进程异常退出后，进度保存在安装目录的 `.update/` 中，页面会显示「继续下载」。`.update/` 是更新工作目录，下载或暂存期间请勿手动删除。
- 下载不设总时长上限，只对连接与读取停滞做超时保护（连接 90 秒、读取 60 秒无数据即重试）；连续 5 次没有进展才会暂停，之后可手动继续。
- 更新包必须能在 `SHA256SUMS.txt` 中找到校验值，校验失败会删除损坏文件并终止本次更新。
- `appsettings*.json`、`mutter-bot.db*`、`logs/`、`update-settings.json` 不会被覆盖或删除。
- 旧版本程序文件备份在安装目录的 `.backup/vX.Y.Z-<时间戳>/`，仅保留最近一份。
- 更新完成后服务自动重启，面板会话失效，需要重新登录。
- 手动回滚：停止程序，将 `.backup/vX.Y.Z-<时间戳>/` 内的文件复制回安装目录覆盖同名文件（**不要删除** `appsettings*.json` 与数据库），再重新启动。

> **发布者签名（镜像源必需）**：用 ECDSA P-256 私钥对 `update.json` 的原始字节签名，CI 从仓库 Secret `UPDATE_SIGNING_KEY` 读取 PEM 私钥；把公钥填到面板「更新设置 → 签名公钥」。未配置 Secret 时官方 GitHub 源仍可更新，但镜像/自定义源会拒绝未签名清单。
>
> ```bash
> openssl ecparam -name prime256v1 -genkey -noout -out update-private.pem
> openssl ec -in update-private.pem -pubout -out update-public.pem
> # 把 update-private.pem 的内容写入 GitHub Secret：UPDATE_SIGNING_KEY
> ```

---

## 快速起步 (Quick Start)

### 1. 准备环境与凭据

- 安装 **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** (`dotnet --version` 验证)。
- 安装 **[Node.js](https://nodejs.org/)** (`v20.x` 或更高版本)。
- 前往 [QQ 开放平台](https://bot.q.qq.com/) 创建机器人，取得 `AppId` 和 `ClientSecret`，并开通群全量消息、@ 机器人等相应权限。

### 2. 配置说明

配置分为两部分：QQ 机器人凭据与面板口令在 `appsettings.json` 中，AI 服务商配置在面板「AI 设置」里写入 SQLite。

仓库中的 [`src/Mutter.Web/appsettings.json`](src/Mutter.Web/appsettings.json) 只保留占位符，**不要**把真实凭据写进去——它是受 Git 跟踪的文件。本地开发推荐用环境变量覆盖：

```bash
# Linux / macOS
export Mutter__AppId="YOUR_APP_ID"
export Mutter__ClientSecret="YOUR_CLIENT_SECRET"

# Windows PowerShell
$env:Mutter__AppId="YOUR_APP_ID"
$env:Mutter__ClientSecret="YOUR_CLIENT_SECRET"
```

也可以把真实值写进本地 `appsettings.Development.json`（已被 `.gitignore` 忽略），但 ConsoleHost 默认以 Production 环境启动，需要同时设置 `ASPNETCORE_ENVIRONMENT=Development` 才会加载它。

非敏感字段仍可在 `appsettings.json` 中配置，核心结构如下：

```jsonc
{
  "Mutter": {
    "AppId": "YOUR_APP_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET",
    "ApiBaseUrl": "https://api.bot.qq.com"
  },
  "PanelAuth": {
    "Username": "admin",
    // 首次配置可填明文口令；首次成功登录后会自动升级为 PBKDF2 哈希
    "Password": "CHANGE_ME_STRONG_PASSWORD",
    "SessionTimeoutMinutes": 120
  }
}
```

- `Mutter:ApiBaseUrl` 默认 `https://api.bot.qq.com`，一般无需修改。
- `ApiAuth:Token` 可选，仅用于非浏览器客户端以 Bearer Token 调用面板 API。
- 修改 `appsettings.json` 后必须重启进程；运行中的进程不会热加载凭据。
- 面板只读展示打码后的 `AppId` 与连接状态，不提供凭据编辑入口。

> ⚠️ 严禁把真实凭据提交到仓库；`src/Mutter.Web/appsettings.json` 在版本库中只允许保留占位符。

### 3. 构建与本地运行

#### 步骤 A：构建面板前端
> 构建产物输出至 `src/Mutter.Web/wwwroot`（产物不入库），首次运行前必须构建一次：

```bash
cd web
npm ci
npm run build
cd ..
```

#### 步骤 B：启动后端服务

```bash
# 1. 恢复与构建工程（全局开启 Warnings As Errors）
dotnet build Mutter.sln

# 2. 启动控制台宿主（默认进入 TUI 终端看板模式，同时启动 Web 服务）
dotnet run --project src/Mutter.ConsoleHost
```

- **TUI 交互**：终端将展示系统连接状态、实时日志流与热键操作导航。
- **Web 面板**：在浏览器中打开 `http://127.0.0.1:5188`，输入设置的口令即可登录管理。

---

## 管理面板开发 (Web Panel)

在对前端界面进行修改或调试时，可启动 Vite 开发服务器实现热更新（HMR）：

```bash
cd web
npm run dev
```

本地 Vite 开发服务运行在 `http://localhost:5199`，已配置代理自动将 `/api` 与 `/hub` 流量转发至后端的 `http://127.0.0.1:5188`。

---

## 生产部署 (Linux & systemd)

如果不想在本地编译，可直接使用 [Releases](#下载即用版本-releases) 中的 Linux 压缩包。以下为从源码发布的流程，项目提供了专为 Linux 设计的一键部署工具与服务模板：

```bash
# 1. 本地编译前端并发布 Linux 产物
cd web && npm ci && npm run build && cd ..
dotnet publish src/Mutter.ConsoleHost -c Release -r linux-x64 --self-contained false -o ./publish

# 2. 在服务器上通过 root 权限执行部署安装脚本
sudo ./deploy/install.sh ./publish
```

> `dotnet publish` 会自动把 `appsettings.json` 带进发布目录，无需手工复制。
> 使用 `--self-contained false` 发布时，服务器需预先安装 **ASP.NET Core 9 Runtime**（不需要 SDK）。
> 首次部署后编辑 `/opt/mutter-bot/appsettings.json` 填入真实的 `Mutter:AppId`、`Mutter:ClientSecret` 与面板口令，再执行 `sudo systemctl restart mutter-bot`。该文件位于服务器、不在 Git 仓库中，可以安全写入真实凭据。

部署脚本会自动：
- 创建运行用户 `mutter-bot` 与安装目录 `/opt/mutter-bot`；
- 妥善备份已有的 SQLite 数据库（含 `-wal` 与 `-shm` 缓存日志防丢）；
- 注册并启动 systemd 守护服务（以 `--headless` 无头模式运行）。

管理服务：
```bash
sudo systemctl status mutter-bot
sudo journalctl -u mutter-bot -f
```

> 💡 **Linux 运行提示**：若在部分精简版 Linux 系统（如 Alpine 或最小化 Ubuntu）上运行报错，请确保安装了 ICU 基础库：`apt-get install -y libicu-dev`。

### 面板安全部署建议

- **首次登录强制改密**：安装包自带的初始口令（`CHANGE_ME_STRONG_PASSWORD`）是公开信息。用它登录面板时不会进入管理界面，而是强制先修改密码；初始口令被修改前无法获得任何面板会话。请在安装完成后第一时间完成首次登录与改密。
- **公网部署必须走 TLS**：面板默认以 HTTP 明文监听，公网暴露会导致登录口令与会话 Cookie 被嗅探。请只监听 `127.0.0.1` 或用防火墙限制来源，公网访问务必通过 Nginx/Caddy 等反向代理终止 TLS。
- **`PanelAuth:RequireSecureCookie`**：设为 `true` 后会话 Cookie 附加 `Secure` 标记（仅经 HTTPS 传输）。启用 TLS 反代后建议开启。
- 登录接口对同一 IP 连续 5 次失败锁定 15 分钟；面板口令以 PBKDF2（SHA-256、60 万次迭代、随机盐）哈希存储。

---

## 自检工具与质量保证

为了保证发版稳定性与择机发言逻辑的安全可靠，项目自带 `ProactiveCheck` 自动化自检工具：

```bash
# 执行纯逻辑段回归自检（CI 流水线内置，脱离数据库运行）
dotnet run --project tools/ProactiveCheck -- --logic-only

# 全量自检（验证特定版本历史数据库升级与 DI 状态）
dotnet run --project tools/ProactiveCheck -- <sqlite-db-path>
```

仓库根目录开启了 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` 强制约束，任何编译器警告均会导致构建失败。

---

## 贡献与安全

- **参与贡献**：请参阅 [CONTRIBUTING.md](CONTRIBUTING.md) 了解分支模型、提交规范、架构分层以及**不可协商的项目红线**。
- **安全报告**：发现安全隐患请参阅 [SECURITY.md](SECURITY.md) 使用 GitHub Security Advisory 私密通报。

---

## 品牌资产

标志、横版组合、应用图标、配色与使用规则见 [brand/README.md](brand/README.md)。矢量源文件与 PNG / ICO 导出均在 [`brand/`](brand/) 目录。

---

## 许可协议

本项目采用 [MIT 许可证](LICENSE) 开源。
