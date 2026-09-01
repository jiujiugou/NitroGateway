# ADR-036: 桌面端 RTU 串口配置缺口 + siteId 缺省唯一性

- 日期: 2026-08-12 | 状态: 已实施
- 来源: 用户问题排查（从站可见性 / RTU 读取方式 / siteId 设计）

## Context

桌面端 RTU 串口配置不完整（DataBits/StopBits 未保存回填、Parity 缺 Mark/Space、从站 UnitId 无列）；siteId 缺省唯一性未定义——多现场上行若 siteId 缺省冲突会导致中心数据混淆。

## Decision

- D1 桌面 RTU 配置补齐：DeviceEditor/DeviceEditorWindow 补 DataBits/StopBits 保存回填与 UI、Parity 补 Mark/Space；DevicesView 增从站 UnitId 列。
- D2 siteId 唯一性 = 随机生成（site- + 10 位 base32 随机，加密随机源，40 位熵）+ 中心 site_id 唯一索引兜底；siteId 保持唯一代号，可读名走 display_name。
- D3 siteId 生成与解析：桌面首启自动生成并持久化 %LocalAppData%\NitroGateway\site.json；解析顺序 配置/环境变量(Site:Id) > 本地存储 > 自动生成；GatewayHost 启动写回配置，采集/转发/告警/同步统一取用；设置页可展示/编辑/重新生成 + 格式校验。
- D4 siteId 进 MQTT topic 第三层与 URL，格式排除 / + # 空格；"default" 为未初始化哨兵禁止使用。
- D5 中心兜底：M012 迁移 sites 表（site_id 唯一索引）；Ingest 首见注册站点（upsert，保留 source_client_id 首见指纹 + last_seen_client_id 更新）；Web 站点列表 = sites ∪ measurements ∪ alarms 去重（兼容旧库）；中心 Web 站点管理（GET /api/sites/info、PUT /api/sites/{siteId}/rename、前端站点管理页，侧边栏 /sites）。

## Alternatives

- 用设备名/自增 ID 作 siteId：可读但跨现场可能冲突，无唯一性保证。
- 站点直接存 display_name 作主键：可读名可变，无法承载唯一性。

## Rationale

siteId 是跨现场唯一代号，随机生成 + 唯一索引兜底可保证万级现场碰撞可忽略；display_name 承载可读性，二者职责分离；中心站点表兼容旧库去重，避免首见指纹丢失。

## Consequences

- 桌面 RTU 配置完整可用；siteId 自动生成、多现场唯一、可读名走 display_name。
- 中心站点注册/去重/改名闭环；冲突（同 siteId 不同 ClientId 上报）可标记。
