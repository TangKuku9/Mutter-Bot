# 贡献指南

感谢你愿意为 Mutter 出力。本文说明分支模型、提交流程，以及**不可协商的项目红线**。
红线部分请务必读完再动手——其中多条是为了保护群成员隐私和你的账号安全。

> 本项目仅供学习交流，使用与贡献均须遵守 QQ 机器人平台规则与相关法律法规。

---

## 1. 分支模型

| 分支 | 用途 | 谁能推 | 接受 PR |
| --- | --- | --- | --- |
| `main` | 稳定主线，正式发布 | 仅维护者 | ✗ |
| `preview-features` | **新功能**的 PR 目标分支 | 维护者 + PR | ✓ |
| `preview-fixes` | **缺陷修复**的 PR 目标分支 | 维护者 + PR | ✓ |
| `xianxia` | 修仙 RPG 冻结存档 | 仅维护者 | ✗ |

规则：

- **社区 PR 一律提交到 `preview-features` 或 `preview-fixes`，不要直接提到 `main`。**
  维护者会把预览分支合并进 `main`。
- `xianxia` 是刻意冻结的存档，**不接受任何 PR、不接受合并**。若你想恢复修仙玩法，
  请按 README 的建议以 `main` 为基线重新实现。
- 只含文档或仓库治理的极小改动（例如修错别字、补链接）可以提到 `main`。

### 预览分支必须与 main 保持同源

预览分支是"接 PR 的暂存区"，不是长期分叉。合并进 `main` 之后，请把预览分支快进到新的 `main`：

```bash
git fetch origin
git switch preview-features
git merge --ff-only origin/main      # 快进；若报错说明已分叉，需要先处理冲突
git push origin preview-features
```

`preview-fixes` 同理。**维护者合并 PR 后有责任执行这一步**，否则两个预览分支会逐渐累积分叉，
后续 PR 的冲突会越来越难收拾。

---

## 2. 本地开发

### 依赖

- **.NET 9 SDK**（`net9.0`，本项目用 `9.0.*` 版本号浮动）
- **Node.js** `^18.0.0 || ^20.0.0 || >=22.0.0`（Vite 6 的要求；仅在改动/构建面板前端时需要）

### 起步

```bash
git clone https://github.com/TangKuku9/mutter-bot.git
cd mutter-bot

# 配置：仓库只跟踪占位符，真实凭据请写进这个本地文件（已被 .gitignore 忽略）
cp src/Mutter.Web/appsettings.example.json src/Mutter.Web/appsettings.json

dotnet build Mutter.sln
dotnet run --project src/Mutter.ConsoleHost
```

在 QQ 开放平台创建机器人，取 `AppId` / `ClientSecret`，并申请群全量消息、@ 消息、互动事件等
所需 intents。

### 面板前端

**构建产物不入库**，`src/Mutter.Web/wwwroot` 被 `.gitignore` 忽略。仓库克隆下来时没有面板页面，
需要自行构建：

```bash
cd web && npm ci && npm run build    # 输出到 ../src/Mutter.Web/wwwroot
```

开发时可用 `npm run dev`（Vite 会把 `/api` 与 `/hub` 代理到 `http://127.0.0.1:5188`）。

### 数据库

SQLite，首次启动自动建表；已有库由 `AppDbContext.EnsureSchemaUpgradedAsync` 幂等升级。
运行时数据库默认落在 `data/`，**不入库**。

### 自检

```bash
dotnet run --project tools/ProactiveCheck -- <sqlite-db-path>
```

纯逻辑段（1~3）不依赖数据库，任何情况下都应全绿；迁移段（4）需要一个**处于特定中间版本**的库
作为基准，环境不匹配时会失败。**若你的改动涉及择机发言的门禁、去重或上下文规模，请为它补上断言。**

---

## 3. 提交规范

使用 Conventional Commits，**描述用中文**（与仓库既有风格一致）：

```
<type>: <简述>

<可选正文：为什么这么改>
```

