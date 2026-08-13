# ADR-036: 桌面端 RTU 串口配置缺口 + siteId 缺省唯一性
- 日期: 2026-08-12 | 状态: 已修复（RTU 配置/从站列、siteId 生成与桌面展示、中心站点管理 UI）| 来源: 用户问题排查（从站可见性 / RTU 读取方式 / siteId 设计）

## 已修复（2026-08-12，单测 521 全绿）
- 桌面 RTU：DeviceEditor/DeviceEditorWindow 补 DataBits/StopBits 保存回填与 UI，Parity 补 Mark/Space；DevicesView 增从站 UnitId 列。
- siteId 生成（本 ADR 主题）：桌面首启自动生成唯一 siteId（site- + 10 位 base32 随机，加密随机源），持久化 %LocalAppData%\NitroGateway\site.json；解析顺序 配置/环境变量(Site:Id) > 本地存储 > 自动生成；GatewayHost 启动写回配置，采集/转发/告警/同步统一取用；设置页展示/编辑/重新生成 + 格式校验（小写字母数字开头、可含连字符、≤32 位、禁止 default）。
- 中心兜底：M012 迁移 sites 表（site_id 唯一索引）；Ingest 首见注册站点（upsert，保留 source_client_id 首见指纹 + last_seen_client_id 更新）；Web 站点列表 = sites ∪ measurements ∪ alarms 去重（兼容旧库）。
- 中心 Web 站点管理（本 ADR 收尾）：GET /api/sites/info 返回站点详情（显示名/来源指纹/首见最近时间/冲突标记）；PUT /api/sites/{siteId}/rename 改名或绑定 display_name（未注册站点一并建档，upsert 保留首见时间）；前端站点管理页（站点 ID/显示名可编辑保存/来源 ClientId/多来源冲突 tag/首见与最近时间），侧边栏入口 /sites。冲突 = 同一 siteId 被不同 MQTT ClientId（机器）上报。

## 关键设计
- 唯一性 = 随机生成（40 位熵，万级现场碰撞可忽略）+ 中心 site_id 唯一索引兜底；siteId 保持唯一代号，可读名走 display_name。
- siteId 进 MQTT topic 第三层与 URL，格式排除 / + # 空格；"default" 为未初始化哨兵禁止使用。
