<template>
  <h2 class="page-title">实时监控</h2>
  <div class="topbar">
    <!-- ADR-035 第 1 步：按站点过滤实时数据（空 = 全部站点） -->
    <SiteFilter v-model="siteId" />
    <span class="topbar-sep"></span>
    <span style="font-size:12px">
      <span :class="['status-dot', connected ? 'online' : 'offline']"></span>
      {{ connected ? '实时更新中' : '未连接' }}
    </span>
    <span style="margin-left:12px;color:var(--text-muted);font-size:12px">
      {{ visibleDevices.length }} 台设备
    </span>
  </div>

  <!-- 空状态 -->
  <div v-if="visibleDevices.length === 0" class="card" style="padding:60px;text-align:center;color:var(--text-muted)">
    <div style="font-size:48px;margin-bottom:16px">🔌</div>
    <div>暂无设备，或当前站点暂无数据</div>
    <div style="margin-top:8px">
      <el-button type="primary" @click="$router.push('/devices/new')">+ 添加设备</el-button>
    </div>
  </div>

  <!-- 设备卡片 -->
  <div v-else class="cards-grid">
    <div
      v-for="dev in visibleDevices"
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
          <span class="point-value" :class="{ stale: isStale(dev.id, p.id) }">
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

  <!-- ADR-055 缺口1：实时曲线 —— 点选点位 → ECharts 滚动曲线（预载 2h + SignalR Measurement 追加，环形缓冲上限对齐桌面 7200 点） -->
  <div v-if="devices.length > 0" class="card chart-card">
    <div class="chart-head">
      <span class="chart-title">实时曲线</span>
      <el-select v-model="chartDeviceId" placeholder="选择设备" clearable style="width:180px" @change="onChartDeviceChange">
        <el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" />
      </el-select>
      <el-select v-model="chartPointId" placeholder="选择点位" clearable style="width:240px" @change="onChartPointChange">
        <el-option v-for="p in chartPointOptions" :key="p.id" :label="`${p.name} (${p.address})`" :value="p.id" />
      </el-select>
      <span v-if="chartPointLabel" class="chart-point-label">{{ chartPointLabel }}</span>
    </div>
    <div class="chart-body">
      <div ref="chartRef" style="width:100%;height:100%"></div>
      <div v-if="!chartPointId" class="chart-empty">选择设备与点位后，此处显示最近 2 小时实时曲线</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, watch, onMounted, onUnmounted } from 'vue'
// ADR-055 缺口1：复用 HistoryView 的 echarts 按需引入（Line + Grid/Tooltip + Canvas），替代整包引入
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
echarts.use([LineChart, GridComponent, TooltipComponent, CanvasRenderer])
import { getDevices, getPoints } from '../../api/devices'
import { getLatestBatch, getHistory } from '../../api/measurements'
import { createLiveConnection } from '../../api/signalr'
import type { Device, DevicePoint, PointSnapshot } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'
import SiteFilter from '../../components/SiteFilter.vue'

const devices = ref<Device[]>([])
const pointMap = reactive<Record<string, DevicePoint[]>>({})
const snapshots = reactive<Record<string, Record<string, { value: unknown; quality: string; timestamp: string; lastSeenAt: number }>>>({})
const connected = ref(false)
const siteId = ref('')
let conn: any = null

// ADR-055 缺口1：实时曲线状态。选中点位后预载最近 2h 历史（默认 limit=1000，后端夹紧上限），
// 之后由 SignalR Measurement 增量追加；环形缓冲上限对齐桌面 MaxChartPoints=7200（1s 采集 ≈ 2h 窗口）。
const MAX_CHART_POINTS = 7200
const chartRef = ref<HTMLElement>()
const chartDeviceId = ref('')
const chartPointId = ref('')
const chartPointLabel = ref('')
const chartSeries = ref<{ time: string; value: number }[]>([])
let chart: ReturnType<typeof echarts.init> | null = null
let chartRedrawTimer: ReturnType<typeof setTimeout> | undefined

