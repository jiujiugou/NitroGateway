<template>
  <div class="monitor-page">
    <!-- 页头 -->
    <div class="page-head">
      <h2 class="page-title">实时监控</h2>
      <span class="page-sub">实时数据 · {{ devices.length }} 台设备</span>
    </div>

    <!-- 空状态 -->
    <div v-if="devices.length === 0" class="card empty-state">
      <div class="empty-icon">🔌</div>
      <div class="empty-text">暂无设备</div>
      <el-button type="primary" @click="$router.push('/devices/new')">+ 添加设备</el-button>
    </div>

    <template v-else>
      <div class="monitor-layout">
        <!-- 左上：设备 / 点位 选择 + 连接状态 -->
        <div class="select-bar card">
          <div class="select-group">
            <span class="select-label">设备</span>
            <el-select v-model="selectedDeviceId" placeholder="全部设备" clearable style="width:200px" @change="onDeviceFilterChange">
              <el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" />
            </el-select>
          </div>
          <div class="select-group">
            <span class="select-label">点位</span>
            <el-select v-model="chartSelection" placeholder="选择点位（右侧曲线）" clearable filterable style="width:280px" @change="onChartSelectionChange">
              <el-option v-for="o in chartPointOptions" :key="o.value" :label="o.label" :value="o.value" />
            </el-select>
          </div>
          <span v-if="chartPointLabel" class="chart-point-label">{{ chartPointLabel }}</span>
          <span class="select-bar-spacer"></span>
          <span class="status-line">
            <span :class="['status-dot', connected ? 'online' : 'offline']"></span>
            {{ connected ? '实时更新中' : '未连接' }}
          </span>
        </div>

        <!-- 左右分栏：左 = 设备点位数据，右 = 点位曲线 -->
        <div class="main-split">
          <!-- 左侧：设备点位数据（选中设备只显示该设备，否则全部设备卡片纵排） -->
          <div class="left-panel card">
            <div class="panel-head">
              <span>设备点位数据</span>
              <span class="panel-meta">{{ pointCountText }}</span>
            </div>
            <div class="left-body">
              <div
                v-for="dev in displayDevices"
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

                <!-- 点位数据行（点击 → 右侧出曲线） -->
                <div v-if="(pointMap[dev.id]?.length ?? 0) > 0" class="point-rows">
                  <div
                    v-for="p in pointMap[dev.id]"
                    :key="p.id"
                    class="point-row"
                    :class="{ plotting: isPlotted(dev.id, p.id) }"
                    :title="`${p.name} · ${p.address}${p.enabled ? '' : '（已停用）'} — 点击查看曲线`"
                    @click="selectPointForChart(dev.id, p)"
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
                    <!-- 写功能（docs/14）：行尾按钮 → 就地气泡内嵌输入 → 确认 → toast。仅可写点位（WriteOnly/ReadWrite）显示；ReadOnly 无写按钮。 -->
                    <el-popover
                      v-if="p.access !== 'ReadOnly'"
                      placement="left"
                      :width="260"
                      trigger="click"
                      :visible="writePopovers[`${dev.id}:${p.id}`]"
                      @show="initWriteValue(dev.id, p)"
                      @hide="writePopovers[`${dev.id}:${p.id}`] = false"
                    >
                      <template #reference>
                        <el-button
                          class="write-btn"
                          size="small"
                          text
                          type="primary"
                          @click.stop="writePopovers[`${dev.id}:${p.id}`] = !writePopovers[`${dev.id}:${p.id}`]"
                        >写值</el-button>
                      </template>
                      <div class="write-editor">
                        <div class="write-title">
                          <span class="write-name">{{ p.name }}</span>
                          <span class="write-meta">{{ p.dataType }} · {{ p.address }}</span>
                        </div>
                        <el-switch
                          v-if="p.dataType === 'Bool'"
                          v-model="writeValues[`${dev.id}:${p.id}`]"
                          active-text="ON"
                          inactive-text="OFF"
                        />
                        <el-input-number
                          v-else-if="isNumericType(p.dataType)"
                          v-model="writeValues[`${dev.id}:${p.id}`]"
                          :step="p.dataType === 'Float' || p.dataType === 'Double' ? 0.1 : 1"
                          controls-position="right"
                          style="width:100%"
                        />
                        <el-input
                          v-else
                          v-model="writeValues[`${dev.id}:${p.id}`]"
                          placeholder="输入值"
                        />
                        <div class="write-actions">
                          <el-button size="small" @click="writePopovers[`${dev.id}:${p.id}`] = false">取消</el-button>
                          <el-button size="small" type="primary" :loading="writing" @click="confirmWrite(dev.id, p)">确定</el-button>
                        </div>
                      </div>
                    </el-popover>
                  </div>
                </div>
                <div v-else class="card-empty">暂无点位</div>
              </div>
            </div>
          </div>

          <!-- 右侧：点位曲线（ADR-055 缺口1：选点 → ECharts 滚动曲线，预载 2h + SignalR Measurement 追加，环形缓冲上限对齐版面 7200 点） -->
          <div class="right-panel card">
            <div class="panel-head">
              <span>点位曲线</span>
              <span class="panel-meta">{{ chartPointLabel || '未选择点位' }}</span>
            </div>
            <div class="chart-body">
              <div ref="chartRef" class="chart-canvas"></div>
              <div v-if="!chartSelection" class="chart-empty">在上方选择点位，或点击左侧点位行，此处显示最近 2 小时实时曲线</div>
            </div>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, onUnmounted, nextTick } from 'vue'
