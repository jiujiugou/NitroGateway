<template>
  <div class="page-head">
    <h2 class="page-title">点位管理</h2>
    <div class="actions">
      <el-button @click="handleExport">⬇ 导出 CSV</el-button>
      <!-- ADR-055 缺口2：点位 CSV 导入前端接线（后端 PointImportController.ImportCsv 已实现） -->
      <el-tooltip
        content="CSV 列头：Name,Address,DataType（可选 Access,Enabled,ScanIntervalMs,Deadband,ScaleFactor,ScaleOffset,Description），支持引号转义"
        placement="top"
      >
        <el-button type="success" plain @click="triggerImport">⬆ 导入 CSV</el-button>
      </el-tooltip>
      <el-button type="warning" @click="showGen=true">⚙ 批量生成</el-button>
      <el-button type="primary" @click="openAdd">+ 添加点位</el-button>
    </div>
  </div>
  <input ref="importInputRef" type="file" accept=".csv,text/csv" style="display:none" @change="handleImportFile" />

  <div class="card">
    <el-table :data="points" row-key="id" size="small">
      <el-table-column prop="name" label="名称" />
      <el-table-column prop="address" label="地址" width="140" />
      <el-table-column prop="dataType" label="类型" width="90" />
      <el-table-column prop="access" label="权限" width="90" />
      <el-table-column label="缩放" width="150">
        <template #default="{ row }">×{{ row.scaleFactor }} +{{ row.scaleOffset }}</template>
      </el-table-column>
      <el-table-column prop="deadband" label="死区" width="70" />
      <el-table-column label="启用" width="60">
        <template #default="{ row }"><el-switch :model-value="row.enabled" disabled size="small" /></template>
      </el-table-column>
      <el-table-column label="操作" width="140">
        <template #default="{ row }">
          <el-button size="small" text type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" text type="danger" @click="handleDel(row.id)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>

  <!-- 添加/编辑点位 -->
  <el-dialog v-model="showForm" :title="editingId ? '编辑点位' : '添加点位'" width="520px">
    <el-form :model="pf" label-position="top">
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:0 16px">
        <el-form-item label="名称"><el-input v-model="pf.name" /></el-form-item>
        <el-form-item label="地址"><el-input v-model="pf.address" /></el-form-item>
        <el-form-item label="数据类型">
          <el-select v-model="pf.dataType" style="width:100%">
            <el-option v-for="t in types" :key="t" :label="t" :value="t" />
          </el-select>
        </el-form-item>
        <el-form-item label="权限">
          <el-select v-model="pf.access" style="width:100%">
            <el-option label="只读" value="ReadOnly" /><el-option label="只写" value="WriteOnly" /><el-option label="读写" value="ReadWrite" />
          </el-select>
        </el-form-item>
        <el-form-item label="缩放系数"><el-input-number v-model="pf.scaleFactor" :min="0" :step="0.1" /></el-form-item>
        <el-form-item label="缩放偏移"><el-input-number v-model="pf.scaleOffset" :step="0.1" /></el-form-item>
        <el-form-item label="死区"><el-input-number v-model="pf.deadband" :min="0" :step="0.1" /></el-form-item>
        <el-form-item label="采集间隔(ms)"><el-input-number v-model="pf.scanIntervalMs" :min="0" /></el-form-item>
      </div>
    </el-form>
    <template #footer><el-button @click="showForm=false">取消</el-button><el-button type="primary" @click="save">保存</el-button></template>
  </el-dialog>

  <!-- 批量生成 -->
  <el-dialog v-model="showGen" title="批量生成点位" width="460px">
    <el-form :model="gf" label-position="top">
      <el-form-item label="名称模板">
        <el-input v-model="gf.nameTemplate" placeholder="如 AI_{###} → AI_001, AI_002..." />
        <div class="hint">{{ previewName }}</div>
      </el-form-item>
      <!-- ADR-024 P3-3：起始地址按协议解释（Modbus 数字 / S7 DB 区地址 / OPC UA NodeId） -->
      <el-form-item label="起始地址"><el-input v-model="gf.startAddress" :placeholder="defaultStartAddress(deviceProtocol)" style="width:100%" /></el-form-item>
      <el-form-item label="数量"><el-input-number v-model="gf.count" :min="1" :max="5000" style="width:100%" /></el-form-item>
      <el-form-item label="数据类型">
        <el-select v-model="gf.dataType" style="width:100%">
          <el-option v-for="t in types" :key="t" :label="t" :value="t" />
        </el-select>
      </el-form-item>
      <el-form-item label="权限">
        <el-select v-model="gf.access" style="width:100%">
          <el-option label="只读" value="ReadOnly" /><el-option label="读写" value="ReadWrite" />
        </el-select>
      </el-form-item>
      <div class="hint">{{ genHint }}</div>
    </el-form>
    <template #footer><el-button @click="showGen=false">取消</el-button><el-button type="primary" @click="generate">生成</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getDevice, getPoints, addPoint, updatePoint, deletePoint, generatePoints, exportPoints, importPoints } from '../../api/devices'
import type { DevicePoint } from '../../api/types'

