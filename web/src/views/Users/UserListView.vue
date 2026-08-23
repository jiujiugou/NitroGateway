<template>
  <div>
    <h2 class="page-title">用户管理</h2>
    <!-- ADR-066：用户 DB 化管理页（仅 Admin）——新增/改角色/启停/重置密码/删除即时生效，无需改配置重启 -->
    <div class="card" style="margin-bottom:16px">
      <div class="toolbar">
        <el-button type="primary" @click="openCreate">新增用户</el-button>
        <span class="toolbar-hint">密码最少 {{ passwordMin }} 位；不能移除最后一个启用的 Admin</span>
      </div>
    </div>
    <div class="card">
      <el-table :data="users" size="small" empty-text="暂无用户" max-height="560">
        <el-table-column prop="username" label="用户名" width="160" />
        <el-table-column label="角色" width="130">
          <template #default="{ row }">
            <el-select
              v-model="row.role"
              size="small"
              style="width:110px"
              :disabled="row.id === meId"
              @change="onRoleChange(row)"
            >
              <el-option v-for="r in roles" :key="r" :label="r" :value="r" />
            </el-select>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-switch
              v-model="row.isEnabled"
              :disabled="row.id === meId"
              active-text="启用"
              inactive-text="停用"
              @change="onEnabledChange(row)"
            />
          </template>
        </el-table-column>
        <el-table-column label="最近登录" width="180">
          <template #default="{ row }">{{ fmt(row.lastLoginAt) }}</template>
        </el-table-column>
        <el-table-column label="创建时间" width="180">
          <template #default="{ row }">{{ fmt(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="180">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openReset(row)">重置密码</el-button>
            <el-button link type="danger" size="small" :disabled="row.id === meId" @click="onDelete(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <!-- 新增用户 -->
    <el-dialog v-model="createVisible" title="新增用户" width="420">
      <el-form label-width="70px">
        <el-form-item label="用户名">
          <el-input v-model="createForm.username" placeholder="登录名" />
        </el-form-item>
        <el-form-item label="密码">
          <el-input v-model="createForm.password" type="password" show-password :placeholder="`至少 ${passwordMin} 位`" />
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="createForm.role" style="width:160px">
            <el-option v-for="r in roles" :key="r" :label="r" :value="r" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">取消</el-button>
        <el-button type="primary" :loading="creating" @click="onCreate">确定</el-button>
      </template>
    </el-dialog>

    <!-- 重置密码 -->
    <el-dialog v-model="resetVisible" :title="`重置密码：${resetTarget?.username ?? ''}`" width="420">
      <el-form label-width="70px">
        <el-form-item label="新密码">
          <el-input v-model="resetPassword" type="password" show-password :placeholder="`至少 ${passwordMin} 位`" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="resetVisible = false">取消</el-button>
        <el-button type="primary" :loading="resetting" @click="onReset">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  getUsers,
  createUser,
  changeUserRole,
  setUserEnabled,
  resetUserPassword,
  deleteUser,
  loadMe,
  type User
} from '../../api/user'

const roles = ['Admin', 'Operator', 'Viewer']
const passwordMin = 8

const users = ref<User[]>([])
// 当前登录者 id：不能对自己改角色/启停/删除，避免自锁（后端另有「最后一个 Admin」保护兜底）
const meId = ref<number | null>(null)

const createVisible = ref(false)
const createForm = ref({ username: '', password: '', role: 'Viewer' })
const creating = ref(false)

const resetVisible = ref(false)
const resetTarget = ref<User | null>(null)
const resetPassword = ref('')
const resetting = ref(false)

function errMsg(e: any, fallback: string) {
  return e?.response?.data?.error?.message ?? fallback
}

async function load() {
  try {
    users.value = await getUsers()
  } catch { /* 401 由拦截器跳登录，其余静默保留旧数据 */ }
}

async function onRoleChange(row: User) {
  try {
    await changeUserRole(row.id, row.role)
    ElMessage.success('角色已更新')
  } catch (e: any) {
    ElMessage.error(errMsg(e, '角色更新失败'))
  }
  await load() // 失败也刷新，回滚到真实角色
}

async function onEnabledChange(row: User) {
  try {
    await setUserEnabled(row.id, row.isEnabled)
    ElMessage.success(row.isEnabled ? '已启用' : '已停用')
  } catch (e: any) {
    ElMessage.error(errMsg(e, '状态更新失败'))
  }
  await load()
}

function openCreate() {
  createForm.value = { username: '', password: '', role: 'Viewer' }
  createVisible.value = true
}

async function onCreate() {
  const username = createForm.value.username.trim()
  if (!username) { ElMessage.warning('用户名不能为空'); return }
  if (createForm.value.password.length < passwordMin) {
    ElMessage.warning(`密码不能少于 ${passwordMin} 位`)
    return
  }
  creating.value = true
  try {
    await createUser({ username, password: createForm.value.password, role: createForm.value.role })
    ElMessage.success('用户已创建')
    createVisible.value = false
    await load()
  } catch (e: any) {
    ElMessage.error(errMsg(e, '创建失败'))
  } finally {
    creating.value = false
  }
}

function openReset(row: User) {
  resetTarget.value = row
  resetPassword.value = ''
  resetVisible.value = true
}

async function onReset() {
  if (!resetTarget.value) return
  if (resetPassword.value.length < passwordMin) {
    ElMessage.warning(`密码不能少于 ${passwordMin} 位`)
    return
  }
  resetting.value = true
  try {
    await resetUserPassword(resetTarget.value.id, resetPassword.value)
    ElMessage.success('密码已重置')
    resetVisible.value = false
  } catch (e: any) {
    ElMessage.error(errMsg(e, '重置失败'))
  } finally {
    resetting.value = false
  }
}

async function onDelete(row: User) {
  try {
    await ElMessageBox.confirm(`确认删除用户「${row.username}」？此操作不可恢复。`, '删除确认', { type: 'warning' })
  } catch { return }
  try {
    await deleteUser(row.id)
    ElMessage.success('已删除')
    await load()
  } catch (e: any) {
    ElMessage.error(errMsg(e, '删除失败'))
  }
}

function fmt(t?: string | null) {
  return t ? new Date(t).toLocaleString() : '-'
}

onMounted(async () => {
  meId.value = loadMe()?.id ?? null
  await load()
})
</script>

<style scoped>
.page-title { margin-bottom:20px; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:8px; padding:20px; }
.toolbar { display:flex; align-items:center; gap:12px; flex-wrap:wrap; }
.toolbar-hint { color:#a0aec0; font-size:12px; }
</style>
