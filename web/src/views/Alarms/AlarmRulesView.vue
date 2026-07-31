<template>
  <div class="page-head">
    <h2 class="page-title">告警规则</h2>
    <el-button type="primary" @click="openAdd">+ 添加规则</el-button>
  </div>

  <div class="card">
    <el-table :data="rules" row-key="id" size="small" empty-text="暂无告警规则">
      <el-table-column label="设备" min-width="120">
        <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
      </el-table-column>
      <el-table-column label="点位" min-width="100">
        <template #default="{ row }">{{ pointName(row.deviceId, row.pointId) }}</template>
      </el-table-column>
      <el-table-column label="条件" width="160">
        <template #default="{ row }">{{ row.operator }} {{ row.threshold }}{{ row.thresholdUpper ? ' ~ '+row.thresholdUpper : '' }}</template>
      </el-table-column>
      <el-table-column label="时长" width="80">
        <template #default="{ row }">{{ row.durationSeconds }}s</template>
      </el-table-column>
      <el-table-column label="等级" width="100">
        <template #default="{ row }">
          <el-tag :type="sevTag(row.severity)" size="small">{{ row.severity }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="启用" width="60">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" disabled size="small" />
        </template>
      </el-table-column>
      <el-table-column label="操作" width="140">
        <template #default="{ row }">
          <el-button size="small" text type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button size="small" text type="danger" @click="handleDel(row.id)">删除</el-button>
        </template>
      </el-table-column>
    </el-table>
  </div>

  <!-- 添加/编辑对话框 -->
  <el-dialog v-model="showForm" :title="editingId ? '编辑规则' : '添加规则'" width="560px">
    <el-form :model="rf" label-position="top">
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:0 16px">
        <el-form-item label="设备">
          <el-select v-model="rf.deviceId" style="width:100%" @change="onDeviceChange" filterable placeholder="选择设备">
            <el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="点位">
          <el-select v-model="rf.pointId" style="width:100%" filterable placeholder="选择点位">
            <el-option v-for="p in currentDevicePoints" :key="p.id" :label="`${p.name} (${p.address})`" :value="p.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="运算符">
          <el-select v-model="rf.operator" style="width:100%">
            <el-option label=">" value=">" />
            <el-option label=">=" value=">=" />
            <el-option label="<" value="<" />
            <el-option label="<=" value="<=" />
            <el-option label="==" value="==" />
            <el-option label="!=" value="!=" />
            <el-option label="Between" value="Between" />
          </el-select>
        </el-form-item>
        <el-form-item :label="rf.operator==='Between' ? '下限' : '阈值'">
          <el-input-number v-model="rf.threshold" :step="1" style="width:100%" />
        </el-form-item>
        <el-form-item v-if="rf.operator==='Between'" label="上限">
          <el-input-number v-model="rf.thresholdUpper" :step="1" style="width:100%" />
        </el-form-item>
        <el-form-item label="持续时间(秒)">
          <el-input-number v-model="rf.durationSeconds" :min="0" :step="5" style="width:100%" />
        </el-form-item>
        <el-form-item label="严重等级">
          <el-select v-model="rf.severity" style="width:100%">
            <el-option label="Info" value="Info" />
            <el-option label="Warning" value="Warning" />
            <el-option label="Critical" value="Critical" />
            <el-option label="Emergency" value="Emergency" />
          </el-select>
        </el-form-item>
        <el-form-item label="消息模板">
          <el-input v-model="rf.messageTemplate" placeholder="{value} 超过阈值 {threshold}" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="rf.enabled" />
        </el-form-item>
      </div>
    </el-form>
    <template #footer>
      <el-button @click="showForm=false">取消</el-button>
      <el-button type="primary" @click="save">保存</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getDevices, getPoints } from '../../api/devices'
import { getAlarmRules, createAlarmRule, updateAlarmRule, deleteAlarmRule } from '../../api/alarms'
import type { Device, DevicePoint, AlarmRule } from '../../api/types'

const devices = ref<Device[]>([])
const points = ref<Record<string, DevicePoint[]>>({})  // deviceId → points
const rules = ref<AlarmRule[]>([])
const showForm = ref(false)
const editingId = ref<string | null>(null)

const makeEmpty = () => ({
  deviceId: '', pointId: '', operator: '>', threshold: 80,
  thresholdUpper: null as number | null, durationSeconds: 0,
  severity: 'Warning', messageTemplate: '', enabled: true
})
const rf = ref<Record<string, any>>(makeEmpty())

const currentDevicePoints = computed(() => points.value[rf.value.deviceId] ?? [])

function deviceName(id: string) { return devices.value.find(d => d.id === id)?.name ?? id }
function pointName(deviceId: string, pointId: string) {
  return points.value[deviceId]?.find(p => p.id === pointId)?.name ?? pointId
}

onMounted(async () => {
  try { devices.value = await getDevices() } catch {}
  try { rules.value = await getAlarmRules() } catch {}
  // 预载所有设备的点位
  for (const d of devices.value) {
    try { points.value[d.id] = await getPoints(d.id) } catch {}
  }
})

async function onDeviceChange() {
  const did = rf.value.deviceId
  if (!points.value[did]) {
    try { points.value[did] = await getPoints(did) } catch {}
  }
  rf.value.pointId = ''
}

function openAdd() {
  editingId.value = null
  rf.value = makeEmpty()
  showForm.value = true
}

function openEdit(row: AlarmRule) {
  editingId.value = row.id
  rf.value = { ...row }
  showForm.value = true
}

async function save() {
  try {
    const payload = {
      ...rf.value,
      deviceId: rf.value.deviceId,
      pointId: rf.value.pointId,
      thresholdUpper: rf.value.operator === 'Between' ? rf.value.thresholdUpper : null
    }
    if (editingId.value) {
      const r = await updateAlarmRule(editingId.value, payload)
      if (r) {
        const idx = rules.value.findIndex(x => x.id === editingId.value)
        if (idx >= 0) rules.value.splice(idx, 1, r)
      }
    } else {
      const r = await createAlarmRule(payload)
      if (r) rules.value.push(r)
    }
    showForm.value = false
  } catch {}
}

async function handleDel(id: string) {
  try { await deleteAlarmRule(id); rules.value = rules.value.filter(r => r.id !== id) } catch {}
}

function sevTag(s: string) {
  return s === 'Critical' || s === 'Emergency' ? 'danger' : s === 'Warning' ? 'warning' : 'info'
}
</script>

<style scoped>
.page-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.page-title { margin-bottom:0; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
</style>
