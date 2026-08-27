# ADR-067 桌面 MQTT Broker 地址/端口可编辑 + 测试连接

## 问题
桌面端 MQTT 连接参数只能改 `appsettings.json`（localhost:1883 默认）后重启生效，
现场改 broker 地址/端口/凭证需要动配置文件，且没有任何连通性预检——配错了转发静默失败。

## 决策
1. 设置页新增「MQTT 连接设置」可编辑区（Host/端口/TLS/用户名/密码）+「测试连接」「保存」按钮。
2. 持久化到 `%LocalAppData%\NitroGateway\desktop-settings.json`（`DesktopSettings` 扩展
   `MqttHost/MqttPort/MqttUseTls/MqttUsername/MqttPasswordEncrypted/MqttPasswordConfigured`），
   密码 DPAPI（CurrentUser）落盘（复用 ADR-037 S5 的 `DpapiProtector`）。
3. 生效时机：保存后**重启生效**——`GatewayHost.Create` 在 `AddNitroMqtt` 前经
   `MqttDesktopConfig.Apply` 写回配置；优先级 **环境变量(MQTT__*) ＞ 持久化设置 ＞ appsettings 默认**
   （只在环境变量未提供时才写回，避免 ConfigurationManager 索引器 Set 覆盖更高优先级来源）。
4. 测试连接：新增 `IMqttConnectionTester`/`MqttConnectionTester`（`Services/Connectivity/`），
   **独立临时 `MqttClientWrapper` 实例**（无状态监听者、无转发开关、`MaxReconnectAttempts=0`），
   绝不碰 DI 单例（不干扰运行中转发连接）。语义：Connect 成功 + 发布一条测试消息
   （topic `nitrogateway/{siteId}/connection-test`，QoS1，ADR-020 P3-6 无订阅者按成功），
   防「TCP 通但写不进」假阳性（对齐 ADR-023）。整体 8s 超时兜底。

## 代码位置
- `Services/Settings/DesktopSettingsStore.cs`（字段 + DPAPI 落盘/解密）
- `Services/Connectivity/IMqttConnectionTester.cs`、`MqttConnectionTester.cs`
- `ViewModels/SettingsViewModel.cs`（`MqttHost/MqttPortText/...`、`TestMqttCommand`、`SaveMqttSettingsCommand`）
- `Views/SettingsView.xaml`(+.cs)（可编辑区 + PasswordBox 遮蔽）
- `DesktopServiceCollectionExtensions.cs`（注册 tester）
- `Hosting/MqttDesktopConfig.cs`、`Hosting/GatewayHost.cs`（启动配置覆盖）

## 取舍
- 端口用字符串属性 `MqttPortText` 绑定 + 校验时 `int.TryParse`：避免 WPF int 双向绑定对
  非法输入静默保留旧值、用户以为保存成功。
- 密码用 `MqttPasswordConfigured` 区分「未配置」与「明确空密码（匿名）」。
- 不实时热切换 broker（MQTT 客户端启动时绑定配置），UI 明确提示「重启后生效」——与
  LogDirectory/SiteId 模式一致，避免运行期漂移（ADR-006 P3-5）。

## 状态
已实施（2026-08-24），配套单测：tester 5、ViewModel 9、store 3、启动覆盖 4。
