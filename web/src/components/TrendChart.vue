<template><div ref="chartRef" style="width:100%;height:100%"></div></template>
<script setup lang="ts">
import { ref, onMounted, watch } from 'vue'
import * as echarts from 'echarts'
const props = defineProps<{ data: { time: string; value: number }[]; title?: string }>()
const chartRef = ref<HTMLElement>()
onMounted(() => {
  const c = echarts.init(chartRef.value!)
  watch(() => props.data, d => { c.setOption({ title: { text: props.title, textStyle: { color: '#4a5568', fontSize: 14 } }, tooltip: { trigger: 'axis' }, xAxis: { type: 'time', axisLabel: { color: '#a0aec0' } }, yAxis: { type: 'value', axisLabel: { color: '#a0aec0' } }, series: [{ data: d.map(p => [p.time, p.value]), type: 'line', smooth: true, showSymbol: false, lineStyle: { color: '#409eff', width: 2 }, areaStyle: { color: { type: 'linear', x: 0, y: 0, x2: 0, y2: 1, colorStops: [{ offset: 0, color: 'rgba(64,158,255,.12)' }, { offset: 1, color: 'rgba(64,158,255,0)' }] } } }], grid: { left: 50, right: 20, top: 40, bottom: 30 } }, false) }, { immediate: true })
  window.addEventListener('resize', () => c.resize())
})
</script>
