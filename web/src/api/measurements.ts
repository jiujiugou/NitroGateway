import client from './client'
import type { ApiResponse, PointSnapshot } from './types'

// ADR-054：web 收敛为纯边缘（Linux 网关管理端），单一站点，历史/最新查询不再传 siteId
export async function getHistory(deviceId: string, pointId: string, from: string, to: string, limit = 1000): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/history', { params: { deviceId, pointId, from, to, limit } })
  return data.data ?? []
}

export async function getLatestBatch(deviceId: string): Promise<PointSnapshot[]> {
  const { data } = await client.get<ApiResponse<PointSnapshot[]>>('/measurements/latest-batch', { params: { deviceId } })
  return data.data ?? []
}



