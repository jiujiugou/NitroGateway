<template>
  <div class="page-head"><h2 class="page-title">点位管理</h2><el-button type="primary" @click="showForm=true">+ 添加点位</el-button></div>
  <div class="card">
    <el-table :data="points" row-key="id" size="small">
      <el-table-column prop="name" label="名称" /><el-table-column prop="address" label="地址" width="140" />
      <el-table-column prop="dataType" label="类型" width="90" /><el-table-column prop="access" label="权限" width="90" />
      <el-table-column label="缩放" width="150"><template #default="{row}">×{{ row.scaleFactor }} +{{ row.scaleOffset }}</template></el-table-column>
      <el-table-column prop="deadband" label="死区" width="70" /><el-table-column label="启用" width="60"><template #default="{row}"><el-switch :model-value="row.enabled" disabled size="small" /></template></el-table-column>
      <el-table-column label="操作" width="80"><template #default="{row}"><el-button size="small" text type="danger" @click="handleDel(row.id)">删除</el-button></template></el-table-column>
    </el-table>
  </div>
  <el-dialog v-model="showForm" title="添加点位" width="520px">
    <el-form :model="pf" label-position="top">
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:0 16px">
        <el-form-item label="名称"><el-input v-model="pf.name" /></el-form-item>
        <el-form-item label="地址"><el-input v-model="pf.address" /></el-form-item>
        <el-form-item label="数据类型"><el-select v-model="pf.dataType" style="width:100%"><el-option v-for="t in types" :key="t" :label="t" :value="t" /></el-select></el-form-item>
        <el-form-item label="权限"><el-select v-model="pf.access" style="width:100%"><el-option label="只读" value="ReadOnly" /><el-option label="只写" value="WriteOnly" /><el-option label="读写" value="ReadWrite" /></el-select></el-form-item>
        <el-form-item label="缩放系数"><el-input-number v-model="pf.scaleFactor" :min="0" :step="0.1" /></el-form-item>
        <el-form-item label="缩放偏移"><el-input-number v-model="pf.scaleOffset" :step="0.1" /></el-form-item>
        <el-form-item label="死区"><el-input-number v-model="pf.deadband" :min="0" :step="0.1" /></el-form-item>
        <el-form-item label="采集间隔(ms)"><el-input-number v-model="pf.scanIntervalMs" :min="0" /></el-form-item>
      </div>
    </el-form>
    <template #footer><el-button @click="showForm=false">取消</el-button><el-button type="primary" @click="add">添加</el-button></template>
  </el-dialog>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { getPoints, addPoint, deletePoint } from '../../api/devices'
import type { DevicePoint } from '../../api/types'
const route = useRoute(); const deviceId = route.params.deviceId as string
const points = ref<DevicePoint[]>([]); const showForm = ref(false)
const pf = ref({name:'',address:'40001',dataType:'Float',access:'ReadOnly',scaleFactor:1,scaleOffset:0,deadband:0,scanIntervalMs:0,enabled:true})
const types = ['Bool','Byte','Int16','UInt16','Int32','UInt32','Int64','UInt64','Float','Double','String']
onMounted(async () => { try { points.value = await getPoints(deviceId) } catch {} })
async function add() { try { const p = await addPoint(deviceId, pf.value as any); if (p) { points.value.push(p); showForm.value=false } } catch {} }
async function handleDel(id: string) { try { await deletePoint(deviceId,id); points.value=points.value.filter(p=>p.id!==id) } catch {} }
</script>
<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); overflow:hidden; }
</style>
