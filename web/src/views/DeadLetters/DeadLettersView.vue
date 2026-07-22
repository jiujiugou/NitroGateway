<template>
  <div>
    <h2 class="page-title">死信队列管理</h2>
    <div class="card">
      <el-table :data="items" size="small" empty-text="无死信条目">
        <el-table-column prop="deviceId" label="设备 ID" width="180">
          <template #default="{ row }">{{ row.deviceId?.slice(0,8) }}...</template>
        </el-table-column>
        <el-table-column prop="recordCount" label="数据条数" width="90" />
        <el-table-column prop="retryCount" label="重试次数" width="90" />
        <el-table-column prop="lastError" label="错误原因" min-width="200" />
        <el-table-column prop="enqueuedAt" label="入队时间" width="170">
          <template #default="{ row }">{{ fmt(row.enqueuedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="140">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click="retry(row)">重放</el-button>
            <el-button size="small" type="danger" @click="discard(row)">丢弃</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import client from '../../api/client'

const items = ref<any[]>([])

async function load() {
  try { const { data } = await client.get('/deadletters'); items.value = data.data ?? [] } catch {}
}

async function retry(row: any) {
  try { await client.post(`/deadletters/${row.batchId}/retry`); await load() } catch {}
}

async function discard(row: any) {
  try { await client.delete(`/deadletters/${row.batchId}`); await load() } catch {}
}

function fmt(t: string) { return t ? new Date(t).toLocaleString() : '-' }

onMounted(load)
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
</style>
