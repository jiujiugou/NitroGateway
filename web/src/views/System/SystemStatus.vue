<template>
  <div>
    <h2 class="page-title">系统状态</h2>

    <!-- 核心指标 -->
    <div class="stat-grid">
      <div class="stat-card" :class="mqtt.connected ? 'ok' : 'err'">
        <div class="stat-label">MQTT</div>
        <div class="stat-value">{{ mqtt.state }}</div>
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

    <!-- 熔断器状态 -->
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
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import client from '../../api/client'

const mqtt = ref({ state: '-', connected: false })
const backlog = ref(0)
const throttle = ref({ batch: 1000, delay: 0 })
const onlineDevices = ref(0)
const breakers = ref<any[]>([])
const health = ref<any[]>([])

async function refresh() {
  try {
    const { data: sys } = await client.get('/status/system')
    if (sys.data) {
      mqtt.value = { state: sys.data.mqttState, connected: sys.data.mqttConnected }
      backlog.value = sys.data.bufferBacklog
      throttle.value = { batch: sys.data.throttleBatchSize, delay: sys.data.throttleDelayMs }
      onlineDevices.value = sys.data.onlineDevices
      breakers.value = sys.data.circuitBreakers
    }
    const { data: h } = await client.get('/status/devices/health')
    if (h.data) health.value = h.data
  } catch {}
}

onMounted(() => { refresh(); setInterval(refresh, 3000) })

const shortId = (_r: any, _c: any, _i: number) => { return '-' }  // inline below

function breakerTag(row: any): string {
  if (row.state === 'Closed') return 'success'
  if (row.state === 'HalfOpen') return 'warning'
  return 'danger'
}

function fmtTime(t: string): string {
  return t ? new Date(t).toLocaleTimeString() : '-'
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
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
</style>
