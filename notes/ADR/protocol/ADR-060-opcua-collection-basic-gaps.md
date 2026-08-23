# ADR-060 OPC UA 采集距「可用的水平」的差距清单

## 问题
OPC UA 接入（12-OPC-UA接入设计.md S1~S6）已跑通全链路：连接/读写/断链重连实测通过、单测 695 / 集成 45 全绿、批量生成 v2 完成。但仍是**初版**——能连进程内/匿名 Server，换成真实服务器（西门子 PLC / Prosys / 别的网关）或加密端点大概率连不上，且点位地址只能手填。

## 「可用的水平」验收线（不做不算可以）
① 能连上真实服务器（证书不被卡死）② 界面上能点选配点位（非手填 NodeId）③ 稳定采集上云 + 断链自愈 + 不产伪值（✅ 已达标）④ 测试兜底（✅ 已达标）。

## 差距与代码位置
| 缺口 | 代码位置 | 现状 |
|---|---|---|
| **Browse 节点树 + 前端点选** | `OpcUa/IBrowseableDriver.cs`（接口已定义 `BrowseAsync` 但驱动未实现）；`OpcUa/OpcUaDriver.cs`（`class OpcUaDriver` 未实现该接口）；前端 `web/src/views/Points/` 无浏览入口 | 地址只能手填 `ns=2;i=1001` |
| **证书互认落地** | `OpcUa/OpcUaDriver.cs` `ConnectAsync`（`CheckApplicationInstanceCertificates` 尽力而为，失败静默降级 None+匿名）；前端无证书处理/提示 UI | 连加密端点/真实 Server 会卡「证书不受信任」 |
| **质量细分映射** | `OpcUaDriver.ReadBatchAsync`（`StatusCode` 仅 Bad/Good 二元 + 跳过） | 无 Uncertain 细分，`StatusCode→QualityCode` 留待 v2 |

## 修复方向（优先级）
1. **Browse 节点树 + 前端点选**（核心）：`OpcUaDriver` 实现 `IBrowseableDriver.BrowseAsync`（SDK `Browse`/`BrowseNext`，根节点起递归取变量子节点）→ Webapi 暴露浏览 API → 前端 Points 页「从树选点位」填充 Address。
2. **证书互认落地**：`CheckApplicationInstanceCertificates` 失败时不再静默降级，返回明确错误 + 提供「导入/信任服务端证书」的可用流程（读回 rejected/可信目录、界面提示加信任），并把安全策略/凭据做成可配置（None/Basic256Sha256 + 用户名密码，界面可选）。
3. **质量细分映射**（顺手、几十行）：`StatusCode.IsUncertain` → QualityCode.Uncertain，Bad 仍跳过。
4. **明确砍掉订阅推送**：`SupportsSubscription=true` 仅是能力预留，采集引擎保持轮询，注释写死「v1 轮询够用，不做订阅」。

## 状态
待实施（用户拍板后按 1→2→3 顺序做；4 不实施）。
