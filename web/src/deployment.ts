import { ref } from 'vue'
import client from './api/client'

/** 部署形态（与后端 DeploymentMode 对齐）。Gateway=边缘网关；Center=平台中心。 */
export type DeploymentMode = 'Gateway' | 'Center'

// ADR-044：前端部署形态，启动时从 /status/info 拉取一次。
// Gateway（默认）：测试连接/串口/死信等边缘能力全量保留；
// Center：裁剪边缘能力，B 阶段中心「意图下发/回执」沿用同一 mode 语义扩展。
export const mode = ref<DeploymentMode>('Gateway')

/** 启动时拉取部署形态；失败或后端无 /status/info 时默认 Gateway（兼容老版本）。 */
export async function initDeployment(): Promise<void> {
  try {
    const { data } = await client.get<{ success: boolean; data?: { mode?: string } }>('/status/info')
    mode.value = data?.data?.mode === 'Center' ? 'Center' : 'Gateway'
  } catch {
    mode.value = 'Gateway'
  }
}

/** 组件内取用部署形态的辅助函数。 */
export function useDeployment() {
  return {
    mode,
    isCenter: () => mode.value === 'Center',
    isGateway: () => mode.value !== 'Center',
  }
}
