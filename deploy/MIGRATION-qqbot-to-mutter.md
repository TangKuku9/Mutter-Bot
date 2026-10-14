# 迁移：QQBot-Linux（qqbot） → Mutter（mutter-bot）

本次更名是**破坏性变更**：配置节名、数据库文件名、systemd 服务名与安装路径全部改变。
已有的旧部署不会自动升级，需要按本文迁移。

全新的机器请直接用 `deploy/install.sh`，不需要本文。

## 变更清单

| 项目 | 旧 | 新 |
| --- | --- | --- |
| systemd 服务 | `qqbot.service` | `mutter-bot.service` |
| 安装目录 | `/opt/qqbot` | `/opt/mutter-bot` |
| 服务用户/组 | `qqbot` | `mutter-bot` |
| 可执行文件 | `QQBot` | `mutter-bot` |
| 数据库 | `qqbot.db`（含 `-wal` / `-shm`） | `mutter-bot.db`（含 `-wal` / `-shm`） |
| 配置根节 | `"QQBot": { ... }` | `"Mutter": { ... }` |
| 面板会话 Cookie | `qqbot_panel_session` | `mutter_panel_session` |

配置节名不改的话，程序读不到 `AppId` / `ClientSecret`，表现为启动后连不上平台。

## 迁移步骤

> 下面的命令假设你的发布目录是 `./publish`、旧服务确实叫 `qqbot`。
> 每一步执行前先看清路径，尤其是带 `rm` 的那一步。

### 1. 停旧服务

```bash
sudo systemctl stop qqbot
sudo systemctl disable qqbot
```

**必须先停服务**：SQLite 在干净关闭时会对 WAL 做 checkpoint，否则最近的写入还躺在
`qqbot.db-wal` 里，直接搬 `.db` 会丢掉它们。

### 2. 迁移目录与数据库

```bash
# 整个目录改名即可，顺带保留 logs/
sudo mv /opt/qqbot /opt/mutter-bot

# 数据库连同 WAL/SHM 一起改名（有的部署没有 -wal/-shm，用 -f 跳过）
cd /opt/mutter-bot
sudo mv qqbot.db     mutter-bot.db
sudo mv -f qqbot.db-wal mutter-bot.db-wal 2>/dev/null || true
sudo mv -f qqbot.db-shm mutter-bot.db-shm 2>/dev/null || true
```

### 3. 改配置节名

```bash
sudo sed -i 's/^\(  \)"QQBot":/\1"Mutter":/' /opt/mutter-bot/appsettings.json
grep -n '"Mutter"' /opt/mutter-bot/appsettings.json   # 确认已改
```

### 4. 建新用户/组并修正属主

```bash
sudo groupadd -r mutter-bot 2>/dev/null || true
sudo useradd -r -s /bin/false -d /opt/mutter-bot -g mutter-bot mutter-bot 2>/dev/null || true
sudo chown -R mutter-bot:mutter-bot /opt/mutter-bot
```

旧的 `qqbot` 用户确认无其他用途后可删除：`sudo userdel qqbot`

### 5. 部署新版本并装服务

```bash
dotnet publish -c Release -r linux-x64 -o ./publish
```

由于 `install.sh` 会保护已有的 `mutter-bot.db`（先备份、且不被 publish 中的旧库覆盖），
可以直接跑：

```bash
sudo ./deploy/install.sh ./publish
```

若想手工操作，等价于：

```bash
sudo cp deploy/mutter-bot.service /etc/systemd/system/
sudo rm -f /etc/systemd/system/qqbot.service
sudo systemctl daemon-reload
sudo systemctl enable --now mutter-bot
```

### 6. 验证

```bash
systemctl status mutter-bot
journalctl -u mutter-bot -f
```

面板地址不变（默认 `http://<ip>:5188`）。因为会话 Cookie 名变了，**已登录的浏览器需要重新登录**，
这是预期行为。

## 回滚

迁移前请确保旧目录或数据库有备份（`install.sh` 也会自动生成
`mutter-bot.db.bak.<时间戳>`）。回滚即把上面步骤反做一遍：目录改回 `/opt/qqbot`、
数据库改回 `qqbot.db`、配置节改回 `"QQBot"`、重新启用 `qqbot.service`。
