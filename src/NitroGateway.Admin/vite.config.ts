import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5000', '/hubs': { target: 'http://localhost:5000', ws: true } }
  },
  optimizeDeps: { include: ['element-plus', '@element-plus/icons-vue', 'axios', 'echarts', 'pinia', 'vue-router', '@microsoft/signalr'] }
})
