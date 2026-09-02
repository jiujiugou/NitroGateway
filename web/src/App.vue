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
        <!-- ADR-073 D8：OPC UA 证书信任管理（仅 Admin/Operator 可见；后端 AdminOperator 策略兜底） -->
        <router-link v-if="canManageCertificates" to="/opcua/certificates" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🔐</span><span>OPC UA 证书</span>
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
        <!-- ADR-065 A3：操作日志查询页（写值/登录/配置变更可追溯） -->
        <router-link to="/audit" class="nav-item" active-class="nav-active">
          <span class="nav-icon">🧾</span><span>操作日志</span>
        </router-link>
        <!-- ADR-066：用户管理（仅 Admin 可见；后端 AdminOnly 策略兜底） -->
        <router-link v-if="currentUser?.role === 'Admin'" to="/users" class="nav-item" active-class="nav-active">
          <span class="nav-icon">👥</span><span>用户管理</span>
        </router-link>
        <!-- ADR-036：站点身份管理（查看/修改/重新生成，与桌面设置页对齐） -->
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
          <!-- ADR-061：转发开关关闭时明确显示「MQTT 已关闭」，不误导为故障/未连接 -->
          <span :class="['status-dot', mqttDisabled ? 'offline' : (mqttConnected ? 'online' : 'offline')]"></span>
          <span>{{ mqttDisabled ? 'MQTT 已关闭' : (mqttConnected ? 'MQTT 已连接' : 'MQTT 未连接') }}</span>
          <span class="status-sep">|</span>
          <span>缓冲队列 {{ backlog }} 批</span>
        </div>
        <!-- ADR-066：当前登录用户（角色/自助改密/退出登录） -->
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

  <!-- 自助改密（任何已登录角色；ADR-066） -->
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
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import { getSystemStatus } from './api/status'
import { createLiveConnection } from './api/signalr'
import { getMe, saveMe, clearMe, changeMyPassword, type CurrentUser } from './api/user'
import type { HubConnection } from '@microsoft/signalr'

const mqttConnected = ref(false)
const mqttDisabled = ref(false)
const backlog = ref(0)

let conn: HubConnection | null = null

// ADR-066：当前登录用户（顶部栏显示 + 侧边栏菜单门控）
const currentUser = ref<CurrentUser | null>(null)
// ADR-073 D8：证书信任面板仅对可写角色（Admin/Operator）展示（后端 AdminOperator 策略兜底）
const canManageCertificates = computed(() => currentUser.value != null && ['Admin', 'Operator'].includes(currentUser.value.role))
const pwdVisible = ref(false)
const pwdForm = ref({ current: '', next: '' })
const pwdLoading = ref(false)
const pwdMin = 8

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

// ADR-007 P1-3：后端 SignalR 无 BufferBacklogChanged 事件（仅 Measurement/DeviceStatusChanged/MqttStateChanged），
// 原监听静默失效；改为周期性轮询 /status/system 刷新积压数
let statusTimer: number | undefined

async function refreshStatus() {
  try {
    const s = await getSystemStatus()
    applyMqttState(s.mqttState)
    backlog.value = s.bufferBacklog
  } catch { /* 忽略，下次轮询重试 */ }
}

// ADR-061：统一收敛 MQTT 状态 → 连接/关闭两个布尔（Disabled 与 Connected 互斥）
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