// ADR-055 缺口1：复用 HistoryView 的 echarts 按需引入（Line + Grid/Tooltip + Canvas），替代整包引入
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
echarts.use([LineChart, GridComponent, TooltipComponent, CanvasRenderer])
import { getDevices, getPoints, writePoint } from '../../api/devices'
import { getLatestBatch, getHistory } from '../../api/measurements'
import { createLiveConnection } from '../../api/signalr'
import { ElMessage } from 'element-plus'
import type { Device, DevicePoint, PointSnapshot } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'

const devices = ref<Device[]>([])
const pointMap = reactive<Record<string, DevicePoint[]>>({})
const snapshots = reactive<Record<string, Record<string, { value: unknown; quality: string; timestamp: string; lastSeenAt: number }>>>({})
const connected = ref(false)
let conn: any = null

// 写功能（docs/14）：就地气泡输入状态。writeValues / writePopovers 以 `${deviceId}:${pointId}` 为键，多卡片同时展开互不串值。
const writeValues = reactive<Record<string, unknown>>({})
const writePopovers = reactive<Record<string, boolean>>({})
const writing = ref(false)

// 数值点位类型（Bool 走 switch、String 走 text，其余数值点位走 number 输入框）
const NUMERIC_TYPES: DevicePoint['dataType'][] = ['Byte', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Int64', 'UInt64', 'Float', 'Double']
function isNumericType(t: DevicePoint['dataType']): boolean {
  return NUMERIC_TYPES.includes(t)
}

// ---- 左侧筛选（左上「设备」下拉）：'' = 全部设备 ----
const selectedDeviceId = ref('')
const displayDevices = computed(() =>
  selectedDeviceId.value ? devices.value.filter(d => d.id === selectedDeviceId.value) : devices.value
)
const pointCountText = computed(() => {
  const count = displayDevices.value.reduce((n, d) => n + (pointMap[d.id]?.length ?? 0), 0)
  return `${count} 点位`
})

// ---- 右侧曲线点位（左上「点位」下拉 + 左侧点位行点击共用）----
// 用 `${deviceId}:${pointId}` 字符串做选项值，避免 el-select 对象值重渲染不匹配的问题。
const chartSelection = ref('')
const chartDeviceId = ref('')
const chartPointId = ref('')
const chartPointLabel = ref('')
const chartSeries = ref<{ time: string; value: number }[]>([])

interface ChartPointOption { value: string; label: string; deviceId: string; name: string; address: string }
// 只列启用点位（与桌面端一致）；全部设备时用「设备名 / 点位名」区分，选单设备时省略设备前缀。
const chartPointOptions = computed<ChartPointOption[]>(() => {
  const showAll = !selectedDeviceId.value
  const src = showAll ? devices.value : displayDevices.value
  const opts: ChartPointOption[] = []
  for (const d of src) {
    for (const p of (pointMap[d.id] ?? [])) {
      if (!p.enabled) continue
      opts.push({
        value: `${d.id}:${p.id}`,
        label: showAll ? `${d.name} / ${p.name} (${p.address})` : `${p.name} (${p.address})`,
        deviceId: d.id, name: p.name, address: p.address
      })
    }
  }
  return opts
})

function applyChartSelection(key: string) {
  const [devId, ptId] = key.split(':')
  chartDeviceId.value = devId
  chartPointId.value = ptId
  const opt = chartPointOptions.value.find(o => o.value === key)
  chartPointLabel.value = opt ? `${opt.name} (${opt.address})` : ''
}

function onChartSelectionChange(key: string | undefined) {
  if (!key) {
    chartSelection.value = ''
    chartDeviceId.value = ''
    chartPointId.value = ''
    chartPointLabel.value = ''
    chartSeries.value = []
    renderChart()
    return
  }
  applyChartSelection(key)
  loadChartHistory()
}

// 左上「设备」变化：若当前曲线点位属于被过滤掉的设备，则清空曲线选择（'' = 全部设备时不清）
function onDeviceFilterChange() {
  if (chartDeviceId.value && chartDeviceId.value !== selectedDeviceId.value) {
    chartSelection.value = ''
    onChartSelectionChange('')
  }
}

// 点击左侧点位行 → 右侧出该点位曲线
function selectPointForChart(devId: string, p: DevicePoint) {
  if (!p.enabled) return
  chartSelection.value = `${devId}:${p.id}`
  onChartSelectionChange(chartSelection.value)
}

// 当前点位行是否正在右侧绘图（高亮）
function isPlotted(devId: string, pid: string): boolean {
  return chartDeviceId.value === devId && chartPointId.value === pid
}

// 气泡展开时以当前实时值为默认输入，减少重复输入；无实时值则用类型默认值
function initWriteValue(devId: string, p: DevicePoint) {
  const key = `${devId}:${p.id}`
  const cur = snapshots[devId]?.[p.id]?.value
  if (p.dataType === 'Bool') {
    writeValues[key] = cur === true || cur === 1 || cur === '1'
  } else if (p.dataType === 'String') {
    writeValues[key] = cur != null ? String(cur) : ''
  } else {
    const n = toNum(cur)
    writeValues[key] = n != null ? n : 0
  }
}

// 确认写值：调后端 WriteController（Access + WriteGuard 三级门控在后端统一校验），toast 反馈结果
async function confirmWrite(devId: string, p: DevicePoint) {
  const key = `${devId}:${p.id}`
  let value: unknown = writeValues[key]
  if (p.dataType === 'Bool') value = Boolean(value)
  writing.value = true
  try {
    const r = await writePoint(devId, p.id, value)
    if (r.success) {
      writePopovers[key] = false
      ElMessage.success(`写入成功：${p.name}`)
    } else {
      ElMessage.error(`写入失败（${p.name}）：${r.message ?? '未知原因'}`)
    }
  } finally {
    writing.value = false
  }
}

// ADR-053：SignalR 只推「变化点 + 心跳（默认 300s）」。stale = 超过 2×心跳没收到任何更新
// （含心跳）——真正断流才标灰；静默点位由心跳续命，避免把「没变化」误判为「已掉线」。
const STALE_AFTER_MS = 10 * 60 * 1000 // 2 × 心跳 300s
const nowTick = ref(Date.now()) // 定时器递增，驱动 isStale 响应式重算
let staleTimer: ReturnType<typeof setInterval> | undefined
async function loadLatest() {
  Object.keys(snapshots).forEach(k => delete snapshots[k])
  await Promise.all(devices.value.map(async dev => {
    try {
      const latest = await getLatestBatch(dev.id)
      latest.forEach((s: PointSnapshot) => {
        if (!snapshots[dev.id]) snapshots[dev.id] = {}
        snapshots[dev.id][s.devicePointId] = {
          value: s.value,
          quality: s.quality,
          timestamp: s.timestamp,
          lastSeenAt: Date.now() // REST 拉到的最新值视为「刚收到」，避免立即标 stale
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
    // ADR-007 P2-3：挂载后上线的设备补订阅 Measurement 组，否则收不到实时值
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

  // ADR-053：30s tick 一次，让超过阈值未更新的点位从灰变亮/变灰（不依赖下一条推送）
  staleTimer = setInterval(() => { nowTick.value = Date.now() }, 30_000)
  window.addEventListener('resize', onChartResize)
  // 布局稳定后再校一次图表尺寸（左右分栏高度由 flex 撑开）
  nextTick(() => chart?.resize())
})

onUnmounted(() => {
  conn?.stop()
  if (staleTimer) clearInterval(staleTimer)
  if (chartRedrawTimer) clearTimeout(chartRedrawTimer)
  window.removeEventListener('resize', onChartResize)
  chart?.dispose()
})

// ── 实时曲线（ADR-055 缺口1）──
const MAX_CHART_POINTS = 7200
const chartRef = ref<HTMLElement>()
let chart: ReturnType<typeof echarts.init> | null = null
let chartRedrawTimer: ReturnType<typeof setTimeout> | undefined

// 预载最近 2h 历史（与桌面端 LoadPointHistoryAsync 对齐），给曲线一个「有历史上下文」的起点
async function loadChartHistory() {
  const from = new Date(Date.now() - 2 * 3600 * 1000).toISOString()
  const to = new Date().toISOString()
  chartSeries.value = []
  try {
    const rows = await getHistory(chartDeviceId.value, chartPointId.value, from, to, 1000)
    chartSeries.value = rows
      .map(s => ({ time: s.timestamp, value: toNum(s.value) }))
      .filter((p): p is { time: string; value: number } => p.value !== null)
  } catch {}
  renderChart()
}

// SignalR Measurement 命中选中点位 → 追加环形缓冲，超上限批量裁剪（对齐桌面端 ADR-037 S12）
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

// 重绘节流：最多每 500ms 一次（对齐桌面端 ChartRefreshInterval），避免每条推送都全量重绘
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

// 尝试把点位值转数值（Bool→0/1，数值字符串→Number；非数值点位不上曲线，对齐桌面端 TryToDouble）
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
.monitor-page { display:flex; flex-direction:column; gap:14px; height: calc(100vh - 160px); min-height: 520px; }
.page-head { display:flex; align-items:baseline; gap:12px; }
.page-title { margin:0; font-size:20px; font-weight:600; color:var(--text-heading,#1a202c); }
.page-sub { font-size:12px; color:var(--text-muted,#a0aec0); }
.card { background:var(--bg-card,#fff); border:1px solid var(--border,#e4e7ed); border-radius:var(--radius,8px); }
.empty-state { padding:60px; text-align:center; color:var(--text-muted,#a0aec0); flex:1; }
.empty-icon { font-size:48px; margin-bottom:16px; }
.empty-text { margin-bottom:8px; }

.monitor-layout { flex:1; display:flex; flex-direction:column; gap:14px; min-height:0; }

/* 左上选择条 */
.select-bar { flex-shrink:0; display:flex; align-items:center; gap:12px; padding:10px 16px; flex-wrap:wrap; }
.select-group { display:flex; align-items:center; gap:6px; }
.select-label { font-size:12px; color:var(--text-muted,#a0aec0); white-space:nowrap; }
.chart-point-label { font-size:12px; color:var(--text-muted,#a0aec0); font-family:monospace; }
.select-bar-spacer { flex:1; }
.status-line { display:flex; align-items:center; gap:6px; font-size:12px; color:var(--text-muted,#a0aec0); white-space:nowrap; }
.status-dot { width:8px; height:8px; border-radius:50%; display:inline-block; }
.status-dot.online { background:#3fb950; } .status-dot.offline { background:#d29922; }

/* 左右分栏 */
.main-split { flex:1; display:flex; gap:14px; min-height:0; }
.left-panel { flex:0 0 420px; min-width:300px; display:flex; flex-direction:column; overflow:hidden; }
.right-panel { flex:1; min-width:0; display:flex; flex-direction:column; overflow:hidden; }

.panel-head {
  display:flex; align-items:center; justify-content:space-between;
  padding:10px 16px; border-bottom:1px solid var(--border,#e4e7ed); flex-shrink:0;
  font-size:13px; font-weight:600; color:var(--text-heading,#1a202c);
}
.panel-meta { font-size:11px; font-weight:400; color:var(--text-muted,#a0aec0); font-family:monospace; }

.left-body { flex:1; overflow-y:auto; padding:12px; display:flex; flex-direction:column; gap:12px; min-height:0; }

/* 左侧设备卡片 */
.device-card {
  background:var(--bg-card,#fff); border:1px solid var(--border,#e4e7ed);
  border-radius:var(--radius,8px); overflow:hidden; flex-shrink:0;
  transition:box-shadow .2s,border-color .2s;
  max-height:360px; display:flex; flex-direction:column;
}
.device-card:hover { box-shadow:0 2px 8px rgba(0,0,0,.06); }
.card-online { border-left:3px solid #3fb950; }
.card-offline { border-left:3px solid #f85149; }

/* 卡片头部 */
.card-head { display:flex; justify-content:space-between; align-items:center; padding:10px 14px; border-bottom:1px solid var(--border,#e4e7ed); flex-shrink:0; }
.card-head-left { display:flex; align-items:center; gap:8px; min-width:0; }
.card-name { font-weight:600; font-size:14px; color:var(--text-heading,#1a202c); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.card-meta { font-size:11px; color:var(--text-muted,#a0aec0); font-family:monospace; white-space:nowrap; }

/* 点位行（点击看曲线） */
.point-rows { flex:1; overflow-y:auto; padding:2px 0; }
.point-row {
  display:grid; grid-template-columns:1fr auto auto auto;
  align-items:center; gap:8px; padding:6px 14px;
  border-bottom:1px solid var(--border,#eee); cursor:pointer;
  transition:background .15s;
}
.point-row:hover { background:#f5f7fa; }
.point-row.plotting { background:rgba(64,158,255,.08); box-shadow:inset 2px 0 0 #409eff; }
.point-row:last-child { border-bottom:none; }
.point-name { font-size:12px; color:var(--text-heading,#1a202c); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.point-value { font-size:14px; font-weight:700; color:var(--accent,#409eff); font-variant-numeric:tabular-nums; text-align:right; min-width:50px; }
.point-value.stale { color:var(--text-muted,#bbb); }

/* 写功能（docs/14）：行尾「写值」按钮与就地气泡编辑器 */
.write-btn { padding:0 4px; min-width:0; font-size:12px; }
.write-editor { display:flex; flex-direction:column; gap:10px; }
.write-title { display:flex; align-items:center; gap:8px; justify-content:space-between; }
.write-name { font-weight:600; font-size:13px; color:var(--text-heading,#1a202c); }
.write-meta { font-size:11px; color:var(--text-muted,#a0aec0); font-family:monospace; }
.write-actions { display:flex; justify-content:flex-end; gap:8px; margin-top:2px; }

.card-empty { padding:20px; text-align:center; color:var(--text-muted,#a0aec0); font-size:12px; }

/* 右侧曲线（ADR-055 缺口1） */
.chart-body { flex:1; position:relative; min-height:0; }
.chart-canvas { position:absolute; inset:0; }
.chart-empty {
  position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
  color:var(--text-muted,#a0aec0); font-size:13px; pointer-events:none; padding:0 20px; text-align:center;
}

/* 窄屏：左右分栏改为上下堆叠 */
@media (max-width: 1100px) {
  .main-split { flex-direction:column; }
  .left-panel { flex:0 0 auto; max-height:42%; }
  .right-panel { flex:1; min-height:300px; }
}
</style>
