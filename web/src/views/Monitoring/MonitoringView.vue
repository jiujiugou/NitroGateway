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
    >
      <!-- 卡片头部 -->
      <div class="card-head">
        <div class="card-head-left">
          <span class="card-name">{{ dev.name }}</span>
          <StatusTag :status="dev.status" />
        </div>
        <span class="card-meta">{{ dev.protocol.name }} · {{ dev.connection.endpoint }}</span>
      </div>

      <!-- 点位数据行 -->
      <div v-if="(pointMap[dev.id]?.length ?? 0) > 0" class="point-rows">
        <div
          v-for="p in pointMap[dev.id]"
          :key="p.id"
          class="point-row"
          :title="`${p.name} · ${p.address}`"
        >
          <span class="point-name">{{ p.name }}</span>
          <span class="point-value" :class="{ stale: !snapshots[dev.id]?.[p.id] }">
            {{ snapshots[dev.id]?.[p.id] ? fmtVal(snapshots[dev.id][p.id].value) : '--' }}
          </span>
          <span v-if="snapshots[dev.id]?.[p.id]" class="point-quality">
            <el-tag
              :type="snapshots[dev.id][p.id].quality==='Good'?'success':snapshots[dev.id][p.id].quality==='Uncertain'?'warning':'danger'"
              size="small"
            >{{ snapshots[dev.id][p.id].quality }}</el-tag>
          </span>
        </div>
      </div>

      <!-- 无点位 -->
      <div v-else class="card-empty">暂无点位</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted } from 'vue'
import { getDevices, getPoints } from '../../api/devices'
import { getLatestBatch } from '../../api/measurements'
import { createLiveConnection } from '../../api/signalr'
import type { Device, DevicePoint, PointSnapshot } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'

const devices = ref<Device[]>([])
const pointMap = reactive<Record<string, DevicePoint[]>>({})
const snapshots = reactive<Record<string, Record<string, { value: unknown; quality: string; timestamp: string }>>>({})
const connected = ref(false)
let conn: any = null

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}

  // 加载点位 + 数据库最新值（并行）
  await Promise.all(devices.value.map(async dev => {
    try { pointMap[dev.id] = await getPoints(dev.id) } catch { pointMap[dev.id] = [] }
    try {
      const latest = await getLatestBatch(dev.id)
      latest.forEach((s: PointSnapshot) => {
        if (!snapshots[dev.id]) snapshots[dev.id] = {}
        snapshots[dev.id][s.devicePointId] = {
          value: s.value,
          quality: s.quality,
          timestamp: s.timestamp
        }
      })
    } catch {}
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

function fmtVal(v: unknown): string {
  if (typeof v === 'number') return v.toFixed(2)
  if (typeof v === 'boolean') return v ? 'ON' : 'OFF'
  return String(v ?? '--')
}
</script>

<style scoped>
.page-title { margin-bottom:16px; }
.topbar { display:flex; align-items:center; margin-bottom:20px; }
.status-dot { width:8px; height:8px; border-radius:50%; display:inline-block; margin-right:4px; }
.status-dot.online { background:#3fb950; } .status-dot.offline { background:#d29922; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); }

/* 设备卡片网格 */
.cards-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(320px,1fr)); gap:16px; }

/* 单张卡片 */
.device-card {
  background:var(--bg-card);
  border:1px solid var(--border);
  border-radius:var(--radius);
  overflow:hidden;
  transition:box-shadow .2s,border-color .2s;
  max-height:360px;
  display:flex;
  flex-direction:column;
}
.device-card:hover { box-shadow:0 2px 8px rgba(0,0,0,.06); }
.card-online { border-left:3px solid #3fb950; }
.card-offline { border-left:3px solid #f85149; }

/* 卡片头部 */
.card-head {
  display:flex; justify-content:space-between; align-items:center;
  padding:12px 16px; border-bottom:1px solid var(--border);
  flex-shrink:0;
}
.card-head-left { display:flex; align-items:center; gap:8px; }
.card-name { font-weight:600; font-size:14px; color:var(--text-heading); }
.card-meta { font-size:11px; color:var(--text-muted); font-family:monospace; }

/* 点位行 — 滚动 */
.point-rows {
  flex:1; overflow-y:auto; padding:4px 0;
}
.point-row {
  display:grid; grid-template-columns:1fr auto auto;
  align-items:center; gap:8px;
  padding:5px 16px;
  border-bottom:1px solid var(--border, #eee);
}
.point-row:last-child { border-bottom:none; }
.point-name {
  font-size:12px; color:var(--text-heading);
  overflow:hidden; text-overflow:ellipsis; white-space:nowrap;
}
.point-value {
  font-size:14px; font-weight:700; color:var(--accent);
  font-variant-numeric:tabular-nums; text-align:right; min-width:50px;
}
.point-value.stale { color:var(--text-muted,#bbb); }

.card-empty {
  padding:20px; text-align:center; color:var(--text-muted); font-size:12px;
}
</style>
