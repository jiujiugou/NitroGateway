import client from './client'
import type { ApiResponse, Device, DevicePoint } from './types'

export async function getDevices(): Promise<Device[]> {
  const { data } = await client.get<ApiResponse<Device[]>>('/devices')
  return data.data ?? []
}

export async function getDevice(id: string): Promise<Device | null> {
  const { data } = await client.get<ApiResponse<Device>>(`/devices/${id}`)
  return data.data ?? null
}

export async function createDevice(d: Partial<Device>): Promise<Device | null> {
  const { data } = await client.post<ApiResponse<Device>>('/devices', d)
  return data.data ?? null
}

export async function updateDevice(id: string, d: Partial<Device>): Promise<Device | null> {
  const { data } = await client.put<ApiResponse<Device>>(`/devices/${id}`, d)
  return data.data ?? null
}

export async function deleteDevice(id: string): Promise<boolean> {
  const { data } = await client.delete<ApiResponse<unknown>>(`/devices/${id}`)
  return data.success
}

export async function updateDeviceStatus(id: string, status: string): Promise<Device | null> {
  const { data } = await client.put<ApiResponse<Device>>(`/devices/${id}/status`, `"${status}"`, { headers: { 'Content-Type': 'application/json' } })
  return data.data ?? null
}

export async function getPoints(deviceId: string): Promise<DevicePoint[]> {
  const { data } = await client.get<ApiResponse<DevicePoint[]>>(`/devices/${deviceId}/points`)
  return data.data ?? []
}

export async function addPoint(deviceId: string, p: Partial<DevicePoint>): Promise<DevicePoint | null> {
  const { data } = await client.post<ApiResponse<DevicePoint>>(`/devices/${deviceId}/points`, p)
  return data.data ?? null
}

export async function updatePoint(deviceId: string, pointId: string, p: Partial<DevicePoint>): Promise<DevicePoint | null> {
  const { data } = await client.put<ApiResponse<DevicePoint>>(`/devices/${deviceId}/points/${pointId}`, p)
  return data.data ?? null
}

export async function deletePoint(deviceId: string, pointId: string): Promise<boolean> {
  const { data } = await client.delete<ApiResponse<unknown>>(`/devices/${deviceId}/points/${pointId}`)
  return data.success
}

export async function importPoints(deviceId: string, csvText: string): Promise<number> {
  const { data } = await client.post<ApiResponse<{ count: number }>>(`/devices/${deviceId}/points/import`, csvText, { headers: { 'Content-Type': 'text/plain' } })
  return data.data?.count ?? 0
}

export async function generatePoints(deviceId: string, req: { nameTemplate: string; startAddress: number; count: number; dataType: string; access: string }): Promise<number> {
  const { data } = await client.post<ApiResponse<{ count: number }>>(`/devices/${deviceId}/points/generate`, req)
  return data.data?.count ?? 0
}

export async function exportPoints(deviceId: string): Promise<void> {
  const r = await client.get(`/devices/${deviceId}/points/export`, { responseType: 'blob' })
  const url = URL.createObjectURL(new Blob([r.data]))
  const a = document.createElement('a'); a.href = url; a.download = `points_${deviceId}.csv`; a.click()
  URL.revokeObjectURL(url)
}
