import client from './client'
import type { ApiResponse } from './types'

export async function getSystemStatus(): Promise<{ bufferBacklog: number; mqttState: string; mqttConnected: boolean }> {
  const { data } = await client.get<ApiResponse<{ bufferBacklog: number; mqttState: string }>>('/status/system')
  const d = data.data ?? { bufferBacklog: 0, mqttState: 'Disconnected' }
  return { bufferBacklog: d.bufferBacklog, mqttState: d.mqttState, mqttConnected: d.mqttState === 'Connected' }
}
