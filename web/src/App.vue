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
        <div class="nav-group">
          <div class="nav-group-title" role="button" @click="deviceOpen = !deviceOpen" :title="deviceOpen ? '收起设备管理' : '展开设备管理'">
            <span class="nav-icon">🔌</span><span>设备管理</span>
            <span class="nav-group-caret" :class="{ collapsed: !deviceOpen }">▾</span>
          </div>
          <div v-show="deviceOpen" class="nav-group-body">
            <router-link to="/devices" class="nav-item nav-sub" active-class="nav-active">
              <span>Modbus / S7 设备</span>
            </router-link>
            <router-link to="/opcua" class="nav-item nav-sub" active-class="nav-active">
              <span>OPC UA 设备</span>
            </router-link>
          </div>
        </div>
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
        <router-link to="/audit" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🧾</span><span>操作日志</span>
        </router-link>
        <router-link v-if="currentUser?.role === 'Admin'" to="/users" class="nav-item" active-class="nav-active">
          <span class="nav-icon">👥</span><span>用户管理</span>
        </router-link>
        <router-link to="/site" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🏷️</span><span>站点身份</span>
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
          <span :class="['status-dot', mqttDisabled ? 'offline' : (mqttConnected ? 'online' : 'offline')]"></span>
          <span>{{ mqttDisabled ? 'MQTT 已关闭' : (mqttConnected ? 'MQTT 已连接' : 'MQTT 未连接') }}</span>
          <span class="status-sep">|</span>
          <span>缓冲队列 {{ backlog }} 批</span>
        </div>
        <div class="topbar-user">
          <el-dropdown trigger="click">
            <span class="user-chip">
              <span class="user-icon">👤</span>
              <span>{{ currentUser?.username ?? '未登录' }}</span>
              <span v-if="currentUser" class="user-role">{{ currentUser.role }}</span>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item @click="pwdVisible = true">修改密码</el-dropdown-item>
                <el-dropdown-item divided @click="logout">退出登录</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </header>
      <div class="content-area">
        <router-view />
      </div>
    </main>
  </div>

  <el-dialog v-model="pwdVisible" title="修改密码" width="420">
    <el-form label-width="80px">
      <el-form-item label="当前密码">
        <el-input v-model="pwdForm.current" type="password" show-password />
      </el-form-item>
      <el-form-item label="新密码">
        <el-input v-model="pwdForm.next" type="password" show-password :placeholder="`至少 ${pwdMin} 位`" />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="pwdVisible = false">取消</el-button>
      <el-button type="primary" :loading="pwdLoading" @click="submitPassword">确定</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getSystemStatus } from './api/status'
import { createLiveConnection } from './api/signalr'
import { getMe, saveMe, clearMe, changeMyPassword, type CurrentUser } from './api/user'
import type { HubConnection } from '@microsoft/signalr'

const route = useRoute()
const mqttConnected = ref(false)
const mqttDisabled = ref(false)
const backlog = ref(0)

let conn: HubConnection | null = null

const currentUser = ref<CurrentUser | null>(null)
const pwdVisible = ref(false)
const pwdForm = ref({ current: '', next: '' })
const pwdLoading = ref(false)
const pwdMin = 8

// 设备管理分组可收缩；落在 /devices* 或 /opcua* 时自动展开（深链/守卫跳转后不把当前项藏起来）
const deviceOpen = ref(true)
watch(() => route.path, (p) => {
  if (p === '/devices' || p.startsWith('/devices/') || p === '/opcua' || p.startsWith('/opcua/')) {
    deviceOpen.value = true
  }
})

// 登录后/刷新时拉取自己的用户信息并缓存（角色变更后重进页面即生效）
async function refreshMe() {
  try {
    const me = await getMe()
    if (me) {
      currentUser.value = me
      saveMe(me)
    }
  } catch { /* 401 由拦截器跳登录，其余静默 */ }
}

function logout() {
  clearMe()
  localStorage.removeItem('token')
  window.location.href = '/login'
}

async function submitPassword() {
  if (pwdForm.value.next.length < pwdMin) {
    ElMessage.warning(`新密码不能少于 ${pwdMin} 位`)
    return
  }
  pwdLoading.value = true
  try {
    await changeMyPassword(pwdForm.value.current, pwdForm.value.next)
    ElMessage.success('密码已修改')
    pwdVisible.value = false
    pwdForm.value = { current: '', next: '' }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error?.message ?? '修改失败')
  } finally {
    pwdLoading.value = false
  }
}

// 原监听静默失效；改为周期性轮询 /status/system 刷新积压数
let statusTimer: number | undefined

async function refreshStatus() {
  try {
    const s = await getSystemStatus()
    applyMqttState(s.mqttState)
    backlog.value = s.bufferBacklog
  } catch { /* 忽略，下次轮询重试 */ }
}

function applyMqttState(state?: string) {
  mqttDisabled.value = state === 'Disabled'
  mqttConnected.value = state === 'Connected'
}

onMounted(async () => {
  await refreshMe()
  await refreshStatus()
  statusTimer = window.setInterval(refreshStatus, 10000)

  // 建立 SignalR
  conn = createLiveConnection()

  conn.on('MqttStateChanged', (d: { state: string }) => {
    applyMqttState(d.state)
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
.nav-group { display:flex; flex-direction:column; gap:1px; margin:2px 0 4px; }
.nav-group-title { display:flex; align-items:center; gap:10px; padding:9px 14px; color:#4a5568; font-size:13px; font-weight:600; cursor:pointer; border-radius:8px; user-select:none; transition:background .15s; }
.nav-group-title:hover { background:#f0f2f5; color:#1a202c; }
.nav-group-caret { margin-left:auto; font-size:11px; color:#a0aec0; transition:transform .15s; }
.nav-group-caret.collapsed { transform:rotate(-90deg); }
.nav-group-body { display:flex; flex-direction:column; gap:1px; }
.nav-item.nav-sub { padding:8px 14px 8px 42px; font-size:13px; color:#718096; border-left:2px solid transparent; margin-left:8px; }
.nav-item.nav-sub:hover { color:#1a202c; }
.nav-item.nav-sub.nav-active { border-left-color:#409eff; }
.sidebar-footer { padding:16px 20px; border-top:1px solid #eef0f4; }
.version-tag { display:inline-block; padding:2px 10px; background:#f5f7fa; border:1px solid #e4e7ed; border-radius:12px; color:#a0aec0; font-size:11px; }
.main-area { flex:1; display:flex; flex-direction:column; overflow:hidden; }
.topbar { height:52px; background:#fff; border-bottom:1px solid #e4e7ed; display:flex; align-items:center; justify-content:space-between; padding:0 28px; flex-shrink:0; box-shadow:0 1px 2px rgba(0,0,0,.03); }
.topbar-title { color:#1a202c; font-weight:600; font-size:14px; }
.topbar-status { color:#a0aec0; font-size:12px; display:flex; align-items:center; gap:8px; }
.topbar-user { color:#4a5568; font-size:13px; }
.user-chip { display:flex; align-items:center; gap:6px; cursor:pointer; padding:4px 8px; border-radius:6px; }
.user-chip:hover { background:#f0f2f5; }
.user-icon { font-size:16px; }
.user-role { background:#ecf5ff; color:#409eff; border-radius:4px; padding:1px 6px; font-size:11px; }
.status-dot { width:8px; height:8px; border-radius:50%; } .status-dot.online { background:#67c23a; } .status-dot.offline { background:#e6a23c; }
.status-sep { color:#e4e7ed; }
.content-area { flex:1; overflow-y:auto; padding:28px; }
</style>
