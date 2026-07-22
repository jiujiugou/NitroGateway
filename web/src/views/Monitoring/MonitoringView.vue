<template>
  <h2 class="page-title">实时监控</h2>
  <div class="card" style="padding:20px;margin-bottom:16px">
    <el-select v-model="selDevice" @change="subscribe" placeholder="选择设备" style="width:260px"><el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" /></el-select>
    <span style="margin-left:16px;font-size:12px"><span :class="['status-dot',connected?'online':'offline']"></span> {{ connected ? 'SignalR 已连接' : '未连接' }}</span>
  </div>
  <div v-if="snapshots.length" class="points-grid">
    <div v-for="s in snapshots" :key="s.devicePointId" class="data-card">
      <div class="point-name">{{ s.devicePointId.substring(0,8) }}...</div>
      <div class="point-value">{{ formatValue(s.value) }}</div>
      <div class="point-quality"><el-tag :type="s.quality==='Good'?'success':s.quality==='Uncertain'?'warning':'danger'" size="small">{{ s.quality }}</el-tag></div>
      <div style="color:var(--text-muted);font-size:10px;margin-top:6px">{{ new Date(s.timestamp).toLocaleTimeString() }}</div>
    </div>
  </div>
  <div v-else class="card" style="padding:40px;text-align:center;color:var(--text-muted)">选择设备后开始接收实时数据</div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getDevices } from '../../api/devices'
import { createLiveConnection } from '../../api/signalr'
import type { Device, PointSnapshot } from '../../api/types'
const devices = ref<Device[]>([]); const selDevice = ref(''); const snapshots = ref<any[]>([]); const connected = ref(false)
let conn: any = null

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}
  conn = createLiveConnection()
  conn.on('Measurement', (s: any) => {
    snapshots.value = Array.isArray(s) ? s : [s]
  })
  conn.onreconnected(() => { connected.value = true; console.log('重连') })
  conn.onclose(() => { connected.value = false; console.log('断开') })
  try {
    await conn.start()
    connected.value = true
    console.log('SignalR 已连接')
  } catch (e) {
    console.error('SignalR 连接失败:', e)
    connected.value = false
  }
})

async function subscribe() {
  if (!selDevice.value || !conn) return
  try {
    await conn.invoke('SubscribeDevice', selDevice.value)
    console.log('已订阅设备:', selDevice.value)
  } catch (e) {
    console.error('订阅失败:', e)
  }
}

function formatValue(v:unknown):string {
  if (typeof v==='number') return v.toFixed(2)
  if (typeof v==='boolean') return v?'ON':'OFF'
  return String(v??'--')
}
</script>
<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); }
.status-dot { width:8px; height:8px; border-radius:50%; display:inline-block; margin-right:4px; }
.status-dot.online { background:#3fb950; } .status-dot.offline { background:#d29922; }
.points-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(200px,1fr)); gap:12px; }
.data-card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); padding:20px; text-align:center; }
.point-name { font-size:11px; color:var(--text-muted); text-transform:uppercase; letter-spacing:.4px; margin-bottom:8px; }
.point-value { font-size:1.8rem; font-weight:700; color:var(--accent); }
.point-quality { margin-top:8px; }
</style>
