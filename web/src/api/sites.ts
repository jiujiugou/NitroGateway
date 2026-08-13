import client from './client'
import type { ApiResponse } from './types'

// ADR-035 第 1 步 Web 维度：站点目录来自中心库 measurements ∪ alarms 的 site_id 去重

/** 站点详情（ADR-036 中心站点管理）：显示名、来源指纹与冲突标记 */
export interface SiteInfo {
  siteId: string
  displayName: string
  sourceClientId?: string | null
  lastSeenClientId?: string | null
  firstSeenAt?: string | null
  lastSeenAt?: string | null
  /** 冲突 = 同一 siteId 被不同 MQTT ClientId（机器）上报过 */
  hasConflict: boolean
}

export async function getSites(): Promise<string[]> {
  const { data } = await client.get<ApiResponse<string[]>>('/sites')
  return data.data ?? []
}

export async function getSiteInfos(): Promise<SiteInfo[]> {
  const { data } = await client.get<ApiResponse<SiteInfo[]>>('/sites/info')
  return data.data ?? []
}

export async function renameSite(siteId: string, displayName: string): Promise<void> {
  await client.put<ApiResponse<unknown>>('/sites/' + encodeURIComponent(siteId) + '/rename', { displayName })
}
