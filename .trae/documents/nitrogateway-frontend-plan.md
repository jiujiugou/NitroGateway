# NitroGateway 前端开发计划

## 项目概述

NitroGateway 是一个基于 .NET 10 的**工业协议采集网关**，通过 Modbus/OPC UA/S7 协议采集现场设备数据，经过缩放转换和死区过滤后，通过 MQTT 转发到云端，同时本地 SQLite 持久化。

目前项目仅有后端核心（采集引擎、设备管理、数据转发等），**前端和 WebAPI 层完全空白**。

---

## 总体架构

本计划分为两大阶段：

```
[浏览器] ←→ [Vue 3 前端] ←→ [NitroGateway.Webapi] ←→ [NitroGateway 后端服务]
                         ↓ (WebSocket)
                    [实时数据推送]
```

| 层 | 项目 | 说明 |
|---|---|---|
| **前端 (Admin)** | `src/NitroGateway.Admin/` 下的独立前端项目 | Vue 3 管理面板 |
| **API 层 (Webapi)** | `src/NitroGateway.Webapi/` | ASP.NET Core REST API + WebSocket |
| **后端核心** | 现有代码 (Host/Collection/Device/Forwarder等) | 保持不变 |

---

## 技术选型

### 前端栈

| 类别 | 选择 | 原因 |
|---|---|---|
| 框架 | **Vue 3** + Composition API | 上手成本低，生态成熟，与 .NET 配合好 |
| 构建工具 | **Vite** | 极快的 HMR 和构建速度 |
| 语言 | **TypeScript** | 类型安全，与后端 C# 模型对照清晰 |
| UI 组件库 | **Element Plus** | 工业管理后台常用，组件丰富，中文友好 |
| 图表库 | **ECharts** | 时序数据可视化首选，支持大量数据点和时间轴 |
| HTTP 客户端 | **Axios** | 标准 HTTP 请求库 |
| 实时通信 | **SignalR 客户端** (`@microsoft/signalr`) | 与 ASP.NET Core SignalR 原生集成 |
| 路由 | **Vue Router** | Vue 官方路由 |
| 状态管理 | **Pinia** | Vue 3 官方状态管理 |
| 表格/数据处理 | 手写组合函数 + Element Plus Table | 轻量灵活 |

### API 层技术选型

| 类别 | 选择 |
|---|---|
| 框架 | **ASP.NET Core** (使用现有 .NET 10 项目结构) |
| WebAPI | 标准 RESTful Controller |
| 实时推送 | **SignalR** Hub |
| 认证 | 开发阶段暂不启用（内网工业环境） |

---

## 阶段一：NitroGateway.Webapi（REST API 层）

需要新建 `src/NitroGateway.Webapi/` 项目，对外暴露 HTTP 接口，供前端调用。

### 1.1 项目结构

```
NitroGateway.Webapi/
├── NitroGateway.Webapi.csproj
├── Program.cs                         # WebApplication 构建入口
├── appsettings.json                   # 配置文件
├── Controllers/
│   ├── DevicesController.cs           # 设备 CRUD
│   ├── PointsController.cs           # 点位 CRUD
│   ├── MeasurementsController.cs     # 时序数据查询
│   ├── StatusController.cs           # 系统/设备状态
│   └── ForwarderController.cs        # 转发状态监控
├── Hubs/
│   └── LiveDataHub.cs                # SignalR Hub（实时推送）
├── Models/
│   ├── ApiResponse.cs                # 统一响应格式
│   └── Dtos/                         # 数据传输对象
├── Services/
│   ├── IDeviceService.cs
│   └── DeviceService.cs
└── Extensions/
    └── ServiceCollectionExtensions.cs
```

### 1.2 API 端点设计

