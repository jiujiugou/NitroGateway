import axios from 'axios'
import { ElMessage } from 'element-plus'

const client = axios.create({
  // 写死后端地址会导致生产部署下浏览器直连自身 localhost:5100 而全部失败
  baseURL: '/api',
  timeout: 10000,
  headers: { 'Content-Type': 'application/json' }
})

// 请求拦截器：自动带 Token
client.interceptors.request.use(config => {
  const token = localStorage.getItem('token')
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// 响应拦截器：401 跳登录
client.interceptors.response.use(
  r => r,
  err => {
    console.error('API Error:', err.message)
    if (err.response?.status === 401) {
      localStorage.removeItem('token')
      if (window.location.pathname !== '/login')
        window.location.href = '/login'
    } else if (err.response?.status === 403) {
      ElMessage.error(err.response?.data?.error?.message ?? '无权限执行该操作')
    }
    return Promise.reject(err)
  }
)

export default client
