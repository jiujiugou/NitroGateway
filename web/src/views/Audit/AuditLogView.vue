<template>
  <div>
    <h2 class="page-title">操作日志</h2>
    <!-- ADR-065 A3：写值/登录/配置变更 审计可追溯——时间/操作者/动作/结果过滤 -->
    <div class="card" style="margin-bottom:16px">
      <div class="query-bar">
        <el-date-picker
          v-model="range"
          type="datetimerange"
          range-separator="至"
          start-placeholder="开始时间"
          end-placeholder="结束时间"
          style="width:340px"
        />
        <el-input v-model="q.user" placeholder="操作者" clearable style="width:130px" @keyup.enter="search" />
        <el-select v-model="q.method" placeholder="方法" clearable style="width:110px">
          <el-option v-for="m in methods" :key="m" :label="m" :value="m" />
        </el-select>
        <el-input v-model="q.path" placeholder="路径包含" clearable style="width:160px" @keyup.enter="search" />
        <el-input v-model.number="q.status" placeholder="状态码" clearable style="width:100px" @keyup.enter="search" />
        <el-button type="primary" @click="search">查询</el-button>
        <el-button @click="reset">重置</el-button>
      </div>
    </div>
    <div class="card">
      <el-table :data="page.items" size="small" empty-text="暂无操作记录" max-height="560">
        <el-table-column label="时间" width="180">
          <template #default="{ row }">{{ fmt(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column prop="user" label="操作者" width="120" />
        <el-table-column prop="role" label="角色" width="90" />
        <el-table-column label="方法" width="90">
          <template #default="{ row }">
            <el-tag :type="methodTag(row.method)" size="small">{{ row.method }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="path" label="路径" min-width="220" show-overflow-tooltip />
        <el-table-column label="状态码" width="90">
          <template #default="{ row }">
            <el-tag :type="row.statusCode >= 400 ? 'danger' : 'success'" size="small">{{ row.statusCode }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="elapsedMs" label="耗时(ms)" width="90" />
        <el-table-column prop="ip" label="来源 IP" width="130" />
      </el-table>
      <div class="pager">
        <el-pagination
          layout="total, prev, pager, next, sizes"
          :total="page.total"
          :current-page="page.page"
          :page-size="page.pageSize"
          :page-sizes="[20, 50, 100, 200]"
          @current-change="onPage"
          @size-change="onSize"
        />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { getAuditLogs, type AuditLogPage, type AuditLogQuery } from '../../api/audit'

const methods = ['POST', 'PUT', 'DELETE', 'PATCH']
const page = ref<AuditLogPage>({ items: [], total: 0, page: 1, pageSize: 50 })
// v-model.number 清空时运行期为 ''（非 number），类型放宽以兼容空状态码输入
const q = ref<{ user: string; method: string; path: string; status?: number | '' }>({ user: '', method: '', path: '', status: undefined })
const range = ref<[Date, Date] | null>(null)

async function load() {
  const query: AuditLogQuery = {
    page: page.value.page,
    pageSize: page.value.pageSize
  }
  // 时间范围转 UTC ISO（后端按 UTC 过滤）；单选未选则不过滤
  if (range.value?.[0]) query.from = range.value[0].toISOString()
  if (range.value?.[1]) query.to = range.value[1].toISOString()
  if (q.value.user.trim()) query.user = q.value.user.trim()
  if (q.value.method) query.method = q.value.method
  if (q.value.path.trim()) query.path = q.value.path.trim()
  if (q.value.status !== undefined && q.value.status !== '' && !Number.isNaN(q.value.status)) query.status = q.value.status
  try {
    page.value = await getAuditLogs(query)
  } catch {}
}

function search() { page.value.page = 1; load() }
function reset() {
  q.value = { user: '', method: '', path: '', status: undefined }
  range.value = null
  page.value.page = 1
  load()
}
function onPage(p: number) { page.value.page = p; load() }
function onSize(s: number) { page.value.pageSize = s; page.value.page = 1; load() }
function methodTag(m: string) {
  return m === 'DELETE' ? 'danger' : m === 'POST' ? 'warning' : m === 'PUT' || m === 'PATCH' ? 'success' : 'info'
}
function fmt(t: string) { return t ? new Date(t).toLocaleString() : '-' }

onMounted(load)
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
.query-bar { display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
.pager { margin-top:14px; display:flex; justify-content:flex-end; }
</style>
