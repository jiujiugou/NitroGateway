<template>
  <div class="card">
    <div class="toolbar">
      <el-button size="small" @click="handleExport">⬇ 导出 CSV</el-button>
      <el-tooltip
        content="CSV 列头：Name,Address(NodeId),DataType（可选 Access,Enabled,ScanIntervalMs,Deadband,ScaleFactor,ScaleOffset,Description），支持引号转义"
        placement="top"
      >
        <el-button size="small" type="success" plain @click="triggerImport">⬆ 导入 CSV</el-button>
      </el-tooltip>
      <div class="spacer"></div>
      <el-button size="small" type="primary" @click="openAdd">＋ 从服务器选点</el-button>
    </div>
    <input ref="importInputRef" type="file" accept=".csv,text/csv" style="display:none" @change="handleImportFile" />

    <!-- ADR-070 层次1：OPC UA 点位按"服务器地址空间 NodeId"语义呈现，不套用寄存器字段 -->
    <el-table :data="points" row-key="id" size="small" v-loading="loading">
      <el-table-column prop="name" label="点位名称" min-width="140" show-overflow-tooltip />
      <el-table-column label="NodeId（地址）" min-width="230">
        <template #default="{ row }"><span class="mono" :title="row.address">{{ row.address }}</span></template>
      </el-table-column>
      <el-table-column prop="dataType" label="数据类型" width="100" />
      <el-table-column label="访问" width="100">
        <template #default="{ row }">
          <el-tag size="small" :type="accessTagType(row.access)" disable-transitions>{{ accessLabel(row.access) }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="采集间隔(ms)" width="110" align="right">
        <template #default="{ row }">{{ row.scanIntervalMs || '默认' }}</template>
      </el-table-column>
      <el-table-column label="启用" width="70">
        <template #default="{ row }"><el-switch :model-value="row.enabled" disabled size="small" /></template>
      </el-table-column>
      <el-table-column label="操作" width="150">
        <template #default="{ row }">
          <el-button size="small" text type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" text type="danger" @click="handleDel(row.id)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
    <div v-if="!loading && points.length === 0" class="empty">
      暂无点位。点击右上角「从服务器选点」：左侧浏览服务器地址空间，点选变量节点自动带出 NodeId / 类型 / 访问级别。
    </div>
  </div>

  <!-- OPC UA 点位弹窗：左=服务器地址空间浏览取点，右=点位映射与采集配置（ADR-070 D6 增强：原生节点树取点） -->
  <el-dialog
    v-model="showForm"
    :title="editingId ? '编辑 OPC UA 点位' : '从服务器选取 OPC UA 变量'"
    width="980px"
    top="6vh"
  >
    <div v-if="showForm" class="opc-panes">
      <!-- 左：服务器地址空间 -->
      <div class="pane-tree">
        <div class="pane-title">服务器地址空间</div>
        <el-tree
          class="node-tree"
          v-loading="browseLoading"
          lazy
          node-key="nodeId"
          highlight-current
          :load="loadNode"
          :props="{ label: 'name', isLeaf: 'isLeaf' }"
          @node-click="onNodeClick"
        />
        <div class="hint">点击"变量"（叶子节点）选取，自动带出服务器属性；对象节点仅用于展开。</div>
      </div>

      <!-- 右：点位配置 -->
      <div class="pane-form">
        <div class="pane-title">点位映射与采集</div>
        <el-form label-position="top" :model="pf">
          <div class="section-label">点位标识</div>
          <el-form-item label="采集名称">
            <el-input v-model="pf.name" placeholder="例：炉温 / 压力（未填则由节点 BrowseName 自动带入）" />
          </el-form-item>
          <el-form-item label="NodeId（地址）">
            <el-input v-model="pf.address" class="mono-input" placeholder="opc.tcp 服务器地址空间节点，如 ns=2;i=1001" />
            <div class="hint">可从左侧节点树点选回填，也可手填（服务器暂不可浏览时）。</div>
          </el-form-item>

          <!-- 服务器返回的节点属性（从 Browse 带出，仅展示当前会话；不落库） -->
          <div v-if="serverNode" class="node-info">
            <div class="node-info-title">服务器节点（Browse 返回）</div>
            <div class="node-info-row"><span>显示名</span><b>{{ serverNode.name }}</b></div>
            <div class="node-info-row"><span>NodeId</span><b class="mono">{{ serverNode.nodeId }}</b></div>
            <div class="node-info-row"><span>数据类型</span><b>{{ serverNode.typeName || '未知' }}</b></div>
            <div class="node-info-row"><span>访问级别</span><b>{{ serverNode.access || '未知' }}</b></div>
          </div>

          <div class="section-label">数据映射</div>
          <!-- 类型/权限的真值来源：OPC 服务器节点自带 DataType 与 AccessLevel，
               正常应"随服务器自动带出并锁定"；仅当无法浏览/需工程覆盖时才手动指定。 -->
          <el-form-item>
            <el-switch
              v-model="followServer"
              :disabled="serverNode === null"
              active-text="以服务器节点为准"
            />
            <div class="hint">开启后类型/权限由服务器节点自动带出并锁定（服务器为准）；关闭可手动指定——用于服务器不可浏览、或需工程覆盖（如服务器 Double 想按 Float 上报）。</div>
          </el-form-item>
          <div class="form-row">
            <el-form-item label="数据类型">
              <el-select v-model="pf.dataType" :disabled="followServer" style="width:100%">
                <el-option v-for="t in types" :key="t" :label="t" :value="t" />
              </el-select>
            </el-form-item>
            <el-form-item label="采集权限">
              <el-select v-model="pf.access" :disabled="followServer" style="width:100%">
                <el-option label="只读" value="ReadOnly" />
                <el-option label="只写" value="WriteOnly" />
                <el-option label="读写" value="ReadWrite" />
              </el-select>
            </el-form-item>
          </div>

          <div class="section-label">采集与处理</div>
          <div class="form-row">
            <el-form-item label="启用">
              <el-switch v-model="pf.enabled" />
            </el-form-item>
            <el-form-item label="采集间隔(ms)">
              <el-input-number v-model="pf.scanIntervalMs" :min="0" style="width:100%" />
            </el-form-item>
          </div>
          <div class="form-row">
            <el-form-item label="变化死区（死带）">
              <el-input-number v-model="pf.deadband" :min="0" :step="0.1" style="width:100%" />
            </el-form-item>
            <el-form-item label="缩放系数">
              <el-input-number v-model="pf.scaleFactor" :min="0" :step="0.1" style="width:100%" />
            </el-form-item>
          </div>
          <el-form-item label="缩放偏移">
            <el-input-number v-model="pf.scaleOffset" :step="0.1" style="width:100%" />
          </el-form-item>
          <el-form-item label="描述">
            <el-input v-model="pf.description" type="textarea" :rows="2" placeholder="可选，便于在仪表盘 / 告警中识别该点" />
          </el-form-item>
        </el-form>
      </div>
    </div>
    <template #footer>
      <el-button @click="showForm = false">取消</el-button>
      <el-button type="primary" :loading="saving" @click="save">保存</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getDevice, getPoints, addPoint, updatePoint, deletePoint, exportPoints, importPoints, browseNodes } from '../api/devices'
