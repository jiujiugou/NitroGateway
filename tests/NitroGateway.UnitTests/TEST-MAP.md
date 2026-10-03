# UnitTests 测试地图

> 自动生成，用于观察测试组织。模块为主、原型为辅（并发用 `*ConcurrencyTests` 命名标识，属性测试在 `PropertyBased/`）。

## 总览

| 目录 | 文件 | Fact | Theory | Property | InlineData | 断言 | 并发 |
|---|---:|---:|---:|---:|---:|---:|---:|
| <root> | 7 | 0 | 0 | 0 | 0 | 0 | 0 |
| Alarms | 3 | 29 | 0 | 0 | 0 | 61 | 0 |
| Collection | 15 | 91 | 1 | 0 | 3 | 223 | 0 |
| Command | 3 | 20 | 0 | 0 | 0 | 51 | 0 |
| Desktop | 11 | 55 | 0 | 0 | 0 | 128 | 0 |
| Desktop\Sync | 5 | 31 | 1 | 0 | 12 | 149 | 0 |
| Desktop\ViewModels | 16 | 199 | 0 | 0 | 0 | 705 | 0 |
| Devices | 5 | 60 | 0 | 0 | 0 | 151 | 0 |
| Forwarding | 2 | 8 | 1 | 0 | 2 | 25 | 0 |
| Host | 1 | 3 | 0 | 0 | 0 | 6 | 0 |
| Persistence | 12 | 96 | 1 | 0 | 4 | 291 | 0 |
| Primitives | 2 | 14 | 0 | 0 | 0 | 31 | 0 |
| PropertyBased | 10 | 0 | 0 | 32 | 0 | 14 | 0 |
| Protocol | 7 | 30 | 2 | 0 | 9 | 70 | 3 |
| Protocol\Modbus | 3 | 17 | 1 | 0 | 3 | 33 | 0 |
| Protocol\OpcUa | 6 | 64 | 2 | 0 | 7 | 125 | 0 |
| Protocol\S7 | 2 | 9 | 7 | 0 | 65 | 28 | 0 |
| Security | 4 | 31 | 0 | 0 | 0 | 44 | 0 |
| Shared | 2 | 18 | 0 | 0 | 0 | 26 | 0 |
| Telemetry | 2 | 20 | 0 | 0 | 0 | 49 | 0 |
| Transport | 1 | 3 | 0 | 0 | 0 | 11 | 0 |
| Webapi | 6 | 53 | 0 | 0 | 0 | 164 | 0 |
| **合计** | **125** | **851** | **16** | **32** | **105** | **2385** | **3** |

## 明细

### <root>  (7)
- ActivityListenerCollection.cs
- CommandTestFakes.cs
- ConfigSyncTestFakes.cs
- DesktopViewModelTestHelpers.cs
- DeviceConfigTestHelpers.cs
- FakeMqttClient.cs
- SharedFakes.cs

### Alarms  (3)
- AlarmEvaluatorTests.cs
- CachedAlarmRuleRepositoryTests.cs
- ThresholdEvaluatorTests.cs

### Collection  (15)
- ChangeDetectorTests.cs
- ChannelDrainTests.cs
- CircuitBreakerHealthListenerTests.cs
- CircuitBreakerTests.cs
- CollectionEngineShutdownTests.cs
- CollectionOptionsWiringTests.cs
- DataDispatcherTests.cs
- DeviceCollectorMaintenanceTests.cs
- DeviceCollectorProbeTests.cs
- DeviceCollectorScanIntervalTests.cs
- DeviceReaderTests.cs
- HealthReporterTests.cs
- MeasurementWriteHostTests.cs
- PointValuePipelineTests.cs
- SubscriptionCoordinatorTests.cs

### Command  (3)
- CommandHostedServiceTests.cs
- CommandProcessorTests.cs
- CommandRequestParserTests.cs

### Desktop  (11)
- DesktopPathConfigTests.cs
- DesktopSettingsStoreTests.cs
- DesktopShellRegistrationTests.cs
- DesktopThemeTests.cs
- DesktopViewSmokeTests.cs
- DeviceConnectionTesterTests.cs
- EventBridgeTests.cs
- MqttConnectionTesterTests.cs
- MqttDesktopConfigTests.cs
- OpcUaNodeBrowserTests.cs
- UiDispatcherTests.cs

