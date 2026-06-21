<template>
  <h2 class="page-title">历史数据</h2>
  <div class="card" style="padding:20px;margin-bottom:16px">
    <div class="query-bar">
      <el-select v-model="q.deviceId" @change="loadPts" placeholder="选择设备" style="width:200px"><el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" /></el-select>
      <el-select v-model="q.pointId" placeholder="选择点位" style="width:220px"><el-option v-for="p in ptOptions" :key="p.id" :label="`${p.name} (${p.address})`" :value="p.id" /></el-select>
      <el-date-picker v-model="range" type="datetimerange" range-separator="至" start-placeholder="开始" end-placeholder="结束" style="width:340px" />
      <el-button type="primary" @click="search">查询</el-button>
    </div>
  </div>
  <div v-if="chartData.length" class="card" style="padding:20px;margin-bottom:16px">
    <div ref="chartRef" style="height:350px"></div>
  </div>
  <div v-if="history.length" class="card">
    <el-table :data="history" row-key="devicePointId" size="small" max-height="320">
      <el-table-column prop="timestamp" label="时间" width="200"><template #default="{row}">{{ new Date(row.timestamp).toLocaleString() }}</template></el-table-column>
      <el-table-column label="值"><template #default="{row}">{{ formatVal(row.value) }}</template></el-table-column>
      <el-table-column label="质量" width="80"><template #default="{row}"><el-tag :type="row.quality==='Good'?'success':'danger'" size="small">{{ row.quality }}</el-tag></template></el-table-column>
    </el-table>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted, nextTick } from 'vue'
import * as echarts from 'echarts'
import { getDevices, getPoints } from '../../api/devices'
import { getHistory } from '../../api/measurements'
import type { Device, DevicePoint, PointSnapshot } from '../../api/types'
const devices = ref<Device[]>([]); const ptOptions = ref<DevicePoint[]>([]); const history = ref<PointSnapshot[]>([]); const chartData = ref<{time:string;value:number}[]>([]); const chartRef = ref<HTMLElement>()
const q = ref({deviceId:'',pointId:''}); const range = ref<[Date,Date]>([new Date(Date.now()-3600000), new Date()])
onMounted(async () => { try { devices.value = await getDevices() } catch {} })
async function loadPts() { if(q.value.deviceId) try { ptOptions.value = await getPoints(q.value.deviceId) } catch {} }
async function search() {
  if(!q.value.deviceId||!q.value.pointId) return
  const from=range.value[0].toISOString(); const to=range.value[1].toISOString()
  try { history.value = await getHistory(q.value.deviceId,q.value.pointId,from,to); chartData.value = history.value.map(s=>({time:s.timestamp,value:s.value??0})) } catch {}
  await nextTick()
  if(chartRef.value&&chartData.value.length) {
    const c=echarts.init(chartRef.value)
    c.setOption({title:{text:'时序趋势',textStyle:{color:'#4a5568',fontSize:14}},tooltip:{trigger:'axis'},xAxis:{type:'time',axisLabel:{color:'#a0aec0'}},yAxis:{type:'value',axisLabel:{color:'#a0aec0'}},series:[{data:chartData.value.map(p=>[p.time,p.value]),type:'line',smooth:true,showSymbol:false,lineStyle:{color:'#409eff',width:2},areaStyle:{color:{type:'linear',x:0,y:0,x2:0,y2:1,colorStops:[{offset:0,color:'rgba(64,158,255,.15)'},{offset:1,color:'rgba(64,158,255,0)'}]}}}],grid:{left:50,right:20,top:40,bottom:30}})
  }
}
function formatVal(v:unknown):string { if(typeof v==='number') return v.toFixed(3); return String(v??'--') }
</script>
<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.query-bar { display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
</style>