import type { BrowseNode, DataType, DevicePoint, PointAccess } from '../api/types'

// ADR-070 层次1：OPC UA 点位管理专属视图（/opcua/:id/points）。后端接口协议无关（/devices/*），
// 本组件只做前端窗体重组——不改变数据契约，也不影响 Modbus/S7 的通用 PointTable。
const props = defineProps<{ deviceId: string }>()
const router = useRouter()

const points = ref<DevicePoint[]>([])
const loading = ref(false)
const showForm = ref(false)
const editingId = ref<string | null>(null)
const saving = ref(false)
const importInputRef = ref<HTMLInputElement>()
const browseLoading = ref(false)
const serverNode = ref<BrowseNode | null>(null)
// 类型/权限是否"以服务器节点为准"（服务器 Browse 带出即锁定；关闭可手动覆盖/兜底）
const followServer = ref(false)

const types: DataType[] = ['Bool', 'Byte', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Int64', 'UInt64', 'Float', 'Double', 'String']

interface PointForm {
  name: string
  address: string
  description?: string
  dataType: DataType
  access: PointAccess
  enabled: boolean
  scanIntervalMs: number
  deadband: number
  scaleFactor: number
  scaleOffset: number
}

const makeEmpty = (): PointForm => ({
  name: '',
  address: 'ns=2;i=1001',
  dataType: 'Float',
  access: 'ReadOnly',
  enabled: true,
  scanIntervalMs: 0,
  deadband: 0,
  scaleFactor: 1,
  scaleOffset: 0
})

const pf = ref<PointForm>(makeEmpty())

// OPC UA 服务器访问级别 → 采集权限（与后端 BrowseNode.Access 语义一致）
const ACCESS_LABEL: Record<PointAccess, string> = { ReadOnly: '只读', WriteOnly: '只写', ReadWrite: '读写' }
const accessLabel = (a: PointAccess) => ACCESS_LABEL[a] ?? a
const accessTagType = (a: PointAccess) => (a === 'ReadWrite' ? 'warning' : a === 'WriteOnly' ? 'danger' : 'info')

onMounted(async () => {
  // 分区守卫：本页专属 OPC UA，非 OPC UA 设备跳回通用点位页
  try {
    const d = await getDevice(props.deviceId)
    if (d && d.protocol.name !== 'OPC UA') { router.replace(`/devices/${props.deviceId}/points`); return }
  } catch {}
  await reload()
})

async function reload() {
  loading.value = true
  try { points.value = await getPoints(props.deviceId) } catch {} finally { loading.value = false }
}

function openAdd() {
  editingId.value = null
  serverNode.value = null
  followServer.value = false
  pf.value = makeEmpty()
  showForm.value = true
}

function openEdit(row: DevicePoint) {
  editingId.value = row.id
  serverNode.value = null
  // 编辑态为既有配置：类型/权限以库内为准，保持可手动调整
  followServer.value = false
  pf.value = {
    name: row.name,
    address: row.address,
    description: row.description ?? '',
    dataType: row.dataType,
    access: row.access,
    enabled: row.enabled,
    scanIntervalMs: row.scanIntervalMs,
    deadband: row.deadband,
    scaleFactor: row.scaleFactor,
    scaleOffset: row.scaleOffset
  }
  showForm.value = true
}

async function save() {
  if (!pf.value.name.trim()) { ElMessage.warning('请填写采集名称'); return }
  if (!pf.value.address.trim()) { ElMessage.warning('请填写 NodeId（地址）'); return }
  saving.value = true
  try {
    const payload = { ...pf.value }
    if (payload.description === '') delete payload.description
    if (editingId.value) await updatePoint(props.deviceId, editingId.value, payload)
    else await addPoint(props.deviceId, payload)
    ElMessage.success('保存成功')
    showForm.value = false
    await reload()
  } catch (e: any) {
    const msg = e?.response?.data?.error?.message ?? e?.response?.data?.title ?? e?.message ?? '未知错误'
    ElMessage.error(`保存失败: ${msg}`)
  } finally {
    saving.value = false
  }
}

async function handleDel(id: string) {
  try { await ElMessageBox.confirm('确定删除该点位吗？', '删除确认', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }) }
  catch { return }
  try {
    await deletePoint(props.deviceId, id)
    points.value = points.value.filter(p => p.id !== id)
  } catch {}
}

