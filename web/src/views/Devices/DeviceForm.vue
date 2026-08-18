<template>
  <h2 class="page-title" style="margin-bottom:20px">{{ isEdit ? '编辑设备' : '添加设备' }}</h2>
  <div class="card"><div style="padding:24px">
    <el-form :model="f" label-position="top">
      <div class="form-row">
        <el-form-item label="设备名称"><el-input v-model="f.name" placeholder="例如：一号车间 PLC" /></el-form-item>
        <el-form-item label="协议">
          <el-select v-model="f.protocol.name" style="width:100%" @change="onProtocolChange">
            <!-- ADR-007 P2-2：后端 ProtocolDriverFactory 仅注册 Modbus+S7；OPC UA 未接入，Mitsubishi 待 slnx 启用后再放回 -->
            <el-option label="Modbus" value="Modbus" />
            <el-option label="S7" value="S7" />
          </el-select>
        </el-form-item>
        <el-form-item label="传输方式">
          <el-select v-if="f.protocol.name === 'Modbus'" v-model="f.protocol.dialect" style="width:100%" @change="onDialectChange">
            <el-option label="TCP（网口）" value="TCP" />
            <el-option label="RTU（串口）" value="RTU" />
          </el-select>
          <!-- ADR-024 P3-2：S7 仅 TCP（默认 102 端口），不再显示可编辑的 TCP/RTU 输入框 -->
          <el-input v-else :model-value="'TCP'" disabled />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="f.status" style="width:100%">
            <el-option label="在线" value="Online" />
            <el-option label="离线" value="Offline" />
            <el-option label="未知" value="Unknown" />
          </el-select>
        </el-form-item>
      </div>

      <template v-if="isRtu">
        <div class="form-row">
          <el-form-item label="串口">
            <el-select v-model="f.connection.endpoint" style="width:100%" allow-create filterable default-first-option placeholder="COM3 / /dev/ttyUSB0">
              <el-option v-for="p in availablePorts" :key="p" :label="p" :value="p" />
            </el-select>
          </el-form-item>
          <el-form-item label="波特率">
            <el-select v-model="serial.baudRate" style="width:100%">
              <el-option v-for="b in baudRates" :key="b" :label="`${b}`" :value="b" />
            </el-select>
          </el-form-item>
          <el-form-item label="数据位">
            <el-select v-model="serial.dataBits" style="width:100%">
              <el-option label="8" :value="8" />
              <el-option label="7" :value="7" />
            </el-select>
          </el-form-item>
        </div>
        <div class="form-row">
          <el-form-item label="校验位">
            <el-select v-model="serial.parity" style="width:100%">
              <el-option v-for="p in parities" :key="p" :label="p" :value="p" />
            </el-select>
          </el-form-item>
          <el-form-item label="停止位">
            <el-select v-model="serial.stopBits" style="width:100%">
              <el-option v-for="s in stopBits" :key="s" :label="s" :value="s" />
            </el-select>
          </el-form-item>
          <el-form-item label="字节序">
            <el-select v-model="serial.dataFormat" style="width:100%">
              <el-option v-for="fmt in dataFormats" :key="fmt.value" :label="fmt.label" :value="fmt.value" />
            </el-select>
          </el-form-item>
          <el-form-item label="从站地址">
            <el-input-number v-model="serial.unitId" :min="1" :max="247" style="width:100%" />
          </el-form-item>
        </div>
      </template>

      <div v-else class="form-row">
        <el-form-item label="连接地址">
          <!-- ADR-024 P3-2：占位按协议区分，S7 默认端口 102（Modbus 502） -->
          <el-input v-model="f.connection.endpoint" :placeholder="f.protocol.name === 'S7' ? '192.168.1.100:102' : '192.168.1.100:502'" />
        </el-form-item>
        <el-form-item v-if="f.protocol.name === 'Modbus'" label="从站地址">
          <!-- Modbus TCP 从站软件常在单端口上按 UnitId 区分多个窗口：同 IP:端口建多个设备、分别填 1/2/3... -->
          <el-input-number v-model="serial.unitId" :min="1" :max="247" style="width:100%" />
        </el-form-item>
        <el-form-item label="连接超时(ms)"><el-input-number v-model="f.connection.connectTimeoutMs" :min="100" /></el-form-item>
        <el-form-item label="请求超时(ms)"><el-input-number v-model="f.connection.requestTimeoutMs" :min="100" /></el-form-item>
        <el-form-item v-if="f.protocol.name === 'Modbus'" label="字节序">
          <el-select v-model="serial.dataFormat" style="width:100%">
            <el-option v-for="fmt in dataFormats" :key="fmt.value" :label="fmt.label" :value="fmt.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="重试次数"><el-input-number v-model="f.connection.retryCount" :min="0" /></el-form-item>
        <el-form-item label="重试间隔(ms)"><el-input-number v-model="f.connection.retryIntervalMs" :min="100" /></el-form-item>
      </div>

      <!-- ADR-024 P3-1：S7 连接参数（Rack/Slot/CpuType/PingAddress），后端 S7Driver 依赖这些参数 -->
      <div v-if="f.protocol.name === 'S7'" class="form-row">
        <el-form-item label="Rack（机架）"><el-input-number v-model="s7.rack" :min="0" :max="7" style="width:100%" /></el-form-item>
        <el-form-item label="Slot（插槽）"><el-input-number v-model="s7.slot" :min="0" :max="31" style="width:100%" /></el-form-item>
        <el-form-item label="CPU 型号">
          <el-select v-model="s7.cpuType" style="width:100%">
            <el-option v-for="t in s7CpuTypes" :key="t.value" :label="t.label" :value="t.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="Ping 地址"><el-input v-model="s7.pingAddress" placeholder="DB1.DBW0" /></el-form-item>
      </div>

      <el-form-item label="描述"><el-input v-model="f.description" type="textarea" rows="2" /></el-form-item>
      <div style="display:flex;gap:12px;margin-top:8px">
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
        <el-button :loading="testing" @click="testConn">🔌 测试连接</el-button>
        <el-button @click="$router.back()">取消</el-button>
      </div>
      <div v-if="testResult !== null" :class="['test-result', testResult.success ? 'test-ok' : 'test-fail']" style="margin-top:12px">
        {{ testResult.success ? `✅ 连接成功 (${testResult.latencyMs}ms)` : `❌ 连接失败: ${testResult.error}` }}
      </div>
    </el-form>
  </div></div>
