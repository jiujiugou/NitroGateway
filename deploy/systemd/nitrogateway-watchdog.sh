#!/usr/bin/env bash
# NitroGateway 主机级看门狗
# 周期探测 /healthz，连续 FAIL_THRESHOLD 次失败后重启 gateway 容器。
# 依赖：宿主机有 curl（无则 apt install -y curl）。
# 参数经 systemd 的 EnvironmentFile(/etc/default/nitrogateway) 注入，此处给默认值兜底。
set -u

: "${GATEWAY_URL:=http://localhost:5100/healthz}"
: "${STACK_DIR:=/opt/nitrogateway}"
: "${CHECK_INTERVAL:=10}"
: "${FAIL_THRESHOLD:=3}"

fail=0
while true; do
  if curl -fsS --max-time 5 "$GATEWAY_URL" >/dev/null 2>&1; then
    fail=0
  else
    fail=$((fail + 1))
    echo "[$(date '+%F %T')] healthz unreachable ($fail/$FAIL_THRESHOLD)"
    if [ "$fail" -ge "$FAIL_THRESHOLD" ]; then
      echo "[$(date '+%F %T')] healthz failed ${FAIL_THRESHOLD}x, restarting gateway container"
      (cd "$STACK_DIR" && docker compose restart gateway) || true
      fail=0
    fi
  fi
  sleep "$CHECK_INTERVAL"
done
