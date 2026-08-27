import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    // 后端 Kestrel 仅绑定 IPv4（appsettings.json 的 Urls=http://0.0.0.0:5100），
    // Node>=17 会把 localhost 优先解析为 ::1，代理连 ::1:5100 被拒 → 502/无法建立连接；
    // 显式用 127.0.0.1 强制走 IPv4（ADR-063）。
    // ADR-068：VS Code 端口转发会在本机占用 127.0.0.1:5100（NodeService 子进程），
    // 遮挡本地后端（TCP 能连上但无响应）。后端绑 0.0.0.0，改用 127.0.0.2 回环同样可达，绕开该占用。
    proxy: { '/api': 'http://127.0.0.2:5100', '/hubs': { target: 'http://127.0.0.2:5100', ws: true } }
  },
  optimizeDeps: { include: ['element-plus', '@element-plus/icons-vue', 'axios', 'echarts', 'pinia', 'vue-router', '@microsoft/signalr'] }
})
