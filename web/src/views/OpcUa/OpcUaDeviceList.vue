<template>
  <div class="page-head">
    <h2 class="page-title">OPC UA 设备</h2>
    <div class="page-actions">
      <!-- ADR-073 D8：证书信任管理（仅 Admin/Operator 可写角色可见；后端 AdminOperator 策略兜底） -->
      <el-button v-if="canManageCert" @click="$router.push('/opcua/certificates')">🔐 证书信任</el-button>
      <el-button type="primary" @click="$router.push('/opcua/new')">+ 添加 OPC UA 设备</el-button>
    </div>
  </div>
  <!-- ADR-073 D8：OPC UA 设备独立分区管理（安全/浏览/证书一套心智），不再混入通用 Modbus/S7 设备表。
       实时监控跨协议展示所有设备，不受本分区影响。 -->
  <div v-if="devices.length === 0" class="card empty-state">
    <div class="empty-text">暂无 OPC UA 设备</div>
    <el-button type="primary" @click="$router.push('/opcua/new')">+ 添加 OPC UA 设备</el-button>
  </div>
  <div v-else class="card">
    <el-table :data="devices" row-key="id" @row-click="(r:Device) => $router.push(`/opcua/${r.id}`)" style="cursor:pointer">
      <el-table-column prop="name" label="名称" />
      <el-table-column label="连接端点" width="260"><template #default="{row}">{{ row.connection.endpoint }}</template></el-table-column>
      <el-table-column label="安全档位" width="200">
        <template #default="{row}">{{ securitySummary(row) }}</template>
      </el-table-column>
      <el-table-column label="状态" width="110"><template #default="{row}"><StatusTag :status="row.status" /></template></el-table-column>
      <el-table-column label="操作" width="200"><template #default="{row}">
        <el-button size="small" text type="primary" @click.stop="$router.push(`/opcua/${row.id}`)">详情</el-button>
        <el-button size="small" text @click.stop="$router.push(`/opcua/${row.id}/edit`)">编辑</el-button>
        <el-button size="small" text type="danger" @click.stop="handleDel(row.id)">删除</el-button>
      </template></el-table-column>
    </el-table>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessageBox } from 'element-plus'
import { getDevices, deleteDevice } from '../../api/devices'
import { loadMe } from '../../api/user'
import type { Device } from '../../api/types'
import StatusTag from '../../components/DeviceStatusTag.vue'

const devices = ref<Device[]>([])
// ADR-073 D8：证书信任仅对可写角色（Admin/Operator）展示（路由 meta.roles 兜底跳转）
const canManageCert = computed(() => {
  const me = loadMe()
  return me != null && ['Admin', 'Operator'].includes(me.role)
})

// 只列 OPC UA 设备（同一张设备表，前端按协议分区显示）
async function load() {
  try { devices.value = (await getDevices()).filter(d => d.protocol.name === 'OPC UA') } catch {}
}
onMounted(load)

// 列表上给一行轻量安全档位摘要（未声明=自动）
function securitySummary(d: Device) {
  const p = d.connection?.parameters ?? {}
  const policy = String(p.SecurityPolicy || '自动')
  const mode = String(p.SecurityMode || '自动')
  return `${policy} / ${mode}`
}

async function handleDel(id: string) {
  try { await ElMessageBox.confirm('确定删除该 OPC UA 设备吗？此操作不可恢复。', '删除确认', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }) }
  catch { return }
  await deleteDevice(id)
  devices.value = devices.value.filter(d=>d.id!==id)
}
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-actions { display:flex; align-items:center; gap:10px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.empty-state { padding:48px 0; display:flex; flex-direction:column; align-items:center; gap:14px; }
.empty-text { color:var(--text-muted); font-size:14px; }
</style>
