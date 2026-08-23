import client from './client'
import type { ApiResponse } from './types'

/// 用户（对应后端 UserDto，ADR-066；刻意不含密码哈希/明文）
export interface User {
  id: number
  username: string
  role: string
  isEnabled: boolean
  createdAt: string
  updatedAt: string
  lastLoginAt?: string | null
}

/// 当前登录用户（缓存到 localStorage 的「谁登录了」，供菜单门控/顶部栏显示）
export interface CurrentUser {
  id: number
  username: string
  role: string
}

const ME_KEY = 'nitro.user'

/// 读取缓存的当前用户；无缓存返回 null（未登录或旧会话）
export function loadMe(): CurrentUser | null {
  const raw = localStorage.getItem(ME_KEY)
  if (!raw) return null
  try {
    const u = JSON.parse(raw) as CurrentUser
    return u.username && u.role ? u : null
  } catch {
    return null
  }
}

/// 缓存当前用户（登录成功 / 刷新 me 后调用）
export function saveMe(user: CurrentUser): void {
  localStorage.setItem(ME_KEY, JSON.stringify(user))
}

/// 清除当前用户缓存（退出登录）
export function clearMe(): void {
  localStorage.removeItem(ME_KEY)
}

/// 当前登录用户信息（GET /api/user/me，任何已登录角色；返回后调用方负责 saveMe 缓存）
export async function getMe(): Promise<CurrentUser | null> {
  const { data } = await client.get<ApiResponse<User>>('/user/me')
  if (data.success && data.data) {
    return { id: data.data.id, username: data.data.username, role: data.data.role }
  }
  return null
}

/// 用户列表（仅 Admin）
export async function getUsers(): Promise<User[]> {
  const { data } = await client.get<ApiResponse<User[]>>('/user')
  return data.data ?? []
}

export interface CreateUserInput {
  username: string
  password: string
  role: string
}

/// 新增用户（仅 Admin；用户名唯一，密码后端校验最小长度）
export async function createUser(input: CreateUserInput): Promise<User> {
  const { data } = await client.post<ApiResponse<User>>('/user', input)
  return data.data!
}

/// 改角色（仅 Admin）
export async function changeUserRole(id: number, role: string): Promise<User> {
  const { data } = await client.put<ApiResponse<User>>(`/user/${id}/role`, { role })
  return data.data!
}

/// 启停（仅 Admin；停用后下次登录被拒 403）
export async function setUserEnabled(id: number, isEnabled: boolean): Promise<User> {
  const { data } = await client.put<ApiResponse<User>>(`/user/${id}/enabled`, { isEnabled })
  return data.data!
}

/// 重置密码（仅 Admin 代改）
export async function resetUserPassword(id: number, newPassword: string): Promise<User> {
  const { data } = await client.put<ApiResponse<User>>(`/user/${id}/password`, { newPassword })
  return data.data!
}

/// 删除用户（仅 Admin）
export async function deleteUser(id: number): Promise<void> {
  await client.delete(`/user/${id}`)
}

/// 自助改密（任何已登录角色；需校验当前密码）
export async function changeMyPassword(currentPassword: string, newPassword: string): Promise<void> {
  await client.put('/user/me/password', { currentPassword, newPassword })
}