| 方法 | 路径 | 说明 |
|---|---|---|
| **设备管理** |||
| GET | `/api/devices` | 获取所有设备列表 |
| GET | `/api/devices/{id}` | 获取单个设备详情（含点位） |
| POST | `/api/devices` | 创建设备 |
| PUT | `/api/devices/{id}` | 更新设备 |
| DELETE | `/api/devices/{id}` | 删除设备 |
| PUT | `/api/devices/{id}/status` | 更新设备状态（上线/维护等） |
| **点位管理** |||
| GET | `/api/devices/{deviceId}/points` | 获取设备的点位列表 |
| POST | `/api/devices/{deviceId}/points` | 添加点位 |
| PUT | `/api/devices/{deviceId}/points/{pointId}` | 更新点位 |
| DELETE | `/api/devices/{deviceId}/points/{pointId}` | 删除点位 |
| **时序数据** |||
| GET | `/api/devices/{deviceId}/points/{pointId}/history` | 查询历史数据（支持 from/to 参数） |
| GET | `/api/devices/{deviceId}/points/latest` | 获取设备所有点位的最新值 |
| **系统状态** |||
| GET | `/api/status/devices` | 所有设备在线/离线状态概览 |
| GET | `/api/status/system` | 系统运行状态（采集频率、转发队列积压等） |
| **实时推送** |||
| WebSocket | `/hubs/live` | SignalR Hub（实时推送最新采集数据） |

### 1.3 API 统一响应格式

```typescript
// 所有接口统一返回格式
interface ApiResponse<T> {
  success: boolean;
  data?: T;
  error?: {
    code: string;
    message: string;
  };
  timestamp: string;  // ISO 8601
}
```

### 1.4 实现步骤

1. **创建 Webapi 项目** — ASP.NET Core Empty 项目，目标 `net10.0`
2. **注册依赖注入** — 引用现有 `NitroGateway.Storage.Configuration` 和 `NitroGateway.Storage.TimeSeries` 接口
3. **实现 DevicesController** — 设备 CRUD（通过 `IDeviceRepository` 操作）
4. **实现 PointsController** — 点位 CRUD（通过 `IPointRepository` 操作）
5. **实现 MeasurementsController** — 历史数据查询（通过 `IMeasurementStore.QueryAsync`）
6. **实现 StatusController** — 系统状态监控（从各模块获取状态）
7. **实现 LiveDataHub** — SignalR Hub，实时推送采集数据
8. **统一错误处理中间件**
9. **CORS 配置** — 允许开发环境前端跨域访问

---

## 阶段二：NitroGateway.Admin（Vue 3 前端）

### 2.1 项目结构

```
NitroGateway.Admin/
├── index.html
├── package.json
├── vite.config.ts
├── tsconfig.json
├── src/
│   ├── main.ts                       # 入口
│   ├── App.vue                       # 根组件
│   ├── api/                          # API 调用层
│   │   ├── client.ts                 # Axios 实例 + 拦截器
│   │   ├── devices.ts                # 设备 API
│   │   ├── points.ts                 # 点位 API
│   │   ├── measurements.ts           # 时序数据 API
│   │   ├── status.ts                 # 状态 API
│   │   └── types.ts                  # TypeScript 类型定义
│   ├── composables/                  # 组合式逻辑
│   │   ├── useLiveData.ts            # SignalR 实时数据
│   │   └── useRefreshTimer.ts        # 定时刷新
│   ├── router/
│   │   └── index.ts                  # 路由配置
│   ├── stores/
│   │   ├── deviceStore.ts            # 设备状态 Pinia store
│   │   └── liveDataStore.ts          # 实时数据 Pinia store
│   ├── views/                        # 页面
│   │   ├── Dashboard/
│   │   │   └── DashboardView.vue     # 总览仪表盘
│   │   ├── Devices/
│   │   │   ├── DeviceListView.vue    # 设备列表
│   │   │   ├── DeviceDetailView.vue  # 设备详情
│   │   │   └── DeviceForm.vue        # 设备编辑表单
│   │   ├── Points/
│   │   │   ├── PointList.vue         # 点位列表
│   │   │   └── PointForm.vue         # 点位编辑表单
│   │   ├── Monitoring/
│   │   │   └── MonitoringView.vue    # 实时监控
│   │   └── History/
│   │       └── HistoryView.vue       # 历史数据查询
│   ├── components/                   # 通用组件
│   │   ├── DeviceStatusTag.vue       # 设备状态标签
│   │   ├── ValueGauge.vue            # 仪表盘仪表组件
│   │   ├── TrendChart.vue            # 趋势图
│   │   ├── NavSidebar.vue            # 侧边导航栏
│   │   └── TopHeader.vue             # 顶部导航栏
│   └── styles/
│       └── global.css                # 全局样式
```

