<template>
  <h2 class="page-title" style="margin-bottom:20px">{{ isEdit ? '编辑设备' : '添加设备' }}</h2>
  <div class="card"><div style="padding:24px">
    <el-form :model="f" label-position="top">
      <div class="form-row">
        <el-form-item label="设备名称"><el-input v-model="f.name" placeholder="例如：一号车间 PLC" /></el-form-item>
        <el-form-item label="协议"><el-select v-model="f.protocol.name" style="width:100%"><el-option label="Modbus" value="Modbus" /><el-option label="OPC UA" value="OPC UA" /><el-option label="S7" value="S7" /></el-select></el-form-item>
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
        <el-button type="primary" @click="save">保存</el-button>
        <el-button @click="$router.back()">取消</el-button>
      </div>
    </el-form>
  </div></div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getDevice, createDevice, updateDevice } from '../../api/devices'
const route = useRoute(); const router = useRouter()
const isEdit = ref(!!route.params.id)
const f = ref({name:'',description:'',protocol:{name:'Modbus',dialect:'TCP'},connection:{endpoint:'127.0.0.1:502',connectTimeoutMs:3000,requestTimeoutMs:5000,retryCount:3,retryIntervalMs:1000},status:'Online'})
onMounted(async () => { if (isEdit.value) { const d = await getDevice(route.params.id as string); if (d) f.value = d as any } })
async function save() { try { if (isEdit.value) await updateDevice(route.params.id as string, f.value as any); else await createDevice(f.value as any); router.push('/devices') } catch {} }
</script>
<style scoped>
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); }
.form-row { display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:0 20px; }
</style>
