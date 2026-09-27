# 04 · TTL 缓存统一

- 类别：完全统一
- 优先级：中
- 状态：✅ 已完成

## 现状

| 位置 | 文件 | 做法 |
|---|---|---|
| 告警 | `src/NitroGateway.Alarm/Repository/AlarmRuleCache.cs` | `SemaphoreSlim(1,1)` + 双检 + TTL 30s + 失效 |
| 设备 | `src/NitroGateway.Device/DeviceSnapshotCache.cs` | `SemaphoreSlim(1,1)` 闸门 + 缓存 |

同类"带闸门的缓存 + 双检 + 失效"被写了两遍。

## 统一形态

`TtlCache<K,V>`：
- 机制：`GetOrLoadAsync(key, factory)` 原子加载一次（治 check-then-act）+ TTL 过期 + `Invalidate()`。
- 策略：TTL、失效触发（保存/删除时）。

## 差异是参数还是语义

TTL、失效触发是参数；无语义差异。

## 涉及改动文件（已完成）

- [x] 新增 `src/NitroGateway.Primitives/Caching/TtlCache.cs`（`TtlCache<TKey,TValue>`）
- [x] `AlarmRuleCache.cs` 改为 `TtlCache` 的领域包装（`GetOrLoadAsync` / `Invalidate` 语义不变，`CachedAlarmRuleRepository` 无需改动）
- [x] `DeviceSnapshotCache.cs` 改用它
- [x] `NitroGateway.Alarm` / `NitroGateway.Device` 增加 `Primitives` 引用
- [x] 新增 `TtlCacheTests`

## 验收 / 测试

- 现有 `AlarmRuleCache` / `DeviceSnapshotCache` 相关单测保持绿。
- 新增 `TtlCache` 单测：并发 `GetOrLoad` 只执行一次工厂（check-then-act 不变量）、TTL 过期、失效。

## 备注

与 [06 幂等存储](06-idempotency-store.md) 形态相近（原子加载一次），可共用"原子加载"原语。
