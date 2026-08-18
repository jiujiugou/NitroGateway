<template>
  <router-view v-if="$route.path === '/login'" />
  <div v-else class="app-layout">
    <aside class="sidebar">
      <div class="sidebar-brand">
        <div class="brand-icon">⚡</div>
        <div class="brand-text">
          <div class="brand-name">NitroGateway</div>
          <div class="brand-sub">工业协议网关</div>
        </div>
      </div>
      <nav class="sidebar-nav">
        <router-link to="/dashboard" class="nav-item" active-class="nav-active">
          <span class="nav-icon">📊</span><span>仪表盘</span>
        </router-link>
        <router-link to="/devices" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🔌</span><span>设备管理</span>
        </router-link>
        <router-link to="/monitoring" class="nav-item" active-class="nav-active">
          <span class="nav-icon">📡</span><span>实时监控</span>
        </router-link>
        <router-link to="/history" class="nav-item" active-class="nav-active">
          <span class="nav-icon">📈</span><span>历史数据</span>
        </router-link>
        <router-link to="/alarmrules" class="nav-item" active-class="nav-active">
          <span class="nav-icon">⚙️</span><span>告警规则</span>
        </router-link>
        <router-link to="/alarms" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🔔</span><span>告警记录</span>
        </router-link>
        <!-- ADR-044/054：死信是转发缓冲产物；web 恒为边缘形态（会转发），死信入口恒显示 -->
        <router-link to="/deadletters" class="nav-item" active-class="nav-active">
          <span class="nav-icon">📬</span><span>死信管理</span>
        </router-link>
        <router-link to="/system" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🖥️</span><span>系统状态</span>
        </router-link>
      </nav>
      <div class="sidebar-footer">
        <div class="version-tag">v1.0.0</div>
      </div>
    </aside>
    <main class="main-area">
      <header class="topbar">
        <div class="topbar-title">NitroGateway 管理控制台</div>
        <!-- ADR-044：Center 形态不采集/不转发/无 MQTT，隐藏转发侧状态，避免误导 -->
        <div class="topbar-status">
          <span :class="['status-dot', mqttConnected ? 'online' : 'offline']"></span>
          <span>{{ mqttConnected ? 'MQTT 已连接' : 'MQTT 未连接' }}</span>
          <span class="status-sep">|</span>
          <span>缓冲队列 {{ backlog }} 批</span>
        </div>
      </header>
      <div class="content-area">
        <router-view />
      </div>
    </main>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { getSystemStatus } from './api/status'
import { createLiveConnection } from './api/signalr'
import type { HubConnection } from '@microsoft/signalr'

const mqttConnected = ref(false)
const backlog = ref(0)

let conn: HubConnection | null = null

// ADR-007 P1-3：后端 SignalR 无 BufferBacklogChanged 事件（仅 Measurement/DeviceStatusChanged/MqttStateChanged），
// 原监听静默失效；改为周期性轮询 /status/system 刷新积压数
let statusTimer: number | undefined

async function refreshStatus() {
  try {
    const s = await getSystemStatus()
    mqttConnected.value = s.mqttConnected
    backlog.value = s.bufferBacklog
  } catch { /* 忽略，下次轮询重试 */ }
}

onMounted(async () => {
  await refreshStatus()
  statusTimer = window.setInterval(refreshStatus, 10000)

  // 建立 SignalR
  conn = createLiveConnection()

  conn.on('MqttStateChanged', (d: { state: string }) => {
    mqttConnected.value = d.state === 'Connected'
  })

  try {
    await conn.start()
  } catch (e) {
    console.warn('SignalR:', e)
  }
})

onUnmounted(() => {
  if (statusTimer !== undefined) window.clearInterval(statusTimer)
  conn?.stop()
})
</script>

<style scoped>
.app-layout { display:flex; height:100vh; overflow:hidden; }
.sidebar { width:240px; background:#fff; border-right:1px solid #e4e7ed; display:flex; flex-direction:column; flex-shrink:0; }
.sidebar-brand { padding:24px 20px 20px; display:flex; align-items:center; gap:12px; border-bottom:1px solid #eef0f4; }
.brand-icon { font-size:28px; }
.brand-name { color:#1a202c; font-size:15px; font-weight:700; }
.brand-sub { color:#a0aec0; font-size:11px; margin-top:1px; }
.sidebar-nav { flex:1; padding:12px 10px; display:flex; flex-direction:column; gap:2px; }
.nav-item { display:flex; align-items:center; gap:10px; padding:10px 14px; border-radius:8px; color:#4a5568; text-decoration:none; font-size:14px; transition:background .15s; }
.nav-item:hover { background:#f0f2f5; color:#1a202c; }
.nav-active { background:#ecf5ff!important; color:#409eff!important; }
.nav-icon { font-size:16px; width:22px; text-align:center; }
.sidebar-footer { padding:16px 20px; border-top:1px solid #eef0f4; }
.version-tag { display:inline-block; padding:2px 10px; background:#f5f7fa; border:1px solid #e4e7ed; border-radius:12px; color:#a0aec0; font-size:11px; }
.main-area { flex:1; display:flex; flex-direction:column; overflow:hidden; }
.topbar { height:52px; background:#fff; border-bottom:1px solid #e4e7ed; display:flex; align-items:center; justify-content:space-between; padding:0 28px; flex-shrink:0; box-shadow:0 1px 2px rgba(0,0,0,.03); }
.topbar-title { color:#1a202c; font-weight:600; font-size:14px; }
.topbar-status { color:#a0aec0; font-size:12px; display:flex; align-items:center; gap:8px; }
.status-dot { width:8px; height:8px; border-radius:50%; } .status-dot.online { background:#67c23a; } .status-dot.offline { background:#e6a23c; }
.status-sep { color:#e4e7ed; }
.content-area { flex:1; overflow-y:auto; padding:28px; }
</style>
