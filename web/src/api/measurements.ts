import client from './client'
import type { ApiResponse, PointSnapshot } from './types'

export async function getHistory(deviceId: string, pointId: string, from: string, to: string): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/history', { params: { deviceId, pointId, from, to } })
  return data.data ?? []
}

export async function getLatestBatch(deviceId: string): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/latest-batch', { params: { deviceId } })
  return data.data ?? []
}
