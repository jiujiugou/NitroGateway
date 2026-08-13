<template>
  <div>
    <h2 class="page-title">站点管理</h2>
    <div class="card">
      <el-table :data="items" row-key="siteId" size="small" empty-text="暂无站点数据（数据上报后自动出现）">
        <el-table-column prop="siteId" label="站点 ID" min-width="140" />
        <el-table-column label="显示名" min-width="220">
          <template #default="{ row }">
            <div class="name-cell">
              <el-input v-model="row._displayName" size="small" placeholder="未命名" @keyup.enter="save(row)" />
              <el-button size="small" type="primary" :loading="savingId === row.siteId" @click="save(row)">保存</el-button>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="来源 ClientId" min-width="240">
          <template #default="{ row }">
            <div>{{ row.sourceClientId ?? '-' }}</div>
            <div v-if="row.lastSeenClientId && row.lastSeenClientId !== row.sourceClientId" class="muted">最近: {{ row.lastSeenClientId }}</div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="120">
          <template #default="{ row }">
            <el-tag v-if="row.hasConflict" type="danger" size="small">多来源冲突</el-tag>
            <el-tag v-else-if="row.displayName" type="success" size="small">已命名</el-tag>
            <el-tag v-else type="info" size="small">未命名</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="首见时间" width="170">
          <template #default="{ row }">{{ fmt(row.firstSeenAt) }}</template>
        </el-table-column>
        <el-table-column label="最近上报" width="170">
          <template #default="{ row }">{{ fmt(row.lastSeenAt) }}</template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { getSiteInfos, renameSite, type SiteInfo } from '../../api/sites'

type Row = SiteInfo & { _displayName: string }

const items = ref<Row[]>([])
const savingId = ref<string | null>(null)

async function load() {
  try {
    const list = await getSiteInfos()
    items.value = list.map(s => ({ ...s, _displayName: s.displayName }))
  } catch { items.value = [] }
}

async function save(row: Row) {
  const name = (row._displayName ?? '').trim()
  if (name === row.displayName) return
  savingId.value = row.siteId
  try {
    await renameSite(row.siteId, name)
    ElMessage.success('显示名已保存')
    await load()
  } catch (e: any) {
    ElMessage.error(`保存失败: ${e?.response?.data?.error?.message ?? e?.message ?? e}`)
  } finally { savingId.value = null }
}

function fmt(t?: string | null) { return t ? new Date(t).toLocaleString() : '-' }

onMounted(load)
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
.name-cell { display:flex; gap:8px; align-items:center; }
.muted { color:#a0aec0; font-size:12px; }
</style>
