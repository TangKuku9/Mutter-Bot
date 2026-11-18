#!/bin/bash
# mutter-bot Linux 部署脚本
# 用法: ./install.sh [publish-dir]
# 默认 publish-dir: ./publish

set -e
umask 077

PUBLISH_DIR="${1:-./publish}"
INSTALL_DIR="/opt/mutter-bot"
SERVICE_NAME="mutter-bot"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
DB_FILE="mutter-bot.db"

# 脚本与其模板同目录：从仓库根、发布包根或任意目录调用都能定位 unit，
# 不再依赖调用者的当前目录。
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_TEMPLATE="$SCRIPT_DIR/${SERVICE_NAME}.service"

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

# 在任何破坏性操作（停服、覆盖文件）之前校验服务模板存在，
# 避免半升级状态：服务已停止却没有新 unit 可装。
if [ ! -f "$SERVICE_TEMPLATE" ]; then
    echo "错误: 未找到服务模板 $SERVICE_TEMPLATE"
    echo "请在包含 ${SERVICE_NAME}.service 的目录中执行本脚本（deploy/ 或发布包根目录）"
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
fi
# 备份安装目录已有的全部配置（环境覆盖文件会覆盖 appsettings.json，一并保留）。
for installed_config in "$INSTALL_DIR"/appsettings*.json; do
    [ -f "$installed_config" ] || continue
    base="$(basename "$installed_config")"
    CONFIG_BACKUP="$INSTALL_DIR/$base.bak.$(date +%Y%m%d%H%M%S)"
    cp "$installed_config" "$CONFIG_BACKUP"
    echo "  已备份现有配置 $base -> $CONFIG_BACKUP"
done

# 无论安装目录是否有旧库，publish 中的数据库及 WAL/SHM 一律不进安装目录：
# 开发机残库会覆盖线上数据，首次安装也不该带上开发数据。
PUBLISH_SKIP_FILES=()
for f in "$DB_FILE" "$DB_FILE-wal" "$DB_FILE-shm" update-settings.json outbound-settings.json; do
    if [ -f "$PUBLISH_DIR/$f" ]; then
        mv "$PUBLISH_DIR/$f" "$PUBLISH_DIR/.$f.skip"
        PUBLISH_SKIP_FILES+=(".$f.skip")
    fi
done

# 保护 appsettings*.json：与面板更新器同口径。已有安装的任何环境覆盖
# （appsettings.json / appsettings.Production.json / appsettings.Development.json 等）
# 都先备份并保留；publish 中的环境覆盖文件（尤其源码发布自带的本地
# appsettings.Production.json）一律不进安装目录，否则会在 ASPNETCORE_ENVIRONMENT=Production
# 下覆盖 appsettings.json 的面板口令与机器人凭据。
shopt -s nullglob
CONFIG_SKIP_FILES=()
for publish_config in "$PUBLISH_DIR"/appsettings*.json; do
    base="$(basename "$publish_config")"
    if [ "$base" = "appsettings.json" ] && [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
        continue  # 首次安装：允许占位模板铺出
    fi
    mv "$publish_config" "$PUBLISH_DIR/.$base.skip"
    CONFIG_SKIP_FILES+=(".$base.skip")
    if [ "$base" != "appsettings.json" ]; then
        echo "  警告: 发布目录包含 $base，已排除（环境覆盖文件不应部署到服务器）"
    fi
done
shopt -u nullglob

# 还原 publish 中被临时移走的文件（数据库/环境覆盖配置/用户设置），发布目录保持原样。
# 用 trap 保证中途失败（磁盘满、cp 失败）也会还原；正常结束时先显式还原再撤销 trap。
restore_skipped() {
    local skip_name="$1"
    local restored_name="${skip_name#.}"
    restored_name="${restored_name%.skip}"
    [ -f "$PUBLISH_DIR/$skip_name" ] || return 0
    mv "$PUBLISH_DIR/$skip_name" "$PUBLISH_DIR/$restored_name"
}
restore_all_skipped() {
    local skip_name
    for skip_name in "${PUBLISH_SKIP_FILES[@]}" "${CONFIG_SKIP_FILES[@]}"; do
        restore_skipped "$skip_name"
    done
}
trap restore_all_skipped EXIT
for publish_item in "$PUBLISH_DIR"/*; do
    [ "$(basename "$publish_item")" = "data" ] && continue
    cp -r "$publish_item" "$INSTALL_DIR/"
done
restore_all_skipped
trap - EXIT
chown -R "$SERVICE_NAME:$SERVICE_NAME" "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR"
# 配置、聊天数据库及旧备份都可能含凭据或群聊内容，只允许服务账户读取。
find "$INSTALL_DIR" -maxdepth 1 -type f \( -name 'appsettings*.json*' -o -name 'mutter-bot.db*' -o -name '*-settings.json*' \) -exec chmod 600 {} +
chmod -R go-rwx "$INSTALL_DIR/logs"
chmod +x "$INSTALL_DIR/mutter-bot"

echo "[4/6] 安装 systemd 服务..."
# 覆盖前备份管理员可能自定义过的 unit（端口、环境变量等）
if [ -f "$SERVICE_FILE" ]; then
    cp -a "$SERVICE_FILE" "$SERVICE_FILE.bak.$(date +%Y%m%d%H%M%S)"
fi
install -m 0644 "$SERVICE_TEMPLATE" "$SERVICE_FILE"
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
echo "  本机管理入口: http://127.0.0.1:5188"
echo "  首次设置请使用本机浏览器或 SSH 端口转发；公网访问必须配置 HTTPS 反向代理。"
echo "  首次初始化码: $INSTALL_DIR/logs/panel-bootstrap.token（30 分钟有效，改密成功后失效）"
echo "  公网部署说明: docs/public-panel-security.md；Nginx 示例: nginx-mutter.conf.example"
echo "  TUI 模式: $INSTALL_DIR/mutter-bot (需要终端环境)"
echo ""
echo "  常用命令:"
echo "    sudo systemctl status $SERVICE_NAME"
echo "    sudo systemctl restart $SERVICE_NAME"
echo "    sudo journalctl -u $SERVICE_NAME -f"
echo ""