### Desktop\Sync  (5)
- CenterConfigClientTests.cs
- CenterConfigImporterTests.cs
- CenterSyncSettingsStoreTests.cs
- SiteConfigSyncServiceTests.cs
- SiteIdProviderTests.cs

### Desktop\ViewModels  (16)
- AlarmRuleEditorTests.cs
- AlarmRulesViewModelTests.cs
- AlarmsViewModelTests.cs
- DashboardViewModelTests.cs
- DeviceEditorTests.cs
- DevicesViewModelFactoryTests.cs
- DevicesViewModelTests.cs
- HistoryViewModelTests.cs
- MainViewModelTests.cs
- OpcUaPointEditorViewModelTests.cs
- PointBatchEditorTests.cs
- PointEditorTests.cs
- PointsViewModelTests.cs
- RealtimeViewModelTests.cs
- SettingsViewModelTests.cs
- StartupViewModelTests.cs

### Devices  (5)
- DeviceHealthMonitorTests.cs
- DeviceManagerTests.cs
- PointBatchServiceTests.cs
- PointManagerTests.cs
- WriteServiceTests.cs

### Forwarding  (2)
- ForwarderActivityTests.cs
- ForwarderRegistrationTests.cs

### Host  (1)
- GatewayLifecycleTests.cs

### Persistence  (12)
- CredentialPersistenceTests.cs
- DiskGuardTests.cs
- MeasurementRetentionServiceTests.cs
- MigrationRunnerTests.cs
- SqliteAlarmRepositoryTests.cs
- SqliteAuditLogStoreTests.cs
- SqliteDeviceRepositoryTests.cs
- SqliteErrorClassifierTests.cs
- SqliteForwardMqttToggleTests.cs
- SqliteForwardOutboxTests.cs
- SqliteMeasurementStoreTests.cs
- SqliteUserStoreTests.cs

### Primitives  (2)
- ResiliencePipelineFactoryTests.cs
- TtlCacheTests.cs

### PropertyBased  (10)
- AddressParserPropertyTests.cs
- AlarmEvaluatorPropertyTests.cs
- BatchMeasurementsPropertyTests.cs
- ChangeDetectorPropertyTests.cs
- CommandProcessorPropertyTests.cs
- ModbusAddressPropertyTests.cs
- PipelinePropertyTests.cs
- PointBatchServicePropertyTests.cs
- ThresholdEvaluatorPropertyTests.cs
- WriteServicePropertyTests.cs

### Protocol  (7)
- EndpointParserTests.cs
- ProtocolDriverFactoryTests.cs
- ProtocolDriverGateConcurrencyTests.cs
- ProtocolDriverPoolConcurrencyTests.cs
- ProtocolDriverPoolTests.cs
- ReliableProtocolDriverConcurrencyTests.cs
- ReliableProtocolDriverTests.cs

### Protocol\Modbus  (3)
- ModbusAddressParserTests.cs
- ModbusBatchPlannerTests.cs
- ModbusDriverBaseTests.cs

### Protocol\OpcUa  (6)
- OpcUaAddressParserTests.cs
- OpcUaClientConfigurationFactoryTests.cs
- OpcUaDriverKeepAliveTests.cs
- OpcUaDriverSecurityTests.cs
- OpcUaDriverTests.cs
- OpcUaParametersValidationTests.cs

### Protocol\S7  (2)
- S7AddressParserTests.cs
- S7DriverTests.cs

### Security  (4)
- LoginRateLimiterTests.cs
- SecurityConfigValidationTests.cs
- TokenGeneratorTests.cs
- WriteGuardTests.cs

### Shared  (2)
- DataTypeExtensionsTests.cs
- OperationalErrorTests.cs

### Telemetry  (2)
- TelemetryServiceCollectionExtensionsTests.cs
- TelemetryTracingOptionsTests.cs

### Transport  (1)
- MqttReconnectPolicyTests.cs

### Webapi  (6)
- CertificateTrustListTests.cs
- DeviceStatusDispatcherTests.cs
- OpcUaBrowseControllerTests.cs
- WebapiAuthorizationTests.cs
- WebapiControllerTests.cs
- WebSiteIdProviderTests.cs