| type | 用途 |
| --- | --- |
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` | 文档 |
| `refactor` | 重构（不改变外部行为） |
| `perf` | 性能 |
| `test` | 测试与自检 |
| `build` | 构建、依赖、仓库配置 |
| `chore` | 杂项 |

破坏性变更在 type 后加 `!`，并在正文里说明迁移方式。例如：

```
refactor!: 剥离修仙功能到 xianxia 分支，主分支专注 AI 聊天/择机发言
```

---

## 4. 提交前检查清单

- [ ] `dotnet build Mutter.sln` **0 warning / 0 error**
- [ ] 若改动了 `web/`，`npm run build` 通过
- [ ] 若改动了业务逻辑，`tools/ProactiveCheck` 跑完没有 `FAIL`
- [ ] `git status` 里没有意外文件（尤其没有被 `.gitignore` 忽略却被 `git add -f` 强加进来的）
- [ ] 提交里没有混进无关的格式化或重构噪音

---

## 5. PR 流程

1. 从 `main` 切出你的分支：`git switch -c fix/proactive-dedup main`
2. 一个 PR 只做一件事；改动小、目的单一，评审才快
3. 目标分支按性质选 `preview-features`（新功能）或 `preview-fixes`（缺陷修复）
4. PR 描述里写清：**改了什么、为什么、怎么验证的**
5. 涉及行为的改动，附上复现步骤或自检输出

---

## 6. 项目红线

以下条目不可协商。违反任一条的 PR 会被直接关闭；已合并的会被回退。

### 6.1 凭据与密钥

- **绝不提交** `AppId`、`ClientSecret`、AI 服务商 `ApiKey`、面板口令或任何 token。
- `src/Mutter.Web/appsettings.json` 只允许占位符（`YOUR_APP_ID` 等）。
  真凭据请放 `appsettings.Development.json` 或环境变量——它们已被 `.gitignore` 忽略。
- **一旦误提交：立刻轮换该凭据**（平台重置 secret、改面板口令）。
  只删掉提交**不等于**修复——历史里仍能翻出来。

### 6.2 群成员隐私

- **绝不**把真实群号、群 openid、成员 openid、昵称、头像链接写进代码、注释、测试用例、
  文档、issue 或 PR 截图。
- 测试与自检一律使用合成假数据（仓库既有风格如 `11112222333344445555666677778888`、
  `AAAABBBBCCCCDDDDEEEEFFFF00001111`）。
- 聊天记录只存在于部署本地的数据库中，**不得**进入仓库或任何公开渠道。
- 需要引用"线上现象"说明来由时，只描述现象，不要附带可识别的标识符。

### 6.3 运行时数据与构建产物

**不入库**（见 `.gitignore`）：

- 数据库：`*.db` / `*.db-wal` / `*.db-shm` / `*.sqlite*`
- 日志：`logs/`、`*.log`
- 构建：`bin/`、`obj/`、`publish/`、`src/Mutter.Web/wwwroot/`
- 环境配置：`appsettings.*.json`、`secrets.json`

### 6.4 数据安全（不可逆操作）

- **不得删除或重建已有数据表。** 已有部署里可能存着真实的玩家与聊天数据，删表不可逆。
  这正是 `main` 不再使用、却仍在 `EnsureSchemaUpgradedAsync` 中保留
  `PlayerProfiles` / `GroupWorlds` / `PlayerInventories` / `GameCooldowns` /
  `CultivationEventLogs` 五张表的原因。
- 迁移代码必须**幂等**：重复执行安全，且不丢已有行。
- 破坏性的 schema 变更必须在 PR 中说明影响面与回滚方案。

### 6.5 平台合规

- **不得绕过或放宽 QQ 官方频控上限。** 配额必须钳制在官方限制内（当前实现为分钟/日双重配额）。
- 不得实现自动加群、批量私聊、爬取成员列表、规避风控等违反平台规则的能力。
- 不得加入任何意图规避平台审核或封禁的机制。

### 6.6 安全底线

- 面板口令必须走 PBKDF2 哈希，不得明文入库。
- **面板可调字段必须在服务端钳制。** 前端输入框的 `min`/`max` 可被 API 直连绕过，
  因此 `MaxTokens`、`MaxContextMessages`、`MaxAttachedImages` 等在写入前都要收敛到合法区间。
- 新增 API 端点必须接入既有鉴权中间件。
- 不得引入命令注入、路径穿越、远程代码执行等攻击面。

### 6.7 范围与依赖

- **不要碰 `xianxia`**：冻结存档，不接受改动。
- 不引入遥测、外呼或第三方统计；确有必要时须在 PR 中明确说明并取得同意。
- 新增第三方依赖请说明理由；面板前端依赖请同步更新 `package-lock.json`。

---

## 7. 报告安全问题

发现安全漏洞（凭据泄露、鉴权绕过、注入等）请**不要开公开 issue**。请通过
仓库的 **Security → Report a vulnerability**（GitHub 私密安全公告）提交，附上影响面与复现步骤。

---

## 8. 许可

本项目以 [MIT 许可](LICENSE) 发布。你提交的贡献将同样以 MIT 许可授权给本项目及所有使用者。
