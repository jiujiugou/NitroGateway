<template>
  <div v-if="device" class="page-head"><h2 class="page-title">{{ device.name }}</h2>
    <div style="display:flex;gap:8px">
      <el-button @click="$router.push(`/devices/${device.id}/edit`)">编辑</el-button>
      <el-button @click="$router.push(`/devices/${device.id}/points`)">管理点位</el-button>
    </div>
  </div>
  <div v-if="device" class="card" style="margin-bottom:16px">
    <div class="card-header">基本信息</div>
    <div style="padding:20px;display:grid;grid-template-columns:repeat(3,1fr);gap:20px;font-size:13px">
      <div><span class="meta-label">ID</span><div class="meta-value" style="font-family:monospace;font-size:12px">{{ device.id }}</div></div>
      <div><span class="meta-label">协议</span><div class="meta-value">{{ device.protocol.name }}{{ device.protocol.dialect ? ' / '+device.protocol.dialect : '' }}</div></div>
      <div><span class="meta-label">状态</span><div class="meta-value"><StatusTag :status="device.status" /></div></div>
      <div><span class="meta-label">连接地址</span><div class="meta-value">{{ device.connection.endpoint }}</div></div>
      <div><span class="meta-label">超时</span><div class="meta-value">{{ device.connection.connectTimeoutMs }}ms / {{ device.connection.requestTimeoutMs }}ms</div></div>
      <div><span class="meta-label">重试</span><div class="meta-value">{{ device.connection.retryCount }} 次 × {{ device.connection.retryIntervalMs }}ms</div></div>
    </div>
  </div>
  <div v-if="device" class="card">
    <div class="card-header">点位列表 ({{ device.points?.length ?? 0 }})</div>
    <el-table :data="device.points" row-key="id" size="small">
      <el-table-column prop="name" label="名称" /><el-table-column prop="address" label="地址" width="140" />
      <el-table-column prop="dataType" label="类型" width="80" /><el-table-column prop="access" label="权限" width="80" />
      <el-table-column label="缩放" width="140"><template #default="{row}">×{{ row.scaleFactor }} +{{ row.scaleOffset }}</template></el-table-column>
      <el-table-column prop="deadband" label="死区" width="70" /><el-table-column label="启用" width="60"><template #default="{row}"><el-switch :model-value="row.enabled" disabled size="small" /></template></el-table-column>
    </el-table>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { getDevice } from '../../api/devices'
import type { Device } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'
const route = useRoute(); const device = ref<Device|null>(null)
onMounted(async () => { try { device.value = await getDevice(route.params.id as string) } catch {} })
</script>
<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.card-header { padding:14px 20px; border-bottom:1px solid var(--border); color:var(--text-heading); font-weight:600; font-size:14px; }
.meta-label { color:var(--text-muted); font-size:11px; text-transform:uppercase; letter-spacing:.5px; }
.meta-value { color:var(--text-heading); margin-top:4px; }
</style>
