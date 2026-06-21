export interface Device {
  id: string
  name: string
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
}

export type DataType = 'Bool' | 'Byte' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32' | 'Int64' | 'UInt64' | 'Float' | 'Double' | 'String'
export type PointAccess = 'ReadOnly' | 'WriteOnly' | 'ReadWrite'

export interface PointSnapshot {
  deviceId: string
  devicePointId: string
  rawValue?: unknown
  value?: number
  timestamp: string
  quality: 'Good' | 'Uncertain' | 'Bad'
  errorMessage?: string
}

export interface DeviceStatusSummary {
  deviceId: string
  deviceName: string
  status: DeviceStatus
  lastError?: string
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
