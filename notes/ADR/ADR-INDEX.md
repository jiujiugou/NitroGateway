# ADR Index

> 只保留架构决策。Code Review / 调试记录已归档删除，变更历史见 git。

---

## Quick Reference（必读 5 篇）

| ADR | 一句话 | 为什么必读 |
|-----|--------|-----------|
| 025 | Ingest独立容器幂等入库主键冲突 | 整体架构定义，所有代码的基础 |
| 054 | web纯边缘不再复合中心双身份 | 纠正了035的双模式错误 |
| 053 | 变化抑制加心跳三处共用瘦身 | 310GB→1GB/月，数据管道核心 |
| 004 | 密码哈希校验加登录限流兜底 | 安全红线 |
| 035 | 桌面边缘采集Web中心管理唯一写点 | siteId/MQTT topic布局/DeploymentMode |

---

## 全部 ADR（19 篇）

| # | 模块 | 一句话摘要 | 状态 |
|---|------|-----------|------|
| 004 | security | 密码哈希校验加登录限流兜底 | ✅ |
| 006 | forwarder | ClientId唯一加重连重放订阅 | ✅ |
| 025 | architecture | Ingest独立容器幂等入库主键冲突 | ✅ |
| 026 | architecture | 宿主复用模块注册现场无内嵌Web | ✅ |
| 033 | architecture | 中心为准手动导入到自动下发 | ✅ |
| 035 | architecture | 桌面边缘采集Web中心管理唯一写点 | ✅ |
| 044 | architecture | Center模式裁剪边缘能力测试连桌面 | ✅ |
| 053 | performance | 变化抑制加心跳三处共用瘦身 | ✅ |
| 054 | architecture | web纯边缘不再复合中心双身份 | ✅ |
| 055 | architecture | 实时曲线加CSV导入补齐web缺口 | ✅ |
| 069 | architecture | 订阅commands校验写值幂等回执 | ✅ |
| 070 | protocol | 单层Browse加前端懒加载点选 | ✅ |
| 071 | architecture | 订阅接入现有Pipeline失败回退轮询 | ✅ |
| 072 | architecture | KeepAlive接入复用SDK重连保订阅 | ✅ |
| 073 | architecture | 显式安全档位加凭据加密落库 | ✅ |
| 074 | protocol | 驱动单闸门所有权装饰器不再持锁 | ✅ |
| 075 | forwarder | MQTT 重试退避外包 Polly 删自写退避 | ✅ |
| 076 | architecture | 通用机制层Primitives机制与策略分离 | ✅ |
| 077 | protocol | 驱动池裸借用不做排水释放不等在途 | ✅ |
| 078 | protocol | 驱动双接口释放同步不排水异步优雅拆除 | ✅ |
