import client from './client'
import type { ApiResponse, BrowseNode, Device, DevicePoint, OpcUaCertificate } from './types'

// ADR-054：web 收敛为纯边缘（Linux 网关管理端），单一站点，设备列表不再按站点过滤
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

// 写功能（docs/14）：下发控制指令到点位。借用 ThingsGateway 行内就地输入，Web 端先弹就地气泡输入，确认后调用此 API。
// 失败原因由后端 ApiResponse.Fail 携带在 err.response.data.error.message（HTTP 400）。
export async function writePoint(
  deviceId: string,
  pointId: string,
  value: unknown
): Promise<{ success: boolean; message?: string }> {
  try {
    const { data } = await client.post<ApiResponse<unknown>>(
      `/devices/${deviceId}/points/${pointId}/write`,
      { value }
    )
    return { success: data.success }
  } catch (err: any) {
    return {
      success: false,
      message: err?.response?.data?.error?.message ?? err?.message ?? '写入失败'
    }
  }
}

export async function generatePoints(deviceId: string, req: { nameTemplate: string; startAddress: string; count: number; dataType: string; access: string; protocol?: string }): Promise<number> {
  const { data } = await client.post<ApiResponse<{ count: number }>>(`/devices/${deviceId}/points/generate`, req)
  return data.data?.count ?? 0
}

// ADR-055 缺口2：点位 CSV 导入。后端 PointImportController.ImportCsv 用 [FromBody] string 接收，
// 与 updateDeviceStatus 相同的 JSON 字符串编码（application/json + JSON.stringify）。
export async function importPoints(deviceId: string, csvText: string): Promise<number> {
  const { data } = await client.post<ApiResponse<{ count: number }>>(`/devices/${deviceId}/points/import`, JSON.stringify(csvText))
  return data.data?.count ?? 0
}

export async function exportPoints(deviceId: string): Promise<void> {
  const r = await client.get(`/devices/${deviceId}/points/export`, { responseType: 'blob' })
  const url = URL.createObjectURL(new Blob([r.data]))
  const a = document.createElement('a'); a.href = url; a.download = `points_${deviceId}.csv`; a.click()
  URL.revokeObjectURL(url)
}

// ADR-070 层次1：OPC UA 节点浏览（前端树点选）。parent 缺省 = Objects 目录（根）。
export async function browseNodes(deviceId: string, parent = ''): Promise<BrowseNode[]> {
  const { data } = await client.get<ApiResponse<BrowseNode[]>>(`/devices/${deviceId}/browse`, {
    params: parent ? { parent } : {}
  })
  return data.data ?? []
}

export async function testConnection(d: Partial<Device>): Promise<{ success: boolean; latencyMs: number; ping?: string; error?: string }> {
  const { data } = await client.post('/devices/test-connection', d)
  return data.data ?? { success: false, latencyMs: 0, error: '未知错误' }
}

export interface SerialPortInfo {
  portName: string
  isOpen: boolean
  leaseCount: number
  baudRate: number
  dataBits: number
  parity: string
  stopBits: string
}

export async function getSerialPorts(): Promise<string[]> {
  const { data } = await client.get<ApiResponse<string[]>>('/devices/serial-ports')
  return data.data ?? []
}

export async function getSerialPortStatus(): Promise<SerialPortInfo[]> {
  const { data } = await client.get<ApiResponse<SerialPortInfo[]>>('/devices/serial-port-status')
  return data.data ?? []
}

// ADR-073 D8：OPC UA 服务器证书信任管理（pki/rejected、pki/trusted 白名单）。信任状态以 pki 目录为唯一权威。
export async function getRejectedCertificates(): Promise<OpcUaCertificate[]> {
  const { data } = await client.get<ApiResponse<OpcUaCertificate[]>>('/opcua/certificates/rejected')
  return data.data ?? []
}

export async function getTrustedCertificates(): Promise<OpcUaCertificate[]> {
  const { data } = await client.get<ApiResponse<OpcUaCertificate[]>>('/opcua/certificates/trusted')
  return data.data ?? []
}

/// 信任指定指纹的服务器证书（从 rejected 移入 trusted 白名单）。可选 deviceId 触发该设备驱动驱逐 → 下一轮以新信任状态重连。
export async function trustCertificate(thumbprint: string, deviceId?: string): Promise<boolean> {
  const { data } = await client.post<ApiResponse<unknown>>(`/opcua/certificates/${thumbprint}/trust`, undefined, {
    params: deviceId ? { deviceId } : {}
  })
  return data.success
}

/// 撤销信任（把 trusted 白名单中的证书移除，回到未信任状态）。
export async function revokeCertificate(thumbprint: string): Promise<boolean> {
  const { data } = await client.delete<ApiResponse<unknown>>(`/opcua/certificates/${thumbprint}`)
  return data.success
}

