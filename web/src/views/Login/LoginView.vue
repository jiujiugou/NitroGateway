<template>
  <div class="login-page">
    <form class="login-card" @submit.prevent="handleLogin">
      <div class="login-header">
        <div class="login-icon">⚡</div>
        <h1>NitroGateway</h1>
        <p>工业协议网关管理控制台</p>
      </div>
      <el-input v-model="username" placeholder="用户名" size="large" />
      <el-input v-model="password" type="password" placeholder="密码" size="large" show-password />
      <el-button type="primary" size="large" native-type="submit" :loading="loading" style="width:100%">
        登 录
      </el-button>
      <div v-if="error" class="login-error">{{ error }}</div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import client from '../../api/client'

const router = useRouter()
const username = ref('')
const password = ref('')
const loading = ref(false)
const error = ref('')

async function handleLogin() {
  if (!username.value || !password.value) { error.value = '请输入用户名和密码'; return }
  loading.value = true; error.value = ''
  try {
    const { data } = await client.post('/auth/login', { username: username.value, password: password.value })
    if (data.data?.token) {
      localStorage.setItem('token', data.data.token)
      router.push('/dashboard')
    } else {
      error.value = data.error?.message ?? '登录失败'
    }
  } catch (e: any) {
    error.value = e?.response?.data?.error?.message ?? '登录失败'
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.login-page { display:flex; align-items:center; justify-content:center; height:100vh; background:#f0f2f5; }
.login-card { width:380px; padding:40px; background:#fff; border-radius:12px; box-shadow:0 4px 24px rgba(0,0,0,.08); display:flex; flex-direction:column; gap:18px; }
.login-header { text-align:center; margin-bottom:8px; }
.login-icon { font-size:40px; }
.login-header h1 { margin:8px 0 4px; font-size:22px; color:#1a202c; }
.login-header p { color:#a0aec0; font-size:13px; margin:0; }
.login-error { color:#f56c6c; font-size:13px; text-align:center; }
</style>
