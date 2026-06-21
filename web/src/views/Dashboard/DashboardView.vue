<template>
  <h2 class="page-title">仪表盘</h2>
  <div class="stats-grid">
    <div class="stat-card"><div class="value" style="color:#539bf5">{{ devices.length }}</div><div class="label">设备总数</div></div>
    <div class="stat-card"><div class="value" style="color:#3fb950">{{ online }}</div><div class="label">在线设备</div></div>
    <div class="stat-card"><div class="value" style="color:#f85149">{{ offline }}</div><div class="label">离线/故障</div></div>
    <div class="stat-card"><div class="value" style="color:#a371f7">{{ totalPoints }}</div><div class="label">总点位数</div></div>
  </div>
  <div class="card" style="margin-top:20px">
    <div class="card-header">设备概览</div>
    <el-table :data="devices" row-key="id">
      <el-table-column prop="name" label="名称" />
      <el-table-column label="协议" width="100"><template #default="{row}">{{ row.protocol.name }}{{ row.protocol.dialect ? '/'+row.protocol.dialect : '' }}</template></el-table-column>
      <el-table-column prop="connection.endpoint" label="连接地址" width="200" />
      <el-table-column label="状态" width="120"><template #default="{row}"><StatusTag :status="row.status" /></template></el-table-column>
      <el-table-column label="点位" width="60"><template #default="{row}">{{ row.points?.length ?? 0 }}</template></el-table-column>
      <el-table-column label="" width="80"><template #default="{row}"><el-button size="small" text @click="$router.push(`/devices/${row.id}`)">详情 →</el-button></template></el-table-column>
    </el-table>
  </div>
</template>
<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getDevices } from '../../api/devices'
import type { Device } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'
const devices = ref<Device[]>([])
const online = computed(() => devices.value.filter(d=>d.status==='Online').length)
const offline = computed(() => devices.value.filter(d=>d.status==='Offline'||d.status==='Error').length)
const totalPoints = computed(() => devices.value.reduce((s,d)=>s+(d.points?.length??0), 0))
onMounted(async () => { try { devices.value = await getDevices() } catch {} })
</script>
<style scoped>
.page-title { margin-bottom:24px; }
.stats-grid { display:grid; grid-template-columns:repeat(4,1fr); gap:16px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.card-header { padding:14px 20px; border-bottom:1px solid var(--border); color:var(--text-heading); font-weight:600; font-size:14px; }
</style>
