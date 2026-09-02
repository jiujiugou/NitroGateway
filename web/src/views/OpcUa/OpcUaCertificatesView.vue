<template>
  <div>
    <h2 class="page-title">OPC UA 证书</h2>
    <!-- ADR-073 D8：OPC UA 服务器证书信任管理（pki/rejected、pki/trusted 白名单）。
         信任状态以 pki 目录为唯一权威，不入 SQLite；本页只读投影文件系统 PKI 状态。 -->
    <div class="card">
      <div class="toolbar">
        <span class="toolbar-hint">首次连接被拒的服务器证书会进入「待信任」；信任后移入白名单，可选触发对应设备重连（重连需该设备仍在线目标可用）。</span>
        <el-button :loading="loading" @click="refresh">刷新</el-button>
      </div>
      <el-tabs v-model="tab" @tab-change="refresh">
        <el-tab-pane label="待信任（rejected）" name="rejected">
          <el-table :data="rejected" size="small" empty-text="暂无待信任证书" v-loading="loading">
            <el-table-column prop="subject" label="主题（Subject）" min-width="220" show-overflow-tooltip />
            <el-table-column label="指纹（Thumbprint）" min-width="230">
              <template #default="{ row }">
                <span class="thumb">{{ row.thumbprint }}</span>
              </template>
            </el-table-column>
            <el-table-column label="有效期至" width="170">
              <template #default="{ row }">{{ fmt(row.notAfter) }}</template>
            </el-table-column>
            <el-table-column label="进入时间" width="170">
              <template #default="{ row }">{{ fmt(row.importedAt) }}</template>
            </el-table-column>
            <el-table-column label="操作" width="160" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" size="small" @click="openTrust(row)">信任</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane label="已信任（trusted）" name="trusted">
          <el-table :data="trusted" size="small" empty-text="白名单为空" v-loading="loading">
            <el-table-column prop="subject" label="主题（Subject）" min-width="220" show-overflow-tooltip />
            <el-table-column label="指纹（Thumbprint）" min-width="230">
              <template #default="{ row }">
                <span class="thumb">{{ row.thumbprint }}</span>
              </template>
            </el-table-column>
            <el-table-column label="有效期至" width="170">
              <template #default="{ row }">{{ fmt(row.notAfter) }}</template>
            </el-table-column>
            <el-table-column label="信任时间" width="170">
              <template #default="{ row }">{{ fmt(row.importedAt) }}</template>
            </el-table-column>
            <el-table-column label="操作" width="160" fixed="right">
              <template #default="{ row }">
                <el-button link type="danger" size="small" @click="onRevoke(row)">撤销信任</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <!-- 信任确认：可选指定 OPC UA 设备 → 信任后驱逐该设备驱动，使其以新信任状态自动重连（ADR-073 D8） -->
    <el-dialog v-model="trustVisible" title="信任服务器证书" width="480">
      <el-form label-position="top">
        <el-form-item label="主题"><span class="trust-subject">{{ trustTarget?.subject ?? '' }}</span></el-form-item>
        <el-form-item label="指纹"><span class="thumb">{{ trustTarget?.thumbprint ?? '' }}</span></el-form-item>
        <el-form-item label="信任后触发重连（可选）">
          <el-select v-model="trustDeviceId" style="width:100%" clearable filterable placeholder="选择要重连的 OPC UA 设备（不选则仅移入白名单）">
            <el-option v-for="dv in opcuaDevices" :key="dv.id" :label="dv.name" :value="dv.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="trustVisible = false">取消</el-button>
        <el-button type="primary" :loading="trusting" @click="onTrust">确认信任</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  getRejectedCertificates,
  getTrustedCertificates,
  trustCertificate,
  revokeCertificate,
  getDevices
} from '../../api/devices'
import type { OpcUaCertificate } from '../../api/types'

const tab = ref<'rejected' | 'trusted'>('rejected')
const loading = ref(false)
const rejected = ref<OpcUaCertificate[]>([])
const trusted = ref<OpcUaCertificate[]>([])

const trustVisible = ref(false)
const trusting = ref(false)
const trustTarget = ref<OpcUaCertificate | null>(null)
const trustDeviceId = ref('')
const opcuaDevices = ref<{ id: string; name: string }[]>([])

// OPC UA 时间（后端 O 格式 UTC，如 2026-09-02T03:04:05.0000000Z）转本地可读；空串显示 '-'
function fmt(iso: string): string {
  if (!iso) return '-'
  const d = new Date(iso)
  if (isNaN(d.getTime())) return iso
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

async function refresh() {
  loading.value = true
  try {
    const [rej, tr] = await Promise.all([getRejectedCertificates(), getTrustedCertificates()])
    rejected.value = rej
    trusted.value = tr
  } catch {
    /* 401/403 由拦截器提示，其余静默 */
  } finally {
    loading.value = false
  }
}

async function loadDevices() {
  try {
    const ds = await getDevices()
    opcuaDevices.value = ds.filter(d => d.protocol.name === 'OPC UA').map(d => ({ id: d.id, name: d.name }))
  } catch {
    opcuaDevices.value = []
  }
}

function openTrust(row: OpcUaCertificate) {
  trustTarget.value = row
  trustDeviceId.value = ''
  trustVisible.value = true
}

async function onTrust() {
  if (!trustTarget.value) return
  trusting.value = true
  try {
    const ok = await trustCertificate(trustTarget.value.thumbprint, trustDeviceId.value || undefined)
    if (!ok) {
      ElMessage.error('信任失败，请刷新后重试')
      return
    }
    ElMessage.success(trustDeviceId.value ? '已信任并触发设备重连' : '已信任')
    trustVisible.value = false
    await refresh()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error?.message ?? e?.message ?? '信任失败')
  } finally {
    trusting.value = false
  }
}

async function onRevoke(row: OpcUaCertificate) {
  try {
    await ElMessageBox.confirm(
      `撤销对 ${row.subject} 的信任？该证书将回到未信任状态，相关设备下一次连接会被拒绝。`,
      '撤销信任',
      { type: 'warning', confirmButtonText: '撤销', cancelButtonText: '取消' }
    )
  } catch {
    return // 用户取消
  }
  try {
    const ok = await revokeCertificate(row.thumbprint)
    if (!ok) {
      ElMessage.error('撤销失败，请刷新后重试')
      return
    }
    ElMessage.success('已撤销信任')
    await refresh()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error?.message ?? e?.message ?? '撤销失败')
  }
}

onMounted(async () => {
  await refresh()
  await loadDevices()
})
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); padding:20px 24px; }
.toolbar { display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:8px; }
.toolbar-hint { color:#909399; font-size:12px; }
.thumb { font-family:Consolas,Menlo,monospace; font-size:12px; color:#4a5568; letter-spacing:.3px; }
.trust-subject { font-size:14px; color:#303133; }
</style>