// ADR-055：点位下拉复用已加载的 pointMap，只列出启用点位（与桌面端一致）
const chartPointOptions = computed(() => {
  if (!chartDeviceId.value) return []
  return (pointMap[chartDeviceId.value] ?? []).filter(p => p.enabled)
})

// ADR-053：SignalR 只推「变化点 + 心跳（默认 300s）」。stale = 超过 2×心跳没收到任何更新
// （含心跳）——真正断流才标灰；静默点位由心跳续命，避免把"没变化"误判为"已掉线"。
const STALE_AFTER_MS = 10 * 60 * 1000 // 2 × 心跳 300s
const nowTick = ref(Date.now()) // 定时器递增，驱动 isStale 响应式重算
let staleTimer: ReturnType<typeof setInterval> | undefined

// ADR-035 第 1 步：选中具体站点时仅展示该站点有数据的设备（设备本身是共享配置，不归属站点）
const visibleDevices = computed(() => {
  if (!siteId.value) return devices.value
  return devices.value.filter(d => snapshots[d.id] && Object.keys(snapshots[d.id]).length > 0)
})

// ADR-035 第 1 步：按当前站点重拉最新值；切换站点先清空旧快照，避免跨站点数据残留
async function loadLatest() {
  Object.keys(snapshots).forEach(k => delete snapshots[k])
  await Promise.all(devices.value.map(async dev => {
    try {
      const latest = await getLatestBatch(dev.id, siteId.value)
      latest.forEach((s: PointSnapshot) => {
        if (!snapshots[dev.id]) snapshots[dev.id] = {}
        snapshots[dev.id][s.devicePointId] = {
          value: s.value,
          quality: s.quality,
          timestamp: s.timestamp,
          lastSeenAt: Date.now() // REST 拉到的最新值视为"刚收到"，避免立即标 stale
        }
      })
    } catch {}
  }))
}

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}

  // 加载点位（并行）
  await Promise.all(devices.value.map(async dev => {
    try { pointMap[dev.id] = await getPoints(dev.id) } catch { pointMap[dev.id] = [] }
  }))
  await loadLatest()

  conn = createLiveConnection()
  conn.on('Measurement', (data: any[]) => {
    // ADR-035 第 1 步：SignalR payload 无 site 字段，选中具体站点时忽略实时推送，
    // 防止跨站点数据串台；切回「全部站点」恢复实时更新
    if (siteId.value) return
    (Array.isArray(data) ? data : [data]).forEach((m: any) => {
      if (!snapshots[m.deviceId]) snapshots[m.deviceId] = {}
      snapshots[m.deviceId][m.devicePointId] = {
        value: m.value,
        quality: m.quality,
        timestamp: m.timestamp,
        lastSeenAt: Date.now()
      }
      appendChartPoint(m)
    })
  })
  conn.on('DeviceStatusChanged', (d: { deviceId: string; status: string }) => {
    const dev = devices.value.find(x => x.id === d.deviceId)
    if (dev) dev.status = d.status as any
    // ADR-007 P2-3：挂载后上线的设备补订阅 Measurement 群组，否则收不到实时值
    if (d.status === 'Online') conn?.invoke('SubscribeDevice', d.deviceId).catch(() => {})
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

  // ADR-053：30s tick 一次，让超过阈值未更新的点位从灰变回/变灰（不依赖下一条推送）
  staleTimer = setInterval(() => { nowTick.value = Date.now() }, 30_000)
  window.addEventListener('resize', onChartResize)
})

watch(siteId, () => { loadLatest() })

onUnmounted(() => {
  conn?.stop()
  if (staleTimer) clearInterval(staleTimer)
  if (chartRedrawTimer) clearTimeout(chartRedrawTimer)
  window.removeEventListener('resize', onChartResize)
  chart?.dispose()
})

// ═══════════ 实时曲线（ADR-055 缺口1） ═══════════

function onChartDeviceChange() {
  chartPointId.value = ''
  chartPointLabel.value = ''
  chartSeries.value = []
  renderChart()
}

async function onChartPointChange() {
  chartSeries.value = []
  if (!chartDeviceId.value || !chartPointId.value) { chartPointLabel.value = ''; renderChart(); return }
  const pt = chartPointOptions.value.find(p => p.id === chartPointId.value)
  chartPointLabel.value = pt ? `${pt.name} (${pt.address})` : ''
  await loadChartHistory()
}

