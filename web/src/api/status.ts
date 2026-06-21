import client from './client'
import type { ApiResponse, DeviceStatusSummary } from './types'

export async function getDeviceSummary(): Promise<DeviceStatusSummary[]> {
  const { data } = await client.get<ApiResponse<DeviceStatusSummary[]>>('/status/devices')
  return data.data ?? []
}

export async function getSystemStatus(): Promise<{ bufferBacklog: number; mqttConnected: boolean }> {
  const { data } = await client.get<ApiResponse<{ bufferBacklog: number; mqttConnected: boolean }>>('/status/system')
  return data.data ?? { bufferBacklog: 0, mqttConnected: false }
}
