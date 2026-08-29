#!/usr/bin/env bash
# 安装 NitroGateway systemd 服务 + 看门狗
# 用法：sudo bash install.sh [STACK_DIR]   （默认 /opt/nitrogateway）
# STACK_DIR = 放置 docker-compose.yml 的目录（即你的 Linux 部署目录）
set -euo pipefail

STACK_DIR="${1:-/opt/nitrogateway}"
SYSTEMD_DIR="/etc/systemd/system"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then
  echo "请用 sudo 运行：sudo bash install.sh $STACK_DIR" >&2
  exit 1
fi

# 1. 看门狗脚本
install -m 0755 "$SCRIPT_DIR/nitrogateway-watchdog.sh" /usr/local/bin/nitrogateway-watchdog.sh

# 2. 环境配置（栈目录、健康地址、阈值）
cat > /etc/default/nitrogateway <<EOF
# NitroGateway watchdog 配置（install.sh 生成）
GATEWAY_URL=http://localhost:5100/healthz
STACK_DIR=$STACK_DIR
CHECK_INTERVAL=10
FAIL_THRESHOLD=3
EOF

# 3. unit 文件
install -m 0644 "$SCRIPT_DIR/nitrogateway.service"          "$SYSTEMD_DIR/nitrogateway.service"
install -m 0644 "$SCRIPT_DIR/nitrogateway-watchdog.service" "$SYSTEMD_DIR/nitrogateway-watchdog.service"

# 4. 启动（enable = 开机自启）
systemctl daemon-reload
systemctl enable --now nitrogateway nitrogateway-watchdog
systemctl --no-pager status nitrogateway nitrogateway-watchdog --lines=0

echo
echo "完成。常用命令："
echo "  systemctl status nitrogateway               # 看栈状态"
echo "  systemctl restart nitrogateway              # 拉起/重启整栈"
echo "  journalctl -u nitrogateway-watchdog -f      # 看门狗日志"
echo "  docker compose -f $STACK_DIR/docker-compose.yml ps   # 容器状态"
