# Mutter

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
- [快速起步 (Quick Start)](#快速起步-quick-start)
  - [1. 准备环境与凭据](#1-准备环境与凭据)
  - [2. 配置说明](#2-配置说明)
  - [3. 构建与本地运行](#3-构建与本地运行)
- [管理面板开发 (Web Panel)](#管理面板开发-web-panel)
- [生产部署 (Linux & systemd)](#生产部署-linux--systemd)
- [自检工具与质量保证](#自检工具与质量保证)
- [贡献与安全](#贡献与安全)

---

## 分支说明

| 分支 | 定位 | 维护状态 | 接收 PR |
| --- | --- | --- | --- |
| `main` | 稳定主线，生产版本发布基线 | 维护中，仅维护者合并 | 仅极轻量文档修复 |
| `preview-features` | **新功能特性** 目标分支 | 维护中 | **✓ 接收** 新功能 PR |
| `preview-fixes` | **缺陷修复** 目标分支 | 维护中 | **✓ 接收** Bug 修复 PR |
| `xianxia` | 历史文字 RPG（修仙游戏）只读归档 | **永久冻结** | ✗ 不接受任何 PR |

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

### 3. 双运行模式宿主 (ConsoleHost)
- **TUI 终端交互模式**：基于 `Terminal.Gui`，在终端内提供可视化运行面板、连接状态看盘与实时日志滚动。
- **无头服务模式 (`--headless`)**：用于 Linux systemd 等后台服务守护，轻量高效。

### 4. 现代化 Web 管理面板
- **Vue 3 + Element Plus** 构建的单页管理后台（SPA）。
- 提供系统监控、配置可视化编辑、会话记录查询、Token 用量统计与主动发言决策审计。
- **安全鉴权机制**：管理口令 PBKDF2 加盐哈希认证、暴力破解频率限制、远程 Session 会话吊销。

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
- **持久化层**：SQLite + Entity Framework Core（自动幂等升级迁移）
- **通信接口**：QQ 官方机器人 WebSocket 网关 + OpenAPI Client
- **管理前端**：Vue 3 + Vite 6 + Element Plus + Pinia + SignalR Client

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

## 快速起步 (Quick Start)

### 1. 准备环境与凭据

- 安装 **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** (`dotnet --version` 验证)。
- 安装 **[Node.js](https://nodejs.org/)** (`v20.x` 或更高版本)。
- 前往 [QQ 开放平台](https://bot.q.qq.com/) 创建机器人，取得 `AppId` 和 `ClientSecret`，并开通群全量消息、@ 机器人等相应权限。

### 2. 配置说明

复制配置模板生成本地运行时配置：

```bash
cp src/Mutter.Web/appsettings.example.json src/Mutter.Web/appsettings.json
```

编辑 `src/Mutter.Web/appsettings.json`，核心字段如下：

```jsonc
{
  "QqBot": {
    "AppId": "YOUR_APP_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  },
  "Ai": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "sk-...",
    "Model": "gpt-4o-mini"
  },
  "PanelAuth": {
    // 首次配置可直接填明文口令，首次成功登录后系统会自动哈希为 PBKDF2 格式
    "PasswordHash": "YourStrongPassword"
  }
}
```

> ⚠️ 核心配置中的敏感凭据切勿提交到代码仓库。

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

本地 Vite 开发服务运行在 `http://localhost:5173`，已配置代理自动将 `/api` 与 `/hub` 流量转发至后端的 `http://127.0.0.1:5188`。

---

## 生产部署 (Linux & systemd)

项目提供了专为 Linux 设计的一键部署工具与服务模板：

```bash
# 1. 本地编译前端并发布 Linux 产物
cd web && npm ci && npm run build && cd ..
dotnet publish src/Mutter.ConsoleHost -c Release -r linux-x64 --self-contained false -o ./publish

# 2. 复制配置文件到发布目录
cp src/Mutter.Web/appsettings.json ./publish/

# 3. 在服务器上通过 root 权限执行部署安装脚本
sudo ./deploy/install.sh ./publish
```

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

## 许可协议

本项目采用 [MIT 许可证](LICENSE) 开源。
