export interface Device {
  id: string
  name: string
  /// 设备站点归属（后端 DTO 保留字段）。ADR-054：web 收敛为纯边缘单一站点，前端不再维护站点归属，恒为空/默认
  siteId?: string
  description?: string
  protocol: ProtocolIdentifier
  connection: DeviceConnection
  status: DeviceStatus
  points: DevicePoint[]
}

export interface ProtocolIdentifier {
  name: string
  dialect?: string
}

export interface DeviceConnection {
  endpoint: string
  connectTimeoutMs: number
  requestTimeoutMs: number
  retryCount: number
  retryIntervalMs: number
  parameters: Record<string, unknown>
}

export type DeviceStatus = 'Unknown' | 'Online' | 'Offline' | 'Error' | 'Maintenance'

export interface DevicePoint {
  id: string
  name: string
  address: string
  description?: string
  dataType: DataType
  enabled: boolean
  access: PointAccess
  scanIntervalMs: number
  deadband: number
  scaleFactor: number
  scaleOffset: number
  /// 写值范围下限（null = 不限，docs/14 写功能）；仅数值点位有意义
  minLimit?: number | null
  /// 写值范围上限（null = 不限，docs/14 写功能）
  maxLimit?: number | null
}

export type DataType = 'Bool' | 'Byte' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32' | 'Int64' | 'UInt64' | 'Float' | 'Double' | 'String'
export type PointAccess = 'ReadOnly' | 'WriteOnly' | 'ReadWrite'

/// ADR-070 层次1：OPC UA 节点浏览结果（前端树点选回填点位）
export interface BrowseNode {
  nodeId: string
  name: string
  typeName: string
  isVariable: boolean
  access: string
}

export interface PointSnapshot {
  deviceId: string
  devicePointId: string
  rawValue?: unknown
  value?: number
  timestamp: string
  quality: 'Good' | 'Uncertain' | 'Bad'
  errorMessage?: string
}

export interface ApiResponse<T> {
  success: boolean
  data?: T
  error?: { code: string; message: string }
  timestamp: string
}

export interface MeasurementQuery {
  deviceId: string
  pointId: string
  from: string
  to: string
}

export interface AlarmRule {
  id: string
  deviceId: string
  pointId: string
  operator: string
  threshold: number
  thresholdUpper?: number
  durationSeconds: number
  severity: string
  messageTemplate?: string
  enabled: boolean
}

