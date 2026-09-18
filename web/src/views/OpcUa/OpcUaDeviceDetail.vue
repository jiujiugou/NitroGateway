<template>
  <div v-if="device" class="page-head"><h2 class="page-title">{{ device.name }}</h2>
    <div style="display:flex;gap:8px">
      <el-button @click="$router.push(`/opcua/${device.id}/edit`)">编辑</el-button>
      <el-button @click="$router.push(`/opcua/${device.id}/points`)">管理点位</el-button>
      <!-- ADR-073 D8：证书信任管理（首次连接未信任 → rejected → 信任后重连；仅 Admin/Operator） -->
      <el-button v-if="canManageCert" @click="$router.push('/opcua/certificates')">🔐 证书信任</el-button>
    </div>
  </div>
  <div v-if="device" class="card" style="margin-bottom:16px">
    <div class="card-header">连接与安全</div>
    <div style="padding:20px;display:grid;grid-template-columns:repeat(3,1fr);gap:20px;font-size:13px">
      <div><span class="meta-label">ID</span><div class="meta-value" style="font-family:monospace;font-size:12px">{{ device.id }}</div></div>
      <div><span class="meta-label">状态</span><div class="meta-value"><StatusTag :status="device.status" /></div></div>
      <div><span class="meta-label">连接端点</span><div class="meta-value" style="font-family:monospace;font-size:12px">{{ device.connection.endpoint }}</div></div>
      <div><span class="meta-label">安全策略</span><div class="meta-value">{{ securityPolicy }}</div></div>
      <div><span class="meta-label">安全模式</span><div class="meta-value">{{ securityMode }}</div></div>
      <div><span class="meta-label">身份</span><div class="meta-value">{{ userName }}</div></div>
      <div><span class="meta-label">密码</span><div class="meta-value">{{ device.connection.hasPassword ? '已设置（密文不回显）' : '未设置（匿名）' }}</div></div>
      <div><span class="meta-label">连接/请求超时</span><div class="meta-value">{{ device.connection.connectTimeoutMs }}ms / {{ device.connection.requestTimeoutMs }}ms</div></div>
      <div><span class="meta-label">采集方式</span><div class="meta-value">订阅优先，回退轮询（ADR-071/072 会话自愈）</div></div>
    </div>
  </div>
  <div v-if="device" class="card">
    <div class="card-header">点位列表 ({{ device.points?.length ?? 0 }})</div>
    <el-table :data="device.points" row-key="id" size="small">
      <el-table-column prop="name" label="名称" /><el-table-column prop="address" label="地址" width="200" />
      <el-table-column prop="dataType" label="类型" width="80" /><el-table-column prop="access" label="权限" width="80" />
      <el-table-column label="启用" width="60"><template #default="{row}"><el-switch :model-value="row.enabled" disabled size="small" /></template></el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getDevice } from '../../api/devices'
import { loadMe } from '../../api/user'
import type { Device } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'

const route = useRoute(); const router = useRouter()
const device = ref<Device|null>(null)
const securityPolicy = ref('自动（默认安全优先）')
const securityMode = ref('自动')
const userName = ref('匿名')
// ADR-073 D8：证书信任仅对可写角色（Admin/Operator）展示（路由 meta.roles 兜底跳转）
const canManageCert = computed(() => {
  const me = loadMe()
  return me != null && ['Admin', 'Operator'].includes(me.role)
})

onMounted(async () => {
  try {
    const d = await getDevice(route.params.id as string)
    if (!d) return
    // 分区守卫：非 OPC UA 设备回到通用设备详情
    if (d.protocol.name !== 'OPC UA') { router.replace(`/devices/${d.id}`); return }
    device.value = d
    const p = d.connection?.parameters ?? {}
    if (p.SecurityPolicy) securityPolicy.value = String(p.SecurityPolicy)
    if (p.SecurityMode) securityMode.value = String(p.SecurityMode)
    if (p.UserName) userName.value = `${p.UserName}（用户名密码）`
  } catch {}
})
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.card-header { padding:14px 20px; border-bottom:1px solid var(--border); color:var(--text-heading); font-weight:600; font-size:14px; }
.meta-label { color:var(--text-muted); font-size:11px; text-transform:uppercase; letter-spacing:.5px; }
.meta-value { color:var(--text-heading); margin-top:4px; }
</style>
