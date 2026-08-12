<template>
  <div class="page-head">
    <h2 class="page-title">设备管理</h2>
    <div class="toolbar">
      <!-- ADR-035 方案 A：设备按站点归属，可按现场过滤 -->
      <SiteFilter v-model="siteId" @update:model-value="load" />
    </div>
    <el-button type="primary" @click="$router.push('/devices/new')">+ 添加设备</el-button>
  </div>
  <div class="card">
    <el-table :data="devices" row-key="id" @row-click="(r:Device) => $router.push(`/devices/${r.id}`)" style="cursor:pointer">
      <el-table-column prop="name" label="名称" />
      <el-table-column label="站点" width="130"><template #default="{row}"><el-tag v-if="row.siteId" size="small" type="info">{{ row.siteId }}</el-tag><span v-else style="color:var(--text-muted)">未标注</span></template></el-table-column>
      <el-table-column label="协议" width="120"><template #default="{row}">{{ row.protocol.name }}{{ row.protocol.dialect ? ' / '+row.protocol.dialect : '' }}</template></el-table-column>
      <el-table-column prop="connection.endpoint" label="连接地址" width="210" />
      <el-table-column label="状态" width="110"><template #default="{row}"><StatusTag :status="row.status" /></template></el-table-column>
      <el-table-column label="操作" width="200"><template #default="{row}">
        <el-button size="small" text type="primary" @click.stop="$router.push(`/devices/${row.id}`)">详情</el-button>
        <el-button size="small" text @click.stop="$router.push(`/devices/${row.id}/edit`)">编辑</el-button>
        <el-button size="small" text type="danger" @click.stop="handleDel(row.id)">删除</el-button>
      </template></el-table-column>
    </el-table>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessageBox } from 'element-plus'
import { getDevices, deleteDevice } from '../../api/devices'
import type { Device } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'
import SiteFilter from '../../components/SiteFilter.vue'
const devices = ref<Device[]>([])
const siteId = ref('')
async function load() { try { devices.value = await getDevices(siteId.value) } catch {} }
onMounted(load)
async function handleDel(id: string) {
  // ADR-007 P3-3：删除为破坏性操作，先弹确认框（取消时 promise reject，静默返回）
  try { await ElMessageBox.confirm('确定删除该设备吗？此操作不可恢复。', '删除确认', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }) }
  catch { return }
  await deleteDevice(id)
  devices.value = devices.value.filter(d=>d.id!==id)
}
</script>
<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.toolbar { display:flex; margin-bottom:12px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
</style>

