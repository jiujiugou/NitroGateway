#!/bin/bash
# NitroGateway 冒烟测试 — 启动→采集→查数据
# 前置: Modbus模拟器开 :502, MQTT Broker开 :1883
set -e

BASE=http://localhost:5100/api
PASS=0; FAIL=0
pass() { echo "  ✅ $1"; PASS=$((PASS+1)); }
fail() { echo "  ❌ $1"; FAIL=$((FAIL+1)); }

echo "══════════════ NitroGateway 冒烟测试 ═══════════════"
echo ""

# 1. 健康检查
echo "--- 1. 健康检查 ---"
HTTP=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5100/healthz)
[ "$HTTP" = "200" ] && pass "healthz" || fail "healthz ($HTTP)"
HTTP=$(curl -s -o /dev/null -w "%{http_code}" http://localhost:5100/readyz)
[ "$HTTP" = "200" ] && pass "readyz" || fail "readyz ($HTTP)"

# 2. 登录
echo "--- 2. 登录 ---"
RESP=$(curl -sX POST $BASE/auth/login -H "Content-Type: application/json" -d '{"username":"admin","password":"admin123"}')
TOKEN=$(echo $RESP | grep -o '"token":"[^"]*"' | cut -d'"' -f4)
[ -n "$TOKEN" ] && pass "登录 admin" || fail "登录 admin"

# 3. 注册设备
echo "--- 3. 设备注册 ---"
RESP=$(curl -sX POST $BASE/devices -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"PLC-TEST01","protocol":{"name":"Modbus","dialect":"TCP"},"connection":{"endpoint":"127.0.0.1:502"}}')
DID=$(echo $RESP | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
[ -n "$DID" ] && pass "注册设备 $DID" || fail "注册设备"

# 4. 添加点位
echo "--- 4. 点位 ---"
RESP=$(curl -sX POST $BASE/devices/$DID/points -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Temp","address":"40001","dataType":"Float"}')
PID=$(echo $RESP | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
[ -n "$PID" ] && pass "添加点位 $PID" || fail "添加点位"

# 5. 等采集 → 查数据
echo "--- 5. 采集链路（等5秒） ---"
sleep 5
RESP=$(curl -s "$BASE/measurements/history?deviceId=$DID&pointId=$PID&from=2020-01-01&to=2030-01-01" -H "Authorization: Bearer $TOKEN")
COUNT=$(echo $RESP | grep -o '"value"' | wc -l)
[ "$COUNT" -gt 0 ] && pass "查到 $COUNT 条数据" || fail "查数据"

# 6. 系统状态
echo "--- 6. 系统状态 ---"
curl -s $BASE/status/system -H "Authorization: Bearer $TOKEN" | grep -q "mqttState" && pass "系统状态" || fail "系统状态"

# 7. 清理
echo "--- 7. 清理 ---"
curl -sX DELETE $BASE/devices/$DID -H "Authorization: Bearer $TOKEN" > /dev/null && pass "清理设备" || fail "清理设备"

echo ""
echo "══════════════ 结果: $PASS 通过 / $FAIL 失败 ═══════════════"
[ "$FAIL" -eq 0 ] && echo "✅ 冒烟测试全部通过" || echo "❌ 有 $FAIL 项失败"
exit $FAIL
