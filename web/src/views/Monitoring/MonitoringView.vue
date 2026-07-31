<template>
  <h2 class="page-title">实时监控</h2>

  <div class="topbar">
    <span style="font-size:12px">
      <span :class="['status-dot', connected ? 'online' : 'offline']"></span>
      {{ connected ? '实时更新中' : '未连接' }}
    </span>
    <span style="margin-left:12px;color:var(--text-muted);font-size:12px">
      {{ devices.length }} 台设备
    </span>
  </div>

  <!-- 空状态 -->
  <div v-if="devices.length === 0" class="card" style="padding:60px;text-align:center;color:var(--text-muted)">
    <div style="font-size:48px;margin-bottom:16px">🔌</div>
    <div>暂无设备，请先添加设备</div>
    <div style="margin-top:8px">
      <el-button type="primary" @click="$router.push('/devices/new')">+ 添加设备</el-button>
    </div>
  </div>

  <!-- 设备卡片 -->
  <div v-else class="cards-grid">
    <div
      v-for="dev in devices"
      :key="dev.id"
      class="device-card"
      :class="{ 'card-online': dev.status === 'Online', 'card-offline': dev.status !== 'Online' }"
      @click="$router.push(`/devices/${dev.id}`)"
    >
      <div class="card-top">
        <span class="card-name">{{ dev.name }}</span>
        <StatusTag :status="dev.status" />
      </div>

      <div class="card-body">
        <div class="card-protocol">{{ dev.protocol.name }}{{ dev.protocol.dialect ? ' / '+dev.protocol.dialect : '' }}</div>
        <div class="card-endpoint">{{ dev.connection.endpoint }}</div>
      </div>

      <div class="card-bottom">
        <div class="card-stat">
          <span class="stat-num">{{ pointCount(dev.id) }}</span>
          <span class="stat-label">点位</span>
        </div>
        <div class="card-stat">
          <span class="stat-num">{{ latestCount(dev.id) }}</span>
          <span class="stat-label">最新</span>
        </div>
        <div class="card-stat">
          <span class="stat-num" :style="{ color: dev.status === 'Online' ? '#3fb950' : '#f85149' }">
            {{ dev.status === 'Online' ? '●' : '●' }}
          </span>
          <span class="stat-label">{{ dev.status === 'Online' ? '在线' : dev.status }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted } from 'vue'
import { getDevices, getPoints } from '../../api/devices'
import { createLiveConnection } from '../../api/signalr'
import type { Device, DevicePoint } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'

const devices = ref<Device[]>([])
const pointMap = reactive<Record<string, DevicePoint[]>>({})
const snapshots = reactive<Record<string, Record<string, { value: unknown; quality: string; timestamp: string }>>>({})
const connected = ref(false)
let conn: any = null

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}
  await Promise.all(devices.value.map(async dev => {
    try { pointMap[dev.id] = await getPoints(dev.id) } catch { pointMap[dev.id] = [] }
  }))

  conn = createLiveConnection()
  conn.on('Measurement', (data: any[]) => {
    (Array.isArray(data) ? data : [data]).forEach((m: any) => {
      if (!snapshots[m.deviceId]) snapshots[m.deviceId] = {}
      snapshots[m.deviceId][m.devicePointId] = {
        value: m.value,
        quality: m.quality,
        timestamp: m.timestamp
      }
    })
  })
  conn.on('DeviceStatusChanged', (d: { deviceId: string; status: string }) => {
    const dev = devices.value.find(x => x.id === d.deviceId)
    if (dev) dev.status = d.status as any
  })
  conn.onreconnected(() => { connected.value = true })
  conn.onclose(() => { connected.value = false })
  try {
    await conn.start()
    connected.value = true
    devices.value.filter(d => d.status === 'Online').forEach(d => {
      conn?.invoke('SubscribeDevice', d.id).catch(() => {})
    })
  } catch (e) { console.warn('SignalR:', e) }
})

onUnmounted(() => { conn?.stop() })

function pointCount(deviceId: string): number { return pointMap[deviceId]?.length ?? 0 }
function latestCount(deviceId: string): number { return Object.keys(snapshots[deviceId] ?? {}).length }
</script>

<style scoped>
.page-title { margin-bottom:16px; }
.topbar { display:flex; align-items:center; margin-bottom:20px; }
.status-dot { width:8px; height:8px; border-radius:50%; display:inline-block; margin-right:4px; }
.status-dot.online { background:#3fb950; } .status-dot.offline { background:#d29922; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); }
.cards-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(260px,1fr)); gap:16px; }
.device-card {
  background:var(--bg-card);
  border:1px solid var(--border);
  border-radius:var(--radius);
  padding:20px;
  cursor:pointer;
  transition:box-shadow .2s,border-color .2s;
}
.device-card:hover { box-shadow:0 2px 12px rgba(0,0,0,.06); }
.card-online { border-left:3px solid #3fb950; }
.card-offline { border-left:3px solid #f85149; }
.card-top { display:flex; justify-content:space-between; align-items:center; margin-bottom:12px; }
.card-name { font-weight:600; font-size:15px; color:var(--text-heading); }
.card-body { margin-bottom:16px; }
.card-protocol { font-size:13px; color:var(--text-heading); }
.card-endpoint { font-size:11px; color:var(--text-muted); margin-top:2px; font-family:monospace; }
.card-bottom { display:flex; gap:20px; }
.card-stat { display:flex; flex-direction:column; }
.stat-num { font-size:20px; font-weight:700; color:var(--text-heading); }
.stat-label { font-size:10px; color:var(--text-muted); text-transform:uppercase; letter-spacing:.5px; }
</style>