// ---- CSV 导入 / 导出（复用后端 /points/export 与 PointImportController，协议无关） ----
async function handleExport() {
  try { await exportPoints(props.deviceId) } catch {}
}

function triggerImport() {
  importInputRef.value?.click()
}

async function handleImportFile(e: Event) {
  const input = e.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = '' // 允许再次选择同一文件
  if (!file) return
  try {
    const text = await readCsvText(file)
    const count = await importPoints(props.deviceId, text)
    ElMessage.success(`已导入 ${count} 个点位`)
    await reload()
  } catch (err: any) {
    ElMessage.error(`导入失败: ${err?.response?.data?.error?.message ?? err?.message ?? '未知错误'}`)
  }
}

// 中文 Windows/Excel 常导出 GBK 编码 CSV，UTF-8 解码出现替换字符时回退 GBK 再解，避免中文点位名乱码
async function readCsvText(file: File): Promise<string> {
  const buf = await file.arrayBuffer()
  let text = new TextDecoder('utf-8').decode(buf)
  if (text.includes('\uFFFD')) {
    text = new TextDecoder('gbk').decode(buf)
  }
  return text.replace(/^\uFEFF/, '')
}

// ---- OPC UA 节点浏览（ADR-070 D1/D2：单层 Browse + 前端懒加载；失败不置 Faulted，仅提示） ----
async function loadNode(node: any, resolve: (data: any[]) => void) {
  browseLoading.value = true
  try {
    const parent = node.level === 0 ? '' : node.data.nodeId
    const list = await browseNodes(props.deviceId, parent)
    resolve(list.map(n => ({ ...n, isLeaf: n.isVariable })))
  } catch (err: any) {
    ElMessage.error(`浏览失败: ${err?.response?.data?.error?.message ?? err?.message ?? '未知错误'}（可在右侧手填 NodeId）`)
    resolve([])
  } finally {
    browseLoading.value = false
  }
}

