# ADR-044: 中心形态 Web 功能裁剪 + 测试连接迁移桌面端（A 阶段）

- 日期: 2026-08-14 | 状态: 已实施（A 阶段） | 来源: ADR-035 角色拆分落地——中心 Web 仍暴露采集侧功能；B 阶段（中心写=意图+下发+回执）前置准备
- 范围: Webapi 部署形态暴露 + center 模式边缘能力裁剪；Vue 按 mode 门控；桌面端 DeviceEditor 增加测试连接；不动数据模型

## 问题
- center 形态（`Deployment:Mode=Center`）下 Web 仍保留采集侧 UI/API：test-connection / serial-ports / 死信 / 转发/MQTT/熔断/串口状态——中心到不了现场 PLC，这些页面要么恒空要么调用失败，误导使用者
- `StatusController` 注入 `IForwardBuffer/IMqttClient/ForwardingThrottle/ICircuitBreakerRegistry`，这些仅在 `AddNitroForwarder/AddNitroMqtt/AddNitroCollection` 注册，而 Program.cs `isCenter` 已跳过三者 → **center 模式 /api/status/* DI 解析失败直接 500**
- 前端无部署形态感知，路由守卫只查 token、无任何能力边界；且只存 token 不存角色（角色感知属另一条线，不在本 ADR）

## 决策（A 阶段）
1. **后端暴露部署形态**：注册 `DeploymentMode` 单例；新增 `GET /api/status/info` 返回 `{ mode: Gateway|Center }`，供前端启动时取一次（B 阶段中心意图下发同样依赖该 mode 语义）
2. **center 模式 /api/status/system 兼容**：StatusController 采集侧依赖改可空注入（`IForwardBuffer?/IMqttClient?/ForwardingThrottle?/ICircuitBreakerRegistry?`），center 模式返回 `mode` + 在线设备数，采集侧字段为空/0；Gateway 模式行为不变
3. **edge-only 端点 center 显式拒绝**：`DevicesController.test-connection / serial-ports / serial-port-status` 在 center 模式返回 400「中心无现场通路，请在桌面端测试」（不返回空/500）
4. **前端 mode 感知基础设施**：`web/src/deployment.ts` 响应式 mode + `initDeployment()`；App.vue 隐藏死信菜单、顶栏 MQTT/缓冲段；SystemStatus 隐藏 MQTT/节流/熔断/串口段；DeviceForm 隐藏测试连接按钮与串口下拉可用端口；路由 `meta.edgeOnly` 守卫 center 下跳回 /dashboard
5. **测试连接迁移桌面端**：桌面 `DeviceEditorWindow` 增加「测试连接」按钮，新增 `IDeviceConnectionTester`（复用 `IProtocolDriverFactory` + `ISerialPortManager`，语义对齐 Web 的 Connect + Ping，ADR-023 防假阳性）；桌面本就是现场采集端，物理通路只在边缘

## 为 B 阶段预留
- mode 感知与「边缘能力 / 中心能力」边界是 B（中心写=意图+配置下发+回执）的地基：B 复用同一 `deployment.ts` 与后端 `DeploymentMode`
- 测试连接/串口永远只在边缘侧；中心 DeviceForm 保留配置编辑（ADR-033 中心裁决权），但不再物理探活——B 中中心写操作改为「意图落库 + 触发下发 + 回执」，不新增直连探活
- `GET /api/status/info` 结构稳定，B 可在此追加中心侧信息（下发队列、回执状态）

## 验收
- `dotnet build` 0 错 + 全量单测通过；center 模式 /api/status/system 不再 500；test-connection center 返回 400
- 前端 center 下：无死信菜单、SystemStatus 无采集侧段、DeviceForm 无测试连接/串口下拉；Gateway 下原样
- 桌面 DeviceEditor 可发起测试连接并展示结果（Connect+Ping）

## 影响
- 行为变更（G1，已与用户确认做 A）：center 模式 status/test-connection 行为变化；前端 UI 按 mode 裁剪；桌面端新增测试连接
- 只改 UI/行为适配，不涉及迁移、数据模型、接口契约删除；`Storage/`、`Protocol/Abstraction/` 纯接口不动