### 2.2 页面功能说明

#### 2.2.1 仪表盘（Dashboard）
- 设备总览：在线/离线/故障设备数量卡片
- 最近采集数据实时滚动
- 系统运行时间/采集总数统计
- 快速跳转到各功能模块

#### 2.2.2 设备管理（Devices）
- **设备列表**：表格展示所有设备，支持搜索和状态筛选
- **设备详情**：查看设备完整信息，包括关联点位列表
- **设备表单**：添加/编辑设备（名称、协议类型、连接参数等）
- **状态控制**：手动切换设备状态按钮

#### 2.2.3 点位管理（Points）
- **点位列表**：以设备为维度，展示其所有采集点位
- **点位表单**：添加/编辑点位（名称、地址、数据类型、缩放系数、死区等）
- **批量操作**：批量启用/禁用点位

#### 2.2.4 实时监控（Monitoring）
- **实时数据面板**：选中设备后，实时展示各点位的最新值
- **仪表盘视图**：关键点位用仪表组件（Gauge）展示
- **SignalR 连接状态**：显示 WebSocket 连接状态

#### 2.2.5 历史数据（History）
- **时间范围选择器**：选择查询时间段
- **折线趋势图**：点位值随时间变化的曲线（ECharts）
- **数据表格**：同时展示原始历史数据表格
- **数据导出**：CSV 导出功能

### 2.3 TypeScript 类型定义

```typescript
// api/types.ts — 与后端 Domain 模型对齐

interface Device {
  id: string;           // Guid
  name: string;
  description?: string;
  protocol: ProtocolIdentifier;
  connection: DeviceConnection;
  status: DeviceStatus;
  points: DevicePoint[];
}

interface ProtocolIdentifier {
  name: string;         // "Modbus" | "OPC UA" | "S7"
  dialect?: string;     // "TCP" | "RTU"
}

interface DeviceConnection {
  endpoint: string;
  connectTimeoutMs: number;
  requestTimeoutMs: number;
  retryCount: number;
  retryIntervalMs: number;
  parameters: Record<string, unknown>;
}

type DeviceStatus = 'Unknown' | 'Online' | 'Offline' | 'Error' | 'Maintenance';

interface DevicePoint {
  id: string;           // Guid
  name: string;
  address: string;
  description?: string;
  dataType: DataType;
  enabled: boolean;
  access: PointAccess;
  scanIntervalMs: number;
  deadband: number;
  scaleFactor: number;
  scaleOffset: number;
}

type DataType = 'Bool' | 'Byte' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32'
              | 'Int64' | 'UInt64' | 'Float' | 'Double' | 'String';

type PointAccess = 'ReadOnly' | 'WriteOnly' | 'ReadWrite';

interface PointSnapshot {
  deviceId: string;
  devicePointId: string;
  rawValue?: unknown;
  value?: number;
  timestamp: string;    // ISO 8601
  quality: 'Good' | 'Uncertain' | 'Bad';
  errorMessage?: string;
}

interface DeviceStatusSummary {
  deviceId: string;
  deviceName: string;
  status: DeviceStatus;
  lastOnlineTime?: string;
  lastError?: string;
}

interface SystemStatus {
  uptime: string;
  totalCollections: number;
  totalForwarded: number;
  bufferBacklog: number;
  mqttConnected: boolean;
}
```

### 2.4 路由设计

```
/                     → Dashboard（仪表盘重定向）
/dashboard            → DashboardView（总览）
/devices              → DeviceListView（设备列表）
/devices/new          → DeviceForm（新建设备）
/devices/:id          → DeviceDetailView（设备详情，含点位管理）
/devices/:id/edit     → DeviceForm（编辑设备）
/devices/:id/points   → PointList（点位列表）
/monitoring           → MonitoringView（实时监控）
/monitoring/:deviceId → MonitoringView（指定设备实时监控）
/history              → HistoryView（历史数据查询）
/history/:deviceId    → HistoryView（指定设备历史数据）
```

### 2.5 实时数据流设计

