import client from './client'
import type { ApiResponse, DeviceStatusSummary } from './types'

export async function getDeviceSummary(): Promise<DeviceStatusSummary[]> {
  const { data } = await client.get<ApiResponse<DeviceStatusSummary[]>>('/status/devices')
  return data.data ?? []
}

export async function getSystemStatus(): Promise<{ bufferBacklog: number; mqttState: string; mqttConnected: boolean }> {
  const { data } = await client.get<ApiResponse<{ bufferBacklog: number; mqttState: string }>>('/status/system')
  const d = data.data ?? { bufferBacklog: 0, mqttState: 'Disconnected' }
  return { bufferBacklog: d.bufferBacklog, mqttState: d.mqttState, mqttConnected: d.mqttState === 'Connected' }
}