const route = useRoute()
const deviceId = route.params.deviceId as string
const points = ref<DevicePoint[]>([])
const showForm = ref(false)
const showGen = ref(false)
const editingId = ref<string | null>(null)
const importInputRef = ref<HTMLInputElement>()
const types = ['Bool','Byte','Int16','UInt16','Int32','UInt32','Int64','UInt64','Float','Double','String']
const deviceProtocol = ref('Modbus')

const makeEmpty = () => ({ name:'', address: defaultStartAddress(deviceProtocol.value), dataType:'Float', access:'ReadOnly', scaleFactor:1, scaleOffset:0, deadband:0, scanIntervalMs:0, enabled:true })
const pf = ref<Record<string, any>>(makeEmpty())
const gf = ref({ nameTemplate:'AI_{###}', startAddress:'40001', count:100, dataType:'Float', access:'ReadOnly' })

// ADR-024 P3-3 扩展：按设备协议给出默认起始地址（Modbus 数字 / S7 DB 区 / OPC UA 数值标识符）
function defaultStartAddress(protocol: string): string {
  if (protocol === 'S7') return 'DB1.DBD0'
  if (protocol === 'OPC UA') return 'ns=2;i=1001'
  return '40001'
}

// 批量生成递增规则的提示文案（OPC UA 仅数值标识符 i= 可自动 +1；s= 字符串标识无连续编号语义）
const genHint = computed(() => {
  const proto = deviceProtocol.value
  let rule = 'Modbus 寄存器数'
  let extra = ''
  if (proto === 'S7') { rule = '类型字节宽度'; extra = '（DB 区，不支持 Bool）' }
  else if (proto === 'OPC UA') { rule = '数值标识（i=）'; extra = '（如 ns=2;i=1001 → 1002，仅支持数值标识符）' }
  return `将生成 ${gf.value.count} 个点位，地址按${rule}递增${extra}`
})

const previewName = computed(() => {
  const pad = (gf.value.nameTemplate.match(/#/g) || []).length
  if (!pad) return gf.value.nameTemplate + '1'
  return gf.value.nameTemplate.replace('#'.repeat(pad), String(1).padStart(pad, '0'))
})

onMounted(async () => {
  try { points.value = await getPoints(deviceId) } catch {}
  // ADR-024 P3-3：按设备协议决定默认起始地址（S7 用 DB 区地址）
  try {
    const d = await getDevice(deviceId)
    if (d) { deviceProtocol.value = d.protocol.name; gf.value.startAddress = defaultStartAddress(d.protocol.name) }
  } catch {}
})

function openAdd() {
  editingId.value = null
  pf.value = makeEmpty()
  showForm.value = true
}

function openEdit(row: DevicePoint) {
  editingId.value = row.id
  pf.value = { ...row }
  showForm.value = true
}

async function save() {
  try {
    if (editingId.value) {
      const p = await updatePoint(deviceId, editingId.value, pf.value as any)
      if (p) {
        const idx = points.value.findIndex(pt => pt.id === editingId.value)
        if (idx >= 0) points.value.splice(idx, 1, p)
        showForm.value = false
      }
    } else {
      const p = await addPoint(deviceId, pf.value as any)
      if (p) { points.value.push(p); showForm.value = false }
    }
  } catch {}
}

async function handleDel(id: string) {
  try { await deletePoint(deviceId,id); points.value=points.value.filter(p=>p.id!==id) } catch {}
}

async function handleExport() {
  try { await exportPoints(deviceId) } catch {}
}

function triggerImport() {
  importInputRef.value?.click()
}

// ADR-055 缺口2：读文件 → 调 importPoints → 刷新列表；后端错误信息（CSV 缺列/解析失败）透出给用户。
// 中文 Windows/Excel 常导出 GBK 编码 CSV，UTF-8 解码出现替换符时回退按 GBK 再解，避免中文点位名乱码。
async function handleImportFile(e: Event) {
  const input = e.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = '' // 允许再次选择同一文件
  if (!file) return
  try {
    const text = await readCsvText(file)
    const count = await importPoints(deviceId, text)
    ElMessage.success(`已导入 ${count} 个点位`)
    points.value = await getPoints(deviceId)
  } catch (err: any) {
    ElMessage.error(`导入失败: ${err?.response?.data?.error?.message ?? err?.message ?? '未知错误'}`)
  }
}

async function readCsvText(file: File): Promise<string> {
  const buf = await file.arrayBuffer()
  let text = new TextDecoder('utf-8').decode(buf)
  if (text.includes('\uFFFD')) {
    text = new TextDecoder('gbk').decode(buf)
  }
  return text.replace(/^\uFEFF/, '')
}

async function generate() {
  try {
    const count = await generatePoints(deviceId, { ...gf.value, protocol: deviceProtocol.value })
    if (count > 0) { points.value = await getPoints(deviceId); showGen.value=false }
  } catch {}
}
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-title { margin-bottom:0; }
.actions { display:flex; gap:8px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
.hint { font-size:12px; color:var(--text-dim,#909399); margin-top:4px; }
</style>

