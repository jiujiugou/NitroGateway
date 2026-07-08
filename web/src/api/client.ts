import axios from 'axios'

const client = axios.create({
  baseURL: 'http://localhost:5100/api',
  timeout: 10000,
  headers: { 'Content-Type': 'application/json' }
})

client.interceptors.response.use(
  r => r,
  err => { console.error('API Error:', err.message); return Promise.reject(err) }
)

export default client
