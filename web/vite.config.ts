import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    // 后端 Kestrel 仅绑定 IPv4（appsettings.json 的 Urls=http://0.0.0.0:5100），
    // Node>=17 会把 localhost 优先解析为 ::1，代理连 ::1:5100 被拒 → 502/无法建立连接；
    // 显式用 127.0.0.1 强制走 IPv4（ADR-063）。
    proxy: { '/api': 'http://127.0.0.1:5100', '/hubs': { target: 'http://127.0.0.1:5100', ws: true } }
  },
  optimizeDeps: { include: ['element-plus', '@element-plus/icons-vue', 'axios', 'echarts', 'pinia', 'vue-router', '@microsoft/signalr'] }
})