// 预载最近 2h 历史（与桌面 LoadPointHistoryAsync 对齐），给曲线一个"有历史上下文"的起点
async function loadChartHistory() {
  const from = new Date(Date.now() - 2 * 3600 * 1000).toISOString()
  const to = new Date().toISOString()
  chartSeries.value = []
  try {
    const rows = await getHistory(chartDeviceId.value, chartPointId.value, from, to, siteId.value, 1000)
    chartSeries.value = rows
      .map(s => ({ time: s.timestamp, value: toNum(s.value) }))
      .filter((p): p is { time: string; value: number } => p.value !== null)
  } catch {}
  renderChart()
}

// SignalR Measurement 命中选中点位 → 追加环形缓冲，超上限批量裁剪（对齐桌面 ADR-037 S12）
function appendChartPoint(m: any) {
  if (!chartDeviceId.value || !chartPointId.value) return
  if (m.deviceId !== chartDeviceId.value || m.devicePointId !== chartPointId.value) return
  const v = toNum(m.value)
  if (v === null) return
  chartSeries.value.push({ time: m.timestamp, value: v })
  const overflow = chartSeries.value.length - MAX_CHART_POINTS
  if (overflow > 0) chartSeries.value.splice(0, overflow)
  scheduleChartRedraw()
}

// 重绘节流：最多每 500ms 一次（对齐桌面 ChartRefreshInterval），避免每条推送都全量重绘
function scheduleChartRedraw() {
  if (chartRedrawTimer) return
  chartRedrawTimer = setTimeout(() => {
    chartRedrawTimer = undefined
    renderChart()
  }, 500)
}

function renderChart() {
  if (!chartRef.value) return
  if (!chart) chart = echarts.init(chartRef.value)
  // ADR-053：死区抑制后为「稀疏变化点」，step:'end' 避免 ECharts 在长静默段画假连续线（同 HistoryView）
  chart.setOption({
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time', axisLabel: { color: '#a0aec0' } },
    yAxis: { type: 'value', scale: true, axisLabel: { color: '#a0aec0' } },
    grid: { left: 50, right: 20, top: 30, bottom: 30 },
    series: [{
      name: chartPointLabel.value || '实时值',
      data: chartSeries.value.map(p => [p.time, p.value]),
      type: 'line', step: 'end', showSymbol: false,
      lineStyle: { color: '#409eff', width: 2 },
      areaStyle: {
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [{ offset: 0, color: 'rgba(64,158,255,.15)' }, { offset: 1, color: 'rgba(64,158,255,0)' }] }
      }
    }]
  }, { notMerge: true })
}

function onChartResize() { chart?.resize() }

// 尝试把点位值转数值（Bool→0/1，数值字符串→Number；非数值点位不上曲线，对齐桌面 TryToDouble）
function toNum(v: unknown): number | null {
  if (typeof v === 'number') return Number.isFinite(v) ? v : null
  if (typeof v === 'boolean') return v ? 1 : 0
  if (typeof v === 'string') {
    const n = Number(v)
    return Number.isFinite(n) ? n : null
  }
  return null
}

// ADR-053：点位是否已断流（从未收到值，或超过 2×心跳无任何更新）
function isStale(devId: string, pid: string): boolean {
  const s = snapshots[devId]?.[pid]
  if (!s) return true
  return nowTick.value - s.lastSeenAt > STALE_AFTER_MS
}

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

/* 实时曲线卡片（ADR-055 缺口1） */
.chart-card { margin-top:16px; padding:16px; }
.chart-head { display:flex; align-items:center; gap:12px; margin-bottom:12px; flex-wrap:wrap; }
.chart-title { font-weight:600; font-size:14px; color:var(--text-heading); margin-right:auto; }
.chart-point-label { font-size:12px; color:var(--text-muted); font-family:monospace; }
.chart-body { position:relative; height:300px; }
.chart-empty {
  position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
  color:var(--text-muted); font-size:13px; pointer-events:none;
}
</style>




