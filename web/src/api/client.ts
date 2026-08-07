import axios from 'axios'

const client = axios.create({
  // ADR-007 P1-1：相对路径，dev 走 Vite 代理(/api → 5100)，生产走 nginx /api/ 反代；
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
    }
    return Promise.reject(err)
  }
)

export default client