// 点选变量节点：回填 NodeId / 类型 / 权限，并把服务器属性展示在右侧（编辑态仍可改）
function onNodeClick(node: any) {
  if (!node.isVariable) return
  serverNode.value = {
    nodeId: node.nodeId,
    name: node.name,
    typeName: node.typeName ?? '',
    isVariable: true,
    access: node.access ?? ''
  }
  pf.value.address = node.nodeId
  // 服务器 DataType 可能超出网关支持的 11 种（Browse 已把其余统一映射为 "Unknown"，如 DateTime 等）。
  // 可映射 → 回填并锁定"以服务器为准"；不可映射 → 解锁让用户手动指定相近类型，
  // 避免"以服务器为准"把类型下拉锁在一个错误/残留的默认值上。
  const serverTypeSupported = types.includes(node.typeName as DataType)
  if (serverTypeSupported) pf.value.dataType = node.typeName as DataType
  pf.value.access = node.access === 'ReadWrite' ? 'ReadWrite' : node.access === 'Write' ? 'WriteOnly' : 'ReadOnly'
  followServer.value = serverTypeSupported
  if (!serverTypeSupported) {
    ElMessage.warning(`服务器数据类型「${node.typeName || 'Unknown'}」不在网关支持列表，请手动选择相近类型`)
  }
  if (!pf.value.name.trim()) pf.value.name = node.name
}
</script>

<style scoped>
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.toolbar { display:flex; gap:8px; align-items:center; padding:10px 16px; border-bottom:1px solid var(--border); }
.spacer { flex:1; }
.mono { font-family:ui-monospace,SFMono-Regular,Consolas,monospace; font-size:12px; }
.mono-input :deep(input) { font-family:ui-monospace,SFMono-Regular,Consolas,monospace; }
.empty { padding:32px 20px; color:var(--text-muted); font-size:13px; }
.hint { font-size:12px; color:var(--text-dim,#909399); margin-top:4px; line-height:1.5; }

.opc-panes { display:grid; grid-template-columns:340px 1fr; gap:18px; align-items:start; }
.pane-title { font-size:13px; font-weight:600; color:var(--text-heading); margin-bottom:10px; }
.pane-tree { border:1px solid var(--border); border-radius:8px; padding:12px; }
.node-tree { max-height:520px; overflow:auto; }

.pane-form .section-label { font-size:12px; font-weight:600; color:var(--text-muted); margin:14px 0 2px; text-transform:uppercase; letter-spacing:.4px; }
.form-row { display:grid; grid-template-columns:1fr 1fr; gap:0 16px; }

.node-info { margin:4px 0 8px; padding:10px 12px; border:1px dashed var(--border); border-radius:8px; background:var(--bg-code,#f8fafc); font-size:12px; }
.node-info-title { font-weight:600; color:var(--text-heading); margin-bottom:6px; }
.node-info-row { display:flex; gap:8px; justify-content:space-between; padding:2px 0; color:var(--text-muted); }
.node-info-row b { color:var(--text-heading); font-weight:500; }
</style>
