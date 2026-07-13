import axios from 'axios'

const client = axios.create({
  baseURL: 'http://localhost:5100/api',
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
