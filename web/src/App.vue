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
      </nav>
      <div class="sidebar-footer">
        <div class="version-tag">v1.0.0</div>
      </div>
    </aside>
    <main class="main-area">
      <header class="topbar">
        <div class="topbar-title">NitroGateway 管理控制台</div>
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
</div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getSystemStatus } from './api/status'
const mqttConnected = ref(false); const backlog = ref(0)
onMounted(async () => { try { const s = await getSystemStatus(); mqttConnected.value = s.mqttConnected; backlog.value = s.bufferBacklog } catch {} })
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
