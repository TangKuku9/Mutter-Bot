#!/bin/bash
# mutter-bot Linux 部署脚本
# 用法: ./install.sh [publish-dir]
# 默认 publish-dir: ./publish

set -e

PUBLISH_DIR="${1:-./publish}"
INSTALL_DIR="/opt/mutter-bot"
SERVICE_NAME="mutter-bot"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
DB_FILE="mutter-bot.db"

echo "========================================="
echo "  mutter-bot Linux 部署脚本"
echo "========================================="

# 检查 root 权限
if [ "$EUID" -ne 0 ]; then
    echo "错误: 请使用 root 或 sudo 执行此脚本"
    exit 1
fi

# 检查发布目录
if [ ! -f "$PUBLISH_DIR/mutter-bot" ]; then
    echo "错误: 未找到 $PUBLISH_DIR/mutter-bot 可执行文件"
    echo "请先运行: dotnet publish -c Release -r linux-x64 -o ./publish"
    exit 1
fi

echo "[1/6] 创建服务用户..."
if ! id -u "$SERVICE_NAME" &>/dev/null; then
    useradd -r -s /bin/false -d "$INSTALL_DIR" "$SERVICE_NAME"
    echo "  用户 $SERVICE_NAME 已创建"
else
    echo "  用户 $SERVICE_NAME 已存在"
fi

echo "[2/6] 创建安装目录..."
mkdir -p "$INSTALL_DIR/logs"
chown "$SERVICE_NAME:$SERVICE_NAME" "$INSTALL_DIR"

echo "[3/6] 复制文件..."
# 先停止运行中的服务：让 SQLite 干净关闭并对 WAL 做检查点，
# 避免未落盘的写丢失。
systemctl stop "$SERVICE_NAME" 2>/dev/null || true

# 保护已有的运行数据库及 WAL/SHM 辅助文件：
# 若安装目录已有 $DB_FILE，先备份，且不被 publish 中的旧库及其 WAL/SHM 覆盖。
# 注意：SQLite 在 WAL 模式下，最近未 checkpoint 的写存在于 $DB_FILE-wal；
#       用开发机的 -wal/-shm 覆盖线上文件会让这些写丢失
#       （表现为最近一批写入回退，而更早已 checkpoint 的数据仍在）。
if [ -f "$INSTALL_DIR/$DB_FILE" ]; then
    BACKUP="$INSTALL_DIR/$DB_FILE.bak.$(date +%Y%m%d%H%M%S)"
    cp "$INSTALL_DIR/$DB_FILE" "$BACKUP"
    if [ -f "$INSTALL_DIR/$DB_FILE-wal" ]; then cp "$INSTALL_DIR/$DB_FILE-wal" "$BACKUP-wal"; fi
    if [ -f "$INSTALL_DIR/$DB_FILE-shm" ]; then cp "$INSTALL_DIR/$DB_FILE-shm" "$BACKUP-shm"; fi
    echo "  已备份现有数据库 -> $BACKUP"
    # 临时移走 publish 中的数据库及 WAL/SHM，避免覆盖线上数据（首次安装无此文件则跳过）
    for f in "$DB_FILE" "$DB_FILE-wal" "$DB_FILE-shm"; do
        if [ -f "$PUBLISH_DIR/$f" ]; then
            mv "$PUBLISH_DIR/$f" "$PUBLISH_DIR/.$f.skip"
        fi
    done
fi
cp -r "$PUBLISH_DIR"/* "$INSTALL_DIR/"
# 还原 publish 中被临时移走的数据库及 WAL/SHM 文件
for f in "$DB_FILE" "$DB_FILE-wal" "$DB_FILE-shm"; do
    if [ -f "$PUBLISH_DIR/.$f.skip" ]; then
        mv "$PUBLISH_DIR/.$f.skip" "$PUBLISH_DIR/$f"
    fi
done
chown -R "$SERVICE_NAME:$SERVICE_NAME" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/mutter-bot"

echo "[4/6] 安装 systemd 服务..."
cp "$SERVICE_NAME.service" "$SERVICE_FILE"
systemctl daemon-reload

echo "[5/6] 启用服务..."
systemctl enable "$SERVICE_NAME"

echo "[6/6] 启动服务..."
systemctl start "$SERVICE_NAME"

echo ""
echo "========================================="
echo "  部署完成!"
echo "========================================="
echo ""
echo "  Web 面板: http://$(hostname -I | awk '{print $1}'):5188"
echo "  TUI 模式: $INSTALL_DIR/mutter-bot (需要终端环境)"
echo ""
echo "  常用命令:"
echo "    sudo systemctl status $SERVICE_NAME"
echo "    sudo systemctl restart $SERVICE_NAME"
echo "    sudo journalctl -u $SERVICE_NAME -f"
echo ""
