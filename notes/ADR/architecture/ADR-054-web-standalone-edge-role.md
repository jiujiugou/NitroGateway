# ADR-054: web 定位调整——收敛为纯边缘单一身份（Linux 网关管理端），拆掉 Center 复合

- 日期: 2026-08-18 | 状态: 已拍板（web 单独化），代码未动待实施 | 关联: ADR-035（Web=平台定位修正）、ADR-025（桌面+中心 B 方案）、ADR-044（模式裁剪）、ADR-053（死区/心跳）

## 一句话结论

**web 不再"既做边缘网关又做中心库"——同一二进制双身份（`Deployment:Mode`）是"乱"的根源；web 收敛为纯边缘（Linux 网关管理端，Gateway 单一形态），桌面端 = Windows 边缘，两者是同一套引擎的两个自包含壳；中心（如需多现场）另立独立项目，不复用 webapi 双模式。**

## 背景（讨论来源）

- 用户连续几轮反思：web 不应既当边缘网关又当中心库——同一 dll 两种人格导致条件分支泛滥
  （`if (!isCenter)` 注册裁剪 + 前端 mode 机制 + StatusController 可空注入），观感"乱、玩具繁多但用处不大"。
- 用户结论：**web 一套、桌面一套**，各自独立完整；Windows = WPF 桌面，Linux = web；不需要两个同时启动。

## 拍板结论（2026-08-18 用户确认）

- **web = 纯边缘**（Linux 网关管理端，Gateway 单一形态）；桌面端 = 纯边缘（Windows WPF，`GatewayHost`）。
- 两个边缘形态共享同一套引擎（Collection / Forwarder / Alarm / Mqtt / Device / Persistence 类库），各为自包含组合根。
- 移除 webapi 的 Center 复合；中心（如需）另立独立项目（中心 webapi + Ingest），不复用 webapi 双模式。

## 清理清单（web 单身份化，实施时逐项确认）

- `Program.cs` / `DeploymentModeParser`：删 `Deployment:Mode` 与 `if (!isCenter)`，无条件注册全套边缘模块。
- 前端 `deployment.ts` / `App.vue` / `SystemStatus.vue` / `router edgeOnly`：删 mode 机制与 `mode !== 'Center'` 条件，永远按边缘显示。
- `Monitoring` / `History` 的 `SiteFilter`（站点过滤）：删（纯边缘只有一个站点，过滤无意义）。
- `Sites` 页面：收敛为本站点 ID（可并入系统状态）。
- `Ingest` + center.db + sites 表 + `docker-compose.center.yml`：**归档暂不删**（待定中心是否需要；要则独立建项目）。
- `StatusController` 可空注入：恢复非空（不再有 Center 模式）。

## 关键边界（不动）

- 桌面端（NitroGateway.Desktop）零改动。
- 共享引擎模块（Collection / Forwarder / Alarm / Mqtt / Device / Persistence）零改动。
- `Storage/`、`Protocol/Abstraction/` 纯接口只增不删。

## 与 ADR-035 的关系

- ADR-035（2026-08-12 用户确认）定位：桌面 = 边缘，Web = 平台管理（中心），中心库唯一写点 = Ingest。
- **ADR-054 修正该定位**：web 改为纯边缘（Linux 形态），不再承担平台管理角色；ADR-035 第 3 步"边缘 Agent 独立进程、Webapi 瘦身"暂缓——
  若未来要中心，按第 3 步思路**独立建中心项目**（而非把 Center 模式塞进边缘 webapi）。

## 待定项

- 是否需要多现场中心？（是 → 独立项目中心-webapi + Ingest；否 → 归档的 ingest/center 代码可删。）
- 实施顺序：先拆复合，再补 ADR-055 缺口。
