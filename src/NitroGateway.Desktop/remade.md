二、目录职责地图
App.xaml(.cs)                      进程入口：单实例/全局异常/宿主启停编排
DesktopServiceCollectionExtensions 桌面壳 DI 注册（EventBridge + 各 ViewModel + 对话框/同步服务）
Hosting/
  GatewayHost.cs                   宿主封装：Create→迁移→Start；Stop→drain→Dispose
  DesktopPathConfig / MqttDesktopConfig  路径与 MQTT 配置默认值
Messaging/EventBridge.cs           服务事件 → 200ms 合并帧 → UI（D2 核心）
ViewModels/                        每页一个 VM（CommunityToolkit.Mvvm）+ 编辑器 VM
Views/                             每页一个 View（XAML）+ 各弹窗
Services/
  Infrastructure/                  UiDispatcher、UiTimer、工厂
  Settings/                        本地设置存储、DPAPI、转发开关
  Connectivity/                    设备/MQTT「测试连接」
  Dialogs/                         文件/设备/告警规则对话框
  Sync/                            中心配置同步（ADR-033）
Themes/Styles.xaml                 设计令牌与控件样式
appsettings.json                   现场侧配置（SQLite/日志路径、MQTT broker）