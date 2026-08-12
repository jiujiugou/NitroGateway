import client from './client'
import type { ApiResponse } from './types'

// ADR-035 第 1 步 Web 维度：站点目录来自中心库 measurements ∪ alarms 的 site_id 去重
export async function getSites(): Promise<string[]> {
  const { data } = await client.get<ApiResponse<string[]>>('/sites')
  return data.data ?? []
}
