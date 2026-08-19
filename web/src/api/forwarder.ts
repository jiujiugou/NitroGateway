import client from './client'
import type { ApiResponse } from './types'

// ADR-059：MQTT 上云转发总开关——运行期启停 mqtt 通道上云转发，无需改配置重启。
// 关闭语义：采集/本地 SQLite/告警/web/SignalR 不受影响，仅跳过 mqtt 通道入转发缓冲
// （无缓冲堆积、不触发死信）；恢复后从关闭时刻起续传，不补发关闭期数据。

/// 查询当前 MQTT 上云转发是否启用（缺省视为启用）
export async function getForwarderEnabled(): Promise<boolean> {
  const { data } = await client.get<ApiResponse<{ enabled: boolean }>>('/forwarder/enabled')
  return data.data?.enabled ?? true
}

/// 设置 MQTT 上云转发开关（即时生效并持久化，重启保持）
export async function setForwarderEnabled(enabled: boolean): Promise<boolean> {
  const { data } = await client.put<ApiResponse<{ enabled: boolean }>>('/forwarder/enabled', { enabled })
  return data.data?.enabled ?? enabled
}
