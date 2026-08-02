<template>
  <h2 class="page-title" style="margin-bottom:20px">{{ isEdit ? '编辑设备' : '添加设备' }}</h2>
  <div class="card"><div style="padding:24px">
    <el-form :model="f" label-position="top">
      <div class="form-row">
        <el-form-item label="设备名称"><el-input v-model="f.name" placeholder="例如：一号车间 PLC" /></el-form-item>
        <el-form-item label="协议">
          <el-select v-model="f.protocol.name" style="width:100%" @change="onProtocolChange">
            <el-option label="Modbus" value="Modbus" />
            <el-option label="OPC UA" value="OPC UA" />
            <el-option label="S7" value="S7" />
            <el-option label="Mitsubishi" value="Mitsubishi" />
          </el-select>
        </el-form-item>
        <el-form-item label="传输方式">
          <el-select v-if="f.protocol.name === 'Modbus'" v-model="f.protocol.dialect" style="width:100%" @change="onDialectChange">
            <el-option label="TCP（网口）" value="TCP" />
            <el-option label="RTU（串口）" value="RTU" />
          </el-select>
          <el-input v-else v-model="f.protocol.dialect" placeholder="TCP / RTU" />
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
        <el-form-item label="连接地址"><el-input v-model="f.connection.endpoint" placeholder="192.168.1.100:502" /></el-form-item>
        <el-form-item label="连接超时(ms)"><el-input-number v-model="f.connection.connectTimeoutMs" :min="100" /></el-form-item>
        <el-form-item label="请求超时(ms)"><el-input-number v-model="f.connection.requestTimeoutMs" :min="100" /></el-form-item>
        <el-form-item label="字节序">
          <el-select v-model="serial.dataFormat" style="width:100%">
            <el-option v-for="fmt in dataFormats" :key="fmt.value" :label="fmt.label" :value="fmt.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="重试次数"><el-input-number v-model="f.connection.retryCount" :min="0" /></el-form-item>
        <el-form-item label="重试间隔(ms)"><el-input-number v-model="f.connection.retryIntervalMs" :min="100" /></el-form-item>
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
const isRtu = computed(() => f.value.protocol.name === 'Modbus' && f.value.protocol.dialect === 'RTU')

function syncParams() {
  const p = f.value.connection.parameters
  if (f.value.protocol.name === 'Modbus') p.DataFormat = serial.value.dataFormat
  else delete p.DataFormat
  if (isRtu.value) {
    p.Transport = 'RTU'
    p.UnitId = serial.value.unitId
    p.BaudRate = serial.value.baudRate
    p.DataBits = serial.value.dataBits
    p.Parity = serial.value.parity
    p.StopBits = serial.value.stopBits
  } else {
    delete p.Transport
    delete p.UnitId
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

function onProtocolChange() {
  if (f.value.protocol.name !== 'Modbus') f.value.protocol.dialect = ''
  else if (!f.value.protocol.dialect) f.value.protocol.dialect = 'TCP'
  if (!isRtu.value && f.value.connection.endpoint.startsWith('COM')) f.value.connection.endpoint = '127.0.0.1:502'
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
