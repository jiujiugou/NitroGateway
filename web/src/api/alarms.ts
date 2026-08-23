import client from './client'
import type { ApiResponse, AlarmRule } from './types'

/// 告警汇总（ADR-065 A1 仪表盘 KPI）：活跃数 / 今日发生数
export interface AlarmSummary {
  active: number
  today: number
}

/// 告警记录（对应后端 AlarmDto）
export interface Alarm {
  id: string
  ruleId: string
  deviceId: string
  pointId: string
  triggerValue: number
  threshold: number
  severity: string
  message: string
  state: string
  occurredAt: string
  resolvedAt?: string
  acknowledgedAt?: string
}

/// 仪表盘 KPI：活跃告警数 + 今日发生数（后端 AlarmsController.Summary）
export async function getAlarmSummary(): Promise<AlarmSummary> {
  const { data } = await client.get<ApiResponse<AlarmSummary>>('/alarms/summary')
  return data.data ?? { active: 0, today: 0 }
}

/// 当前活跃告警列表（仪表盘告警汇总区）
export async function getActiveAlarms(): Promise<Alarm[]> {
  const { data } = await client.get<ApiResponse<Alarm[]>>('/alarms')
  return data.data ?? []
}

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
