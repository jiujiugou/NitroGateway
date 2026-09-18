<template>
  <h2 class="page-title" style="margin-bottom:20px">{{ isEdit ? '编辑 OPC UA 设备' : '添加 OPC UA 设备' }}</h2>
  <div class="card"><div style="padding:24px">
    <el-form :model="f" label-position="top">
      <div class="form-row">
        <el-form-item label="设备名称"><el-input v-model="f.name" placeholder="例如：一号车间 OPC UA 服务器" /></el-form-item>
        <el-form-item label="协议">
          <el-input :model-value="'OPC UA'" disabled />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="f.status" style="width:100%">
            <el-option label="在线" value="Online" />
            <el-option label="离线" value="Offline" />
            <el-option label="未知" value="Unknown" />
          </el-select>
        </el-form-item>
      </div>

      <div class="form-row">
        <el-form-item label="连接端点">
          <!-- OPC UA 走 opc.tcp 二进制传输（12-OPC-UA接入设计.md S6） -->
          <el-input v-model="f.connection.endpoint" placeholder="opc.tcp://127.0.0.1:4840" style="width:100%" />
        </el-form-item>
        <el-form-item label="连接超时(ms)"><el-input-number v-model="f.connection.connectTimeoutMs" :min="100" style="width:100%" /></el-form-item>
        <el-form-item label="请求超时(ms)"><el-input-number v-model="f.connection.requestTimeoutMs" :min="100" style="width:100%" /></el-form-item>
      </div>

      <!-- ADR-073 层4：OPC UA 安全参数（SecurityPolicy/SecurityMode/UserName/Password）。
           None 需显式选择；策略/模式留空=未声明（键不落库，后端按安全优先默认）。
           编辑态密码框留空=不修改（响应 hasPassword 指示是否已设；密文永不回填）。 -->
      <div class="ua-security">
        <div class="ua-security-title">OPC UA 安全（ADR-073 层4）</div>
        <div class="form-row">
          <el-form-item label="安全策略">
            <el-select v-model="uaSec.securityPolicy" style="width:100%" clearable placeholder="自动（默认安全优先）">
              <el-option label="None（无加密，仅显式声明才允许连接）" value="None" />
              <el-option label="Basic128Rsa15" value="Basic128Rsa15" />
              <el-option label="Basic256" value="Basic256" />
              <el-option label="Basic256Sha256（推荐）" value="Basic256Sha256" />
            </el-select>
          </el-form-item>
          <el-form-item label="安全模式">
            <el-select v-model="uaSec.securityMode" style="width:100%" clearable placeholder="自动">
              <el-option label="None（无签名/加密）" value="None" />
              <el-option label="Sign（仅签名）" value="Sign" />
              <el-option label="SignAndEncrypt（签名+加密，推荐）" value="SignAndEncrypt" />
            </el-select>
          </el-form-item>
        </div>
        <div class="form-row">
          <el-form-item label="用户名">
            <el-input v-model="uaSec.userName" placeholder="服务器用户名（匿名请留空）" autocomplete="off" />
          </el-form-item>
          <el-form-item label="密码">
            <el-input
              v-model="uaSec.password"
              type="password"
              show-password
              autocomplete="new-password"
              :placeholder="hasPassword ? '已设密码，留空=不修改' : '服务器密码'"
            />
          </el-form-item>
        </div>
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
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getDevice, createDevice, updateDevice, testConnection } from '../../api/devices'

// ADR-073 层4：OPC UA 专属设备表单（独立分区，固定 OPC UA 协议，不出现 Modbus/S7 字段）。
const route = useRoute(); const router = useRouter()
const isEdit = ref(!!route.params.id)
const saving = ref(false)
const testing = ref(false)
const testResult = ref<{ success: boolean; latencyMs: number; error?: string } | null>(null)

const f = ref({
  name: '', description: '',
  protocol: { name: 'OPC UA' },
  connection: { endpoint: 'opc.tcp://127.0.0.1:4840', connectTimeoutMs: 3000, requestTimeoutMs: 5000, retryCount: 3, retryIntervalMs: 1000, parameters: {} as Record<string, any> },
  status: 'Online'
})
// 空串 = 未声明（键不落库，后端默认安全优先）；hasPassword 由响应回填（密文永不回填），编辑态密码留空 = 不修改。
const uaSec = ref({ securityPolicy: '', securityMode: '', userName: '', password: '' })
const hasPassword = ref(false)

function loadUaFromParams() {
  const p = f.value.connection.parameters ?? {}
  uaSec.value = {
    securityPolicy: String(p.SecurityPolicy || ''),
    securityMode: String(p.SecurityMode || ''),
    userName: String(p.UserName || ''),
    password: '' // 密文永不回填；编辑态留空 = 不修改（后端合并既有凭据）
  }
}

// 仅同步 OPC UA 层4 安全键；空值删键（密码留空=不改，由后端 PreserveExistingCredentialOnEdit 保留既有）。
// 顺带清掉切换协议时期残留的 Modbus/S7 参数键，避免污染连接（12-OPC-UA接入设计.md S6）。
function buildParams() {
  const p = f.value.connection.parameters
  delete p.DataFormat
  delete p.UnitId
  delete p.Rack
  delete p.Slot
  delete p.CpuType
  delete p.PingAddress
  delete p.Transport
  delete p.BaudRate
  delete p.DataBits
  delete p.Parity
  delete p.StopBits
  if (uaSec.value.securityPolicy) p.SecurityPolicy = uaSec.value.securityPolicy
  else delete p.SecurityPolicy
  if (uaSec.value.securityMode) p.SecurityMode = uaSec.value.securityMode
  else delete p.SecurityMode
  if (uaSec.value.userName) p.UserName = uaSec.value.userName
  else delete p.UserName
  if (uaSec.value.password) p.Password = uaSec.value.password
  else delete p.Password
}

onMounted(async () => {
  if (isEdit.value) {
    const d = await getDevice(route.params.id as string)
    if (d) {
      if (d.protocol.name !== 'OPC UA') { router.replace(`/devices/${d.id}`); return }
      f.value = { ...f.value, ...d as any, protocol: { ...(d as any).protocol }, connection: { ...(d as any).connection, parameters: (d as any).connection?.parameters ?? {} } }
      hasPassword.value = !!(d as any).connection?.hasPassword
      loadUaFromParams()
    }
  }
})

async function save() {
  saving.value = true
  try {
    buildParams()
    const payload = JSON.parse(JSON.stringify(f.value))
    if (isEdit.value) await updateDevice(route.params.id as string, payload)
    else await createDevice(payload)
    ElMessage.success('保存成功')
    router.push(isEdit.value ? `/opcua/${route.params.id}` : '/opcua')
  } catch (e: any) {
    saving.value = false
    const msg = e?.response?.data?.error?.message ?? e?.response?.data?.title ?? e?.message ?? '未知错误'
    const status = e?.response?.status ?? ''
    ElMessage.error(`保存失败 [${status}]: ${msg}`)
    console.error('OpcUaDeviceForm save error:', e)
  }
}
async function testConn() {
  testing.value = true
  testResult.value = null
  try {
    buildParams()
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
.ua-security { margin-bottom:18px; padding:14px 16px 4px; border:1px dashed var(--border); border-radius:8px; }
.ua-security-title { font-size:13px; font-weight:600; color:#4a5568; margin-bottom:12px; }
</style>