```
后端 CollectionEngine → 采集 PointSnapshot
        ↓
   DataDispatcher → IMeasurementStore（SQLite 落盘）
        ↓
   转发 Buffer  ←── SignalR Hub（PublishAsync 推送给前端）
        ↓
   Forwarder → MQTT 发布
```

实时数据推送采用 **SignalR Hub**：

```csharp
// Webapi LiveDataHub.cs
public class LiveDataHub : Hub
{
    // 客户端可订阅指定设备的数据
    public async Task SubscribeDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    }

    public async Task UnsubscribeDevice(string deviceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, deviceId);
    }
}
```

后端采集完成后通过 Hub 推送：

```csharp
// 在 CollectionEngine 采集完成后调用
await _hubContext.Clients.Group(deviceId.ToString())
    .SendAsync("OnDataReceived", snapshot);
```

---

## 实施步骤（按执行顺序）

### 第 1 步：创建 NitroGateway.Webapi 项目
- 新建 ASP.NET Core Web API 项目
- 配置项目引用（Domain, Storage.*）
- 配置 Program.cs（CORS 允许前端地址）
- 实现统一响应模型 `ApiResponse<T>`

### 第 2 步：实现设备管理 API
- DevicesController（CRUD）
- 通过 `IDeviceRepository` 操作数据库

### 第 3 步：实现点位管理 API
- PointsController（CRUD）
- 通过 `IPointRepository` 操作数据库

### 第 4 步：实现时序数据查询 API
- MeasurementsController（历史数据查询）
- 通过 `IMeasurementStore.QueryAsync` 查询

### 第 5 步：实现系统状态 API
- StatusController（设备状态概览、系统运行状态）

### 第 6 步：实现 SignalR Hub 实时推送
- LiveDataHub
- 在 CollectionEngine 中注入 HubContext 推送实时数据

### 第 7 步：初始化前端项目
- Vite + Vue 3 + TypeScript + Element Plus
- 包管理：pnpm
- 配置 Axios 实例和 API 层
- 配置 Vue Router

### 第 8 步：实现导航框架
- NavSidebar 侧边栏
- TopHeader 顶部栏
- 路由配置

### 第 9 步：实现仪表盘页面
- 状态统计卡片
- 设备在线概览
- 最近数据滚动

### 第 10 步：实现设备管理页面
- 设备列表
- 设备详情
- 设备表单

### 第 11 步：实现点位管理页面
- 点位列表
- 点位表单

### 第 12 步：实现实时监控页面
- 实时数据面板
- 仪表盘组件（Gauge）
- SignalR 连接管理

### 第 13 步：实现历史数据查询页面
- 时间选择器
- ECharts 趋势图
- 数据导出

### 第 14 步：集成测试与联调
- 启动 Host 后端 + Webapi + 前端
- 端到端验证数据流程

---

## 关键设计决策

### 1. 为什么不直接在 Host 中嵌入 Webapi？
Host 是控制台应用，职责是采集和转发。Webapi 作为独立进程（或可选的子进程）运行，职责分离更清晰，也便于独立扩展。

### 2. 实时数据：SignalR vs WebSocket vs SSE
选择 **SignalR**，因为：
- ASP.NET Core 原生支持，集成成本最低
- 自动处理重连、分组、协议协商
- 支持 .NET 客户端和 JS 客户端

### 3. 数据流转方式
实时数据**不经过 MQTT**，直接从后端内存通过 SignalR 推送到前端，减少延迟。MQTT 路径保留用于云端转发，前端不依赖 MQTT。

### 4. 前端架构风格
采用 **页面/组件/API/Store 分层**，不使用重度框架（如 Nuxt），保持灵活轻量。业务逻辑集中在组合函数（Composables）中。

---

## 风险与注意事项

| 风险 | 缓解措施 |
|---|---|
| Webapi 作为新进程需额外维护 | 初期在 Host 项目中通过 `UseWebApi()` 扩展方法内嵌启动 |
| 前端实时数据量过大 | SignalR 按设备分组订阅，前端限频渲染 |
| 时序数据历史查询性能 | 查询加时间范围限制，分页返回 |
| 开发环境跨域问题 | 正确配置 CORS，仅允许开发地址 |
