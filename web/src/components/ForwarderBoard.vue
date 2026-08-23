<template>
  <div class="card" style="margin-top:20px">
    <div class="fb-head">
      <h3 style="margin:0">转发看板（断点续传证据）</h3>
      <div class="fb-badges">
        <el-tag size="small" :type="forwardEnabled ? 'success' : 'info'">
          {{ forwardEnabled ? '转发开启' : '转发暂停' }}
        </el-tag>
        <el-tag size="small" :type="mqttTag()">{{ mqttLabel() }}</el-tag>
        <el-tag size="small" :type="backlog > 0 ? 'warning' : 'success'">
          当前积压 {{ backlog }} 批
        </el-tag>
      </div>
    </div>
    <div ref="chartRef" style="height:200px;margin-top:12px"></div>
    <div class="fb-note">
      ADR-001 断点续传：MQTT 断开时数据留在本地 outbox（水位上升），重连后按序补传直至水位归零。
      下方事件流由前端每 3s 采样水位变化推导，展示「断网→堆积→续传→清空」全过程。
    </div>
    <div class="fb-events" v-if="events.length">
      <div class="fb-event" v-for="(e, i) in events" :key="i">
        <span class="fb-time">{{ e.time }}</span>
        <span class="fb-arrow" :style="{ color: e.color }">{{ e.arrow }}</span>
        <span>{{ e.text }}</span>
      </div>
    </div>
    <div v-else class="fb-events fb-empty">正在采样水位数据…</div>
  </div>
</template>

<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted, nextTick } from 'vue'
// ADR-007 P3-5：按需引入 echarts（同 HistoryView 模式）
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, TitleComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
echarts.use([LineChart, GridComponent, TooltipComponent, TitleComponent, CanvasRenderer])

const props = defineProps<{
  backlog: number
  mqttState: string
  forwardEnabled: boolean
}>()

/// 水位采样点（保留最近 ~7.5 分钟：3s 采样 × 150 点）
const samples = ref<{ time: number; value: number }[]>([])
/// 续传事件流（前端推导，最近 30 条）
const events = ref<{ time: string; arrow: string; color: string; text: string }[]>([])
const chartRef = ref<HTMLElement>()
let chart: ReturnType<typeof echarts.init> | null = null

function pushEvent(arrow: string, color: string, text: string) {
  const time = new Date().toLocaleTimeString()
  events.value.unshift({ time, arrow, color, text })
  if (events.value.length > 30) events.value.pop()
}

watch(() => props.backlog, (curr, prev) => {
  samples.value.push({ time: Date.now(), value: curr })
  if (samples.value.length > 150) samples.value.shift()

  // 水位变化 → 推导续传事件（prev 缺省为 0，首帧不产生「清空」噪音）
  const p = prev ?? 0
  if (p === 0 && curr > 0) pushEvent('▲', '#e6a23c', `积压开始堆积 ${curr} 批（转发暂停/断网）`)
  else if (curr === 0 && p > 0) pushEvent('▼', '#67c23a', '积压清空，断点续传完成')
  else if (curr > p) pushEvent('▲', '#e6a23c', `积压 ${p} → ${curr} 批（数据持续进入）`)
  else if (curr < p) pushEvent('▼', '#67c23a', `水位下降 ${p} → ${curr} 批（出队续传中）`)

  renderChart()
}, { immediate: true })

watch(() => props.mqttState, (curr, prev) => {
  if (prev === undefined || prev === curr) return
  const map: Record<string, string> = {
    Connected: 'MQTT 已连接',
    Connecting: 'MQTT 连接中',
    Reconnecting: 'MQTT 重连中',
    Disconnected: 'MQTT 已断开',
    Faulted: 'MQTT 故障',
    Disabled: 'MQTT 转发已关闭'
  }
  const color = curr === 'Connected' ? '#67c23a' : '#e6a23c'
  pushEvent('◆', color, map[curr] ?? `MQTT 状态 → ${curr}`)
}, { immediate: false })

watch(() => props.forwardEnabled, (curr, prev) => {
  if (prev === undefined || prev === curr) return
  pushEvent('◆', '#409eff', curr ? '已开启 MQTT 上云转发' : '已暂停 MQTT 上云转发（数据留本地）')
})

function mqttLabel(): string {
  const map: Record<string, string> = {
    Connected: '已连接', Connecting: '连接中', Reconnecting: '重连中',
    Disconnected: '未连接', Faulted: '故障', Disabled: '已关闭'
  }
  return map[props.mqttState] ?? props.mqttState ?? '-'
}
function mqttTag(): string {
  const s = props.mqttState
  if (s === 'Connected') return 'success'
  if (s === 'Disabled' || s === 'Connecting' || s === 'Reconnecting') return 'warning'
  return 'danger'
}

function renderChart() {
  if (!chartRef.value) return
  if (!chart) chart = echarts.init(chartRef.value)
  chart.setOption({
    title: { text: '转发缓冲水位（outbox 积压）', textStyle: { color: '#4a5568', fontSize: 13 } },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'time', axisLabel: { color: '#a0aec0' } },
    yAxis: { type: 'value', minInterval: 1, axisLabel: { color: '#a0aec0' } },
    series: [{
      data: samples.value.map(s => [s.time, s.value]),
      type: 'line',
      showSymbol: false,
      step: 'end',
      lineStyle: { color: '#409eff', width: 2 },
      areaStyle: {
        color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
          colorStops: [{ offset: 0, color: 'rgba(64,158,255,.15)' }, { offset: 1, color: 'rgba(64,158,255,0)' }] }
      }
    }],
    grid: { left: 50, right: 20, top: 40, bottom: 30 }
  })
}

onMounted(async () => {
  await nextTick()
  renderChart()
  // 监听容器尺寸变化，自适应
  if (typeof ResizeObserver !== 'undefined' && chartRef.value) {
    const ro = new ResizeObserver(() => chart?.resize())
    ro.observe(chartRef.value)
  }
})
onUnmounted(() => chart?.dispose())
</script>

<style scoped>
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
.fb-head { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:10px; }
.fb-badges { display:flex; gap:8px; }
.fb-note { margin-top:10px; font-size:12px; color:var(--text-dim,#909399); line-height:1.6; }
.fb-events { margin-top:12px; border-top:1px solid var(--border); padding-top:10px; max-height:180px; overflow-y:auto; }
.fb-event { font-size:12px; color:var(--text-dim,#4a5568); padding:3px 0; display:flex; gap:8px; }
.fb-time { color:#a0aec0; width:74px; flex-shrink:0; }
.fb-arrow { width:14px; text-align:center; flex-shrink:0; font-weight:700; }
.fb-empty { color:#a0aec0; }
</style>
