#!/usr/bin/env bash
# NitroGateway 数据备份脚本
# 用法: ./tools/backup.sh [卷名前缀]   默认前缀: nitrogateway_
# 输出: ./backups/nitrogateway-<时间戳>.db (SQLite 一致性备份, 保留最近 30 份)
set -euo pipefail

PREFIX="${1:-nitrogateway_}"
VOLUME="${PREFIX}gateway-data"
STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_DIR="$(cd "$(dirname "$0")/.." && pwd)/backups"
mkdir -p "$BACKUP_DIR"

echo "备份卷: $VOLUME"
echo "输出: $BACKUP_DIR/nitrogateway-$STAMP.db"

docker run --rm \
  -v "${VOLUME}:/data:ro" \
  -v "${BACKUP_DIR}:/backup" \
  alpine:3 sh -c \
  "apk add --no-cache sqlite >/dev/null 2>&1 && sqlite3 /data/nitrogateway.db \".backup '/backup/nitrogateway-${STAMP}.db'\"" \
  && echo "备份完成: $BACKUP_DIR/nitrogateway-$STAMP.db"

ls -1t "$BACKUP_DIR"/nitrogateway-*.db 2>/dev/null | tail -n +31 | xargs -r rm -f
echo "保留最近 30 份, 清理完成"