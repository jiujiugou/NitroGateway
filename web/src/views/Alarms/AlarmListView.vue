<template>
  <div>
    <h2 class="page-title">告警管理</h2>
    <div class="card">
      <el-table :data="alarms" size="small" empty-text="无活跃告警">
        <el-table-column label="等级" width="100">
          <template #default="{ row }">
            <el-tag :type="sevTag(row.severity)" size="small">{{ row.severity }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="stateTag(row.state)" size="small">{{ stateText(row.state) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="message" label="消息" min-width="200" />
        <el-table-column prop="triggerValue" label="触发值" width="90" />
        <el-table-column prop="threshold" label="阈值" width="80" />
        <el-table-column label="发生时间" width="170">
          <template #default="{ row }">{{ fmt(row.occurredAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="100">
          <template #default="{ row }">
            <el-button v-if="row.state==='Active'" size="small" type="warning" @click="ack(row)">确认</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import client from '../../api/client'

const alarms = ref<any[]>([])

async function load() {
  try { const { data } = await client.get('/alarms'); alarms.value = data.data ?? [] } catch {}
}

function sevTag(s: string) {
  return s === 'Critical' || s === 'Emergency' ? 'danger' : s === 'Warning' ? 'warning' : 'info'
}
function stateTag(s: string) { return s === 'Active' ? 'danger' : s === 'Acknowledged' ? 'warning' : 'success' }
function stateText(s: string) { return s === 'Active' ? '活跃' : s === 'Acknowledged' ? '已确认' : s }
function fmt(t: string) { return t ? new Date(t).toLocaleString() : '-' }

async function ack(row: any) {
  try { await client.post(`/alarms/${row.id}/ack`); await load() } catch {}
}

onMounted(load)
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
</style>
