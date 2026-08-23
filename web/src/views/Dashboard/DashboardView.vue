<template>
  <h2 class="page-title">仪表盘</h2>
  <div class="stats-grid">
    <div class="stat-card"><div class="value" style="color:#539bf5">{{ devices.length }}</div><div class="label">设备总数</div></div>
    <div class="stat-card"><div class="value" style="color:#3fb950">{{ online }}</div><div class="label">在线设备</div></div>
    <div class="stat-card"><div class="value" style="color:#f85149">{{ offline }}</div><div class="label">离线/故障</div></div>
    <div class="stat-card"><div class="value" style="color:#a371f7">{{ totalPoints }}</div><div class="label">总点位数</div></div>
    <!-- ADR-065 A1：告警/转发 KPI——今日告警（AlarmsController.Summary）+ 缓冲积压（/status/system） -->
    <div class="stat-card"><div class="value" style="color:#e6a23c">{{ alarmSummary.today }}</div><div class="label">今日告警</div></div>
    <div class="stat-card"><div class="value" style="color:#56d364">{{ backlog }}</div><div class="label">缓冲积压</div></div>
  </div>
  <!-- ADR-065 A1：活跃告警汇总——复用 /alarms 现有数据，面试第一屏即可见告警闭环 -->
  <div class="card" style="margin-top:20px">
    <div class="card-header">活跃告警（{{ activeAlarms.length }}）</div>
    <el-table :data="activeAlarms" size="small" empty-text="暂无活跃告警">
      <el-table-column label="等级" width="100">
        <template #default="{ row }"><el-tag :type="sevTag(row.severity)" size="small">{{ row.severity }}</el-tag></template>
      </el-table-column>
      <el-table-column label="状态" width="100">
        <template #default="{ row }"><el-tag :type="stateTag(row.state)" size="small">{{ stateText(row.state) }}</el-tag></template>
      </el-table-column>
      <el-table-column prop="message" label="消息" min-width="200" />
      <el-table-column label="发生时间" width="180">
        <template #default="{ row }">{{ fmtTime(row.occurredAt) }}</template>
      </el-table-column>
      <el-table-column label="" width="100">
        <template #default="{ row }"><el-button size="small" text @click="$router.push('/alarms')">查看 →</el-button></template>
      </el-table-column>
    </el-table>
  </div>
  <div class="card" style="margin-top:20px">
    <div class="card-header">设备概览</div>
    <el-table :data="devices" row-key="id">
      <el-table-column prop="name" label="名称" />
      <el-table-column label="协议" width="100"><template #default="{row}">{{ row.protocol.name }}{{ row.protocol.dialect ? '/'+row.protocol.dialect : '' }}</template></el-table-column>
      <el-table-column prop="connection.endpoint" label="连接地址" width="200" />
      <el-table-column label="状态" width="120"><template #default="{row}"><StatusTag :status="row.status" /></template></el-table-column>
      <el-table-column label="点位" width="60"><template #default="{row}">{{ row.points?.length ?? 0 }}</template></el-table-column>
      <el-table-column label="" width="80"><template #default="{row}"><el-button size="small" text @click="$router.push(`/devices/${row.id}`)">详情 →</el-button></template></el-table-column>
    </el-table>
  </div>
</template>
<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { getDevices } from '../../api/devices'
import { getAlarmSummary, getActiveAlarms, type AlarmSummary, type Alarm } from '../../api/alarms'
import { getSystemStatus } from '../../api/status'
import { createLiveConnection } from '../../api/signalr'
import type { Device, DeviceStatus } from '../../api/types'
import type { HubConnection } from '@microsoft/signalr'
import StatusTag from '../../components/DeviceStatusTag.vue'

const devices = ref<Device[]>([])
const alarmSummary = ref<AlarmSummary>({ active: 0, today: 0 })
const activeAlarms = ref<Alarm[]>([])
const backlog = ref(0)
const latestData = ref<Record<string, any>>({})
let conn: HubConnection | null = null
let kpiTimer: number | undefined

const online = computed(() => devices.value.filter(d=>d.status==='Online').length)
const offline = computed(() => devices.value.filter(d=>d.status==='Offline'||d.status==='Error').length)
const totalPoints = computed(() => devices.value.reduce((s,d)=>s+(d.points?.length??0), 0))

// ADR-065 A1：告警汇总 + 缓冲积压 10s 轮询（与 App.vue topbar 同源 /status/system，量级极低）
async function refreshKpis() {
  try { alarmSummary.value = await getAlarmSummary() } catch {}
  try { activeAlarms.value = await getActiveAlarms() } catch {}
  try { backlog.value = (await getSystemStatus()).bufferBacklog } catch {}
}

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}
  await refreshKpis()
  kpiTimer = window.setInterval(refreshKpis, 10000)
  conn = createLiveConnection()
  conn.on('Measurement', (data: any[]) => {
    // ADR-007 P2-1：payload 字段为 devicePointId（对齐 PointSnapshot.devicePointId），原写 m.pointId 恒为 undefined
    data.forEach((m: any) => { latestData.value[m.devicePointId] = m })
  })
  conn.on('DeviceStatusChanged', (d: { deviceId: string; status: DeviceStatus }) => {
    const dev = devices.value.find(x => x.id === d.deviceId)
    if (dev) dev.status = d.status
    // ADR-007 P2-3：挂载后上线的设备补订阅 Measurement 群组，否则收不到实时值
    if (d.status === 'Online') conn?.invoke('SubscribeDevice', d.deviceId).catch(() => {})
  })
  try { await conn.start() } catch (e) { console.warn('SignalR:', e) }
  devices.value.filter(d => d.status === 'Online').forEach(d => {
    conn?.invoke('SubscribeDevice', d.id).catch(() => {})
  })
})

onUnmounted(() => {
  if (kpiTimer !== undefined) window.clearInterval(kpiTimer)
  conn?.stop()
})

function sevTag(s: string) {
  return s === 'Critical' || s === 'Emergency' ? 'danger' : s === 'Warning' ? 'warning' : 'info'
}
function stateTag(s: string) { return s === 'Active' ? 'danger' : s === 'Acknowledged' ? 'warning' : 'success' }
function stateText(s: string) { return s === 'Active' ? '活跃' : s === 'Acknowledged' ? '已确认' : s }
function fmtTime(t: string) { return t ? new Date(t).toLocaleString() : '-' }
</script>
<style scoped>
.page-title { margin-bottom:24px; }
.stats-grid { display:grid; grid-template-columns:repeat(4,1fr); gap:16px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.card-header { padding:14px 20px; border-bottom:1px solid var(--border); color:var(--text-heading); font-weight:600; font-size:14px; }
</style>
