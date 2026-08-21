<template>
  <div>
    <h2 class="page-title">系统状态</h2>

    <!-- 核心指标 -->
    <div class="stat-grid">
      <!-- ADR-054：web 收敛为纯边缘（Linux 网关管理端），单一站点，本站点 ID 并入系统状态 -->
      <div class="stat-card">
        <div class="stat-label">本站点</div>
        <div class="stat-value stat-value-site">{{ siteId || '-' }}</div>
      </div>
      <!-- ADR-061：转发开关关闭（Disabled）显示「已关闭」且按 warning 样式，不误导为故障 -->
      <div class="stat-card" :class="mqttCardClass()">
        <div class="stat-label">MQTT</div>
        <div class="stat-value">{{ mqttStateLabel() }}</div>
      </div>
      <div class="stat-card" :class="backlog > 100 ? 'warn' : 'ok'">
        <div class="stat-label">缓冲积压</div>
        <div class="stat-value">{{ backlog }}</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">节流器</div>
        <div class="stat-value">{{ throttle.batch }} / {{ throttle.delay }}ms</div>
      </div>
      <div class="stat-card">
        <div class="stat-label">在线设备</div>
        <div class="stat-value">{{ onlineDevices }}</div>
      </div>
    </div>

    <!-- ADR-059：MQTT 上云转发总开关——运行期启停，无需改配置重启 -->
    <div class="card" style="margin-top:20px">
      <div style="display:flex;align-items:center;gap:14px">
        <el-switch v-model="forwardMqttEnabled" :loading="forwardMqttLoading"
                   @change="toggleForwardMqtt" />
        <div>
          <h3 style="margin:0;font-size:15px">MQTT 上云转发</h3>
          <div style="font-size:12px;color:var(--text-dim,#909399);margin-top:3px">
            {{ forwardMqttEnabled
                ? '已开启：采集/本地存储/告警不受影响，数据继续 MQTT 上云。'
                : '已关闭：照常采集与本地存储，仅暂停 MQTT 上云；恢复后从关闭时刻续传。' }}
          </div>
        </div>
      </div>
    </div>

    <!-- 设备熔断器状态（ADR-054：纯边缘形态恒展示） -->
    <div class="card" style="margin-top:20px">
      <h3 style="margin:0 0 16px">设备熔断器</h3>
      <el-table :data="breakers" size="small" empty-text="暂无设备">
        <el-table-column prop="deviceId" label="设备 ID" :formatter="shortId" />
        <el-table-column label="状态" width="120">
          <template #default="{ row }">
            <el-tag :type="breakerTag(row)" size="small">{{ row.state }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="通行" width="80">
          <template #default="{ row }">
            <span :style="{ color: row.isOpen ? '#f56c6c' : '#67c23a' }">{{ row.isOpen ? '阻断' : '通行' }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <!-- 设备健康 -->
    <div class="card" style="margin-top:20px">
      <h3 style="margin:0 0 16px">设备健康</h3>
      <el-table :data="health" size="small" empty-text="暂无设备">
        <el-table-column prop="deviceId" label="设备 ID" :formatter="shortId" />
        <el-table-column prop="status" label="状态" width="100" />
        <el-table-column prop="lastCollectionAt" label="最后采集" width="180">
          <template #default="{ row }">{{ row.lastCollectionAt ? fmtTime(row.lastCollectionAt) : '-' }}</template>
        </el-table-column>
        <el-table-column prop="consecutiveFailures" label="连续失败" width="90" />
        <el-table-column prop="lastError" label="最后错误" />
      </el-table>
    </div>

    <!-- 串口状态（ADR-054：纯边缘形态恒展示） -->
    <div class="card" style="margin-top:20px">
      <h3 style="margin:0 0 16px">串口状态</h3>
      <el-table :data="serialPorts" size="small" empty-text="暂无已打开的串口">
        <el-table-column prop="portName" label="端口" width="140" />
        <el-table-column prop="baudRate" label="波特率" width="90" />
        <el-table-column prop="dataBits" label="数据位" width="80" />
        <el-table-column prop="parity" label="校验位" width="90" />
        <el-table-column prop="stopBits" label="停止位" width="90" />
        <el-table-column prop="leaseCount" label="占用数" width="80" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.isOpen ? 'success' : 'info'" size="small">{{ row.isOpen ? '已打开' : '未打开' }}</el-tag>
          </template>
        </el-table-column>
      </el-table>
      <div style="margin-top:8px;font-size:12px;color:var(--text-dim,#909399)">
        可用串口：{{ availablePorts.join(', ') || '无' }}
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import client from '../../api/client'
import { getSerialPorts, getSerialPortStatus } from '../../api/devices'
import { getForwarderEnabled, setForwarderEnabled } from '../../api/forwarder'

const siteId = ref('')
const mqtt = ref({ state: '-', connected: false })
const backlog = ref(0)
const throttle = ref({ batch: 1000, delay: 0 })
const onlineDevices = ref(0)
const breakers = ref<any[]>([])
const health = ref<any[]>([])
const serialPorts = ref<any[]>([])
const availablePorts = ref<string[]>([])
// ADR-059：MQTT 上云转发开关（缺省启用；仅在首载与切换成功后更新，避免 3s 轮询覆盖用户操作）
const forwardMqttEnabled = ref(true)
const forwardMqttLoading = ref(false)

/// ADR-059：切换开关——即时生效并持久化（重启保持）；失败回滚到服务端当前值并提示。
async function toggleForwardMqtt(value: boolean) {
  forwardMqttLoading.value = true
  try {
    forwardMqttEnabled.value = await setForwarderEnabled(value)
    ElMessage.success(forwardMqttEnabled.value ? '已开启 MQTT 上云转发' : '已暂停 MQTT 上云转发')
  } catch (err: any) {
    try { forwardMqttEnabled.value = await getForwarderEnabled() } catch { /* 回滚读取失败则保持乐观值 */ }
    ElMessage.error(`切换失败: ${err?.response?.data?.error?.message ?? err?.message ?? '未知错误'}`)
  } finally {
    forwardMqttLoading.value = false
  }
}

async function refresh() {
  try {
    const { data: sys } = await client.get('/status/system')
    if (sys.data) {
      siteId.value = sys.data.siteId ?? ''
      // ADR-061：状态字串含 Disabled（开关关闭）——connected 仅 Connected 为真，供下方卡片映射
      mqtt.value = { state: sys.data.mqttState, connected: sys.data.mqttState === 'Connected' }
      backlog.value = sys.data.bufferBacklog
      throttle.value = { batch: sys.data.throttleBatchSize, delay: sys.data.throttleDelayMs }
      onlineDevices.value = sys.data.onlineDevices
      breakers.value = sys.data.circuitBreakers
    }
    const { data: h } = await client.get('/status/devices/health')
    if (h.data) health.value = h.data
    availablePorts.value = await getSerialPorts()
    serialPorts.value = await getSerialPortStatus()
  } catch {}
}

// ADR-007 P3-2：setInterval 需在 onUnmounted 清理，避免离开页面后继续轮询
let timer: number | undefined
onMounted(async () => {
  try { forwardMqttEnabled.value = await getForwarderEnabled() } catch { /* 保持缺省启用 */ }
  refresh()
  timer = window.setInterval(refresh, 3000)
})
onUnmounted(() => { if (timer !== undefined) window.clearInterval(timer) })

// ADR-007 P1-2：修复占位符恒返回 '-'；el-table formatter 签名 (row, column, cellValue, index)，多余参数忽略
const shortId = (row: any) => row.deviceId?.slice(0, 8) ?? '-'

function breakerTag(row: any): string {
  if (row.state === 'Closed') return 'success'
  if (row.state === 'HalfOpen') return 'warning'
  return 'danger'
}

function fmtTime(t: string): string {
  return t ? new Date(t).toLocaleTimeString() : '-'
}

// ADR-061：MQTT 状态枚举 → 中文文案（Disabled=转发开关关闭，非故障）
function mqttStateLabel(): string {
  const map: Record<string, string> = {
    Connected: '已连接',
    Connecting: '连接中',
    Reconnecting: '重连中',
    Disconnected: '未连接',
    Faulted: '故障',
    Disabled: '已关闭'
  }
  return map[mqtt.value.state] ?? mqtt.value.state ?? '-'
}

function mqttCardClass(): string {
  const s = mqtt.value.state
  if (s === 'Connected') return 'ok'
  if (s === 'Disabled' || s === 'Connecting' || s === 'Reconnecting') return 'warn'
  return 'err'
}
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.stat-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:12px; }
.stat-card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:16px; text-align:center; }
.stat-card.ok { border-color:#67c23a33; }
.stat-card.warn { border-color:#e6a23c33; }
.stat-card.err { border-color:#f56c6c33; }
.stat-label { font-size:13px; color:var(--text-dim,#909399); margin-bottom:4px; }
.stat-value { font-size:24px; font-weight:700; }
.stat-value-site { font-size:15px; word-break:break-all; line-height:1.3; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
</style>
