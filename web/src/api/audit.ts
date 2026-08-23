import client from './client'
import type { ApiResponse } from './types'

/// 一条操作审计记录（ADR-065 A3，对应后端 AuditLogDto）
export interface AuditLog {
  id: string
  user: string
  role: string
  method: string
  path: string
  statusCode: number
  elapsedMs: number
  ip: string
  createdAt: string
}

/// 审计分页结果（对应后端 AuditLogPageDto）
export interface AuditLogPage {
  items: AuditLog[]
  total: number
  page: number
  pageSize: number
}

/// 审计查询过滤条件（时间用 UTC ISO 串；其余透传后端）
export interface AuditLogQuery {
  from?: string
  to?: string
  user?: string
  method?: string
  path?: string
  status?: number
  page?: number
  pageSize?: number
}

/// 分页查询操作日志（时间倒序；仅 Admin/Operator）
export async function getAuditLogs(q: AuditLogQuery = {}): Promise<AuditLogPage> {
  const { data } = await client.get<ApiResponse<AuditLogPage>>('/auditlogs', { params: q })
  return data.data ?? { items: [], total: 0, page: 1, pageSize: 50 }
}
