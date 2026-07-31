import client from './client'
import type { ApiResponse, AlarmRule } from './types'

export async function getAlarmRules(): Promise<AlarmRule[]> {
  const { data } = await client.get<ApiResponse<AlarmRule[]>>('/alarmrules')
  return data.data ?? []
}

export async function createAlarmRule(r: Partial<AlarmRule>): Promise<AlarmRule | null> {
  const { data } = await client.post<ApiResponse<AlarmRule>>('/alarmrules', r)
  return data.data ?? null
}

export async function updateAlarmRule(id: string, r: Partial<AlarmRule>): Promise<AlarmRule | null> {
  const { data } = await client.put<ApiResponse<AlarmRule>>(`/alarmrules/${id}`, r)
  return data.data ?? null
}

export async function deleteAlarmRule(id: string): Promise<boolean> {
  const { data } = await client.delete<ApiResponse<unknown>>(`/alarmrules/${id}`)
  return data.success
}
