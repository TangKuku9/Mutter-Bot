#!/bin/bash
# QQBot Linux 部署脚本
# 用法: ./install.sh [publish-dir]
# 默认 publish-dir: ./publish

set -e

PUBLISH_DIR="${1:-./publish}"
INSTALL_DIR="/opt/qqbot"
SERVICE_FILE="/etc/systemd/system/qqbot.service"

echo "========================================="
echo "  QQBot Linux 部署脚本"
echo "========================================="

# 检查 root 权限
if [ "$EUID" -ne 0 ]; then
    echo "错误: 请使用 root 或 sudo 执行此脚本"
    exit 1
fi

# 检查发布目录
if [ ! -f "$PUBLISH_DIR/QQBot" ]; then
    echo "错误: 未找到 $PUBLISH_DIR/QQBot 可执行文件"
    echo "请先运行: dotnet publish -c Release -r linux-x64 -o ./publish"
    exit 1
fi

echo "[1/6] 创建服务用户..."
if ! id -u qqbot &>/dev/null; then
    useradd -r -s /bin/false -d "$INSTALL_DIR" qqbot
    echo "  用户 qqbot 已创建"
else
    echo "  用户 qqbot 已存在"
fi

echo "[2/6] 创建安装目录..."
mkdir -p "$INSTALL_DIR/logs"
chown qqbot:qqbot "$INSTALL_DIR"

echo "[3/6] 复制文件..."
# 保护已有的运行数据库：若安装目录已有 qqbot.db，先备份，且不被 publish 中的旧库覆盖
if [ -f "$INSTALL_DIR/qqbot.db" ]; then
    BACKUP="$INSTALL_DIR/qqbot.db.bak.$(date +%Y%m%d%H%M%S)"
    cp "$INSTALL_DIR/qqbot.db" "$BACKUP"
    echo "  已备份现有数据库 -> $BACKUP"
    # 临时移走 publish 中的数据库，避免覆盖线上数据（首次安装无此文件则跳过）
    if [ -f "$PUBLISH_DIR/qqbot.db" ]; then
        mv "$PUBLISH_DIR/qqbot.db" "$PUBLISH_DIR/.qqbot.db.skip"
    fi
fi
cp -r "$PUBLISH_DIR"/* "$INSTALL_DIR/"
# 还原 publish 中被临时移走的数据库文件
if [ -f "$PUBLISH_DIR/.qqbot.db.skip" ]; then
    mv "$PUBLISH_DIR/.qqbot.db.skip" "$PUBLISH_DIR/qqbot.db"
fi
chown -R qqbot:qqbot "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/QQBot"

echo "[4/6] 安装 systemd 服务..."
cp qqbot.service "$SERVICE_FILE"
systemctl daemon-reload

echo "[5/6] 启用服务..."
systemctl enable qqbot

echo "[6/6] 启动服务..."
systemctl start qqbot

echo ""
echo "========================================="
echo "  部署完成!"
echo "========================================="
echo ""
echo "  Web 面板: http://$(hostname -I | awk '{print $1}'):5188"
echo "  TUI 模式: /opt/qqbot/QQBot (需要终端环境)"
echo ""
echo "  常用命令:"
echo "    sudo systemctl status qqbot"
echo "    sudo systemctl restart qqbot"
echo "    sudo journalctl -u qqbot -f"
echo ""
