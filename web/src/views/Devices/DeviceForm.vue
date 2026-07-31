<template>
  <h2 class="page-title" style="margin-bottom:20px">{{ isEdit ? '编辑设备' : '添加设备' }}</h2>
  <div class="card"><div style="padding:24px">
    <el-form :model="f" label-position="top">
      <div class="form-row">
        <el-form-item label="设备名称"><el-input v-model="f.name" placeholder="例如：一号车间 PLC" /></el-form-item>
        <el-form-item label="协议"><el-select v-model="f.protocol.name" style="width:100%"><el-option label="Modbus" value="Modbus" /><el-option label="OPC UA" value="OPC UA" /><el-option label="S7" value="S7" /><el-option label="Mitsubishi" value="Mitsubishi" /></el-select></el-form-item>
        <el-form-item label="方言"><el-input v-model="f.protocol.dialect" placeholder="TCP / RTU" /></el-form-item>
        <el-form-item label="状态"><el-select v-model="f.status" style="width:100%"><el-option label="在线" value="Online" /><el-option label="离线" value="Offline" /><el-option label="未知" value="Unknown" /></el-select></el-form-item>
      </div>
      <div class="form-row">
        <el-form-item label="连接地址"><el-input v-model="f.connection.endpoint" placeholder="192.168.1.100:502" /></el-form-item>
        <el-form-item label="连接超时(ms)"><el-input-number v-model="f.connection.connectTimeoutMs" :min="100" /></el-form-item>
        <el-form-item label="请求超时(ms)"><el-input-number v-model="f.connection.requestTimeoutMs" :min="100" /></el-form-item>
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
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getDevice, createDevice, updateDevice, testConnection } from '../../api/devices'
const route = useRoute(); const router = useRouter()
const isEdit = ref(!!route.params.id)
const saving = ref(false)
const testing = ref(false)
const testResult = ref<{ success: boolean; latencyMs: number; error?: string } | null>(null)
const f = ref({name:'',description:'',protocol:{name:'Modbus',dialect:'TCP'},connection:{endpoint:'127.0.0.1:502',connectTimeoutMs:3000,requestTimeoutMs:5000,retryCount:3,retryIntervalMs:1000,parameters:{}},status:'Online'})
onMounted(async () => { if (isEdit.value) { const d = await getDevice(route.params.id as string); if (d) { f.value = { ...f.value, ...d as any, protocol: {...(d as any).protocol}, connection: {...(d as any).connection, parameters: (d as any).connection?.parameters ?? {}} } } } })
async function save() {
  saving.value = true
  try {
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