</template>
<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getDevice, createDevice, updateDevice, testConnection, getSerialPorts } from '../../api/devices'

const route = useRoute(); const router = useRouter()
const isEdit = ref(!!route.params.id)
const saving = ref(false)
const testing = ref(false)
const testResult = ref<{ success: boolean; latencyMs: number; error?: string } | null>(null)
const availablePorts = ref<string[]>([])

const baudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200]
const parities = ['None', 'Even', 'Odd', 'Mark', 'Space']
const stopBits = ['One', 'Two']
const dataFormats = [
  { value: 'ABCD', label: 'ABCD（标准，高字在前）' },
  { value: 'CDAB', label: 'CDAB（低字在前）' },
  { value: 'BADC', label: 'BADC（字节交换）' },
  { value: 'DCBA', label: 'DCBA（全反转）' }
]

const f = ref({
  name: '', description: '',
  protocol: { name: 'Modbus', dialect: 'TCP' },
  connection: { endpoint: '127.0.0.1:502', connectTimeoutMs: 3000, requestTimeoutMs: 5000, retryCount: 3, retryIntervalMs: 1000, parameters: {} as Record<string, any> },
  status: 'Online'
})
const serial = ref({ unitId: 1, baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One', dataFormat: 'ABCD' })
// ADR-024 P3-1：S7 连接参数，值域与后端 S7Driver.ParseCpuType 一致
const s7CpuTypes = [
  { value: 'S-1200', label: 'S7-1200（默认，Rack 0 / Slot 1）' },
  { value: 'S-1500', label: 'S7-1500（Rack 0 / Slot 1）' },
  { value: 'S-300', label: 'S7-300（Rack 0 / Slot 2）' },
  { value: 'S-400', label: 'S7-400（Rack 0 / Slot 2）' }
]
const s7 = ref({ rack: 0, slot: 1, cpuType: 'S-1200', pingAddress: 'DB1.DBW0' })
const isRtu = computed(() => f.value.protocol.name === 'Modbus' && f.value.protocol.dialect === 'RTU')

function syncParams() {
  const p = f.value.connection.parameters
  if (f.value.protocol.name === 'Modbus') {
    // TCP 也保留 UnitId：后端 ModbusTcpDriver 按设备 UnitId 区分同端口上的从站
    p.DataFormat = serial.value.dataFormat
    p.UnitId = serial.value.unitId
    delete p.Rack
    delete p.Slot
    delete p.CpuType
    delete p.PingAddress
  } else {
    // ADR-024 P3-1：S7 必须落库 Rack/Slot/CpuType/PingAddress，否则后端只能用默认值（S7-300/400 必连不上）
    delete p.DataFormat
    delete p.UnitId
    p.Rack = s7.value.rack
    p.Slot = s7.value.slot
    p.CpuType = s7.value.cpuType
    p.PingAddress = s7.value.pingAddress
  }
  if (isRtu.value) {
    p.Transport = 'RTU'
    p.BaudRate = serial.value.baudRate
    p.DataBits = serial.value.dataBits
    p.Parity = serial.value.parity
    p.StopBits = serial.value.stopBits
  } else {
    delete p.Transport
    delete p.BaudRate
    delete p.DataBits
    delete p.Parity
    delete p.StopBits
  }
}

function loadSerialFromParams() {
  const p = f.value.connection.parameters ?? {}
  serial.value = {
    unitId: Number(p.UnitId) || 1,
    baudRate: Number(p.BaudRate) || 9600,
    dataBits: Number(p.DataBits) === 7 ? 7 : 8,
    parity: String(p.Parity || 'None'),
    stopBits: String(p.StopBits || 'One'),
    dataFormat: String(p.DataFormat || 'ABCD')
  }
}

function loadS7FromParams() {
  const p = f.value.connection.parameters ?? {}
  s7.value = {
    rack: Number(p.Rack ?? 0),
    slot: Number(p.Slot ?? 1),
    cpuType: String(p.CpuType || 'S-1200'),
    pingAddress: String(p.PingAddress || 'DB1.DBW0')
  }
}

function onProtocolChange() {
  if (f.value.protocol.name !== 'Modbus') {
    // ADR-024 P3-2：S7 仅 TCP（102 端口）；从 Modbus 默认地址切过来时同步换端口
    f.value.protocol.dialect = 'TCP'
    if (f.value.connection.endpoint === '127.0.0.1:502') f.value.connection.endpoint = '127.0.0.1:102'
  } else {
    if (!f.value.protocol.dialect) f.value.protocol.dialect = 'TCP'
    if (f.value.connection.endpoint === '127.0.0.1:102') f.value.connection.endpoint = '127.0.0.1:502'
  }
  if (!isRtu.value && f.value.connection.endpoint.startsWith('COM')) f.value.connection.endpoint = f.value.protocol.name === 'S7' ? '127.0.0.1:102' : '127.0.0.1:502'
}

function onDialectChange() {
  if (isRtu.value) {
    if (f.value.connection.endpoint === '127.0.0.1:502' || !f.value.connection.endpoint) {
      f.value.connection.endpoint = availablePorts.value[0] ?? 'COM3'
    }
  }
}

onMounted(async () => {
  try { availablePorts.value = await getSerialPorts() } catch { /* 忽略 */ }
  if (isEdit.value) {
    const d = await getDevice(route.params.id as string)
    if (d) {
      f.value = { ...f.value, ...d as any, protocol: { ...(d as any).protocol }, connection: { ...(d as any).connection, parameters: (d as any).connection?.parameters ?? {} } }
      loadSerialFromParams()
      loadS7FromParams()
    }
  }
})

async function save() {
  saving.value = true
  try {
    syncParams()
    const payload = JSON.parse(JSON.stringify(f.value))
    if (isEdit.value) await updateDevice(route.params.id as string, payload)
    else await createDevice(payload)
    ElMessage.success('保存成功')
    router.push('/devices')
  } catch (e: any) {
    saving.value = false
    const msg = e?.response?.data?.error?.message ?? e?.response?.data?.title ?? e?.message ?? '未知错误'
    const status = e?.response?.status ?? ''
    ElMessage.error(`保存失败 [${status}]: ${msg}`)
    console.error('DeviceForm save error:', e)
  }
}
async function testConn() {
  testing.value = true
  testResult.value = null
  try {
    syncParams()
    const payload = JSON.parse(JSON.stringify(f.value))
    testResult.value = await testConnection(payload)
  } catch (e: any) {
    testResult.value = { success: false, latencyMs: 0, error: e?.message ?? '请求失败' }
  } finally {
    testing.value = false
  }
}
</script>
<style scoped>
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); }
.form-row { display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:0 20px; }
.test-result { padding:10px 14px; border-radius:6px; font-size:13px; }
.test-ok { background:#f0fdf4; border:1px solid #86efac; color:#166534; }
.test-fail { background:#fef2f2; border:1px solid #fca5a5; color:#991b1b; }
</style>


