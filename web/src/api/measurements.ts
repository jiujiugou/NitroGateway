import client from './client'
import type { ApiResponse, PointSnapshot } from './types'

export async function getHistory(deviceId: string, pointId: string, from: string, to: string, siteId?: string, limit = 1000): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/history', { params: { deviceId, pointId, from, to, siteId, limit } })
  return data.data ?? []
}

export async function getLatestBatch(deviceId: string, siteId?: string): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/latest-batch', { params: { deviceId, siteId } })
  return data.data ?? []
}



