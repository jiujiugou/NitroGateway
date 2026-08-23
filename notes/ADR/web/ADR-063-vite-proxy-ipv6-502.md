# ADR-063：Vite 代理 localhost 解析到 ::1 导致 webui 与服务器 502 无法建立连接

## 问题
webui（`npm run dev`）启动后所有 `/api/*`、`/hubs/*` 请求返回 502，SignalR 协商失败，前端「无法与服务器建立连接」。

## 根因 / 代码位置
- 后端 Kestrel 仅绑定 IPv4：`src/NitroGateway.Webapi/appsettings.json` 的 `Urls=http://0.0.0.0:5100`（未监听 `::1`）。
- Vite 代理 target 用 `http://localhost:5100`：`web/vite.config.ts`（Node ≥17 起 `localhost` 优先解析为 `::1`）。
- 实测：Node v22.12.0 下代理报 `Error: connect ECONNREFUSED ::1:5100`（vite 日志），直连 `127.0.0.1:5100` 返回 401（正常），`[::1]:5100` 拒绝。

## 修复方向（已完成）
`web/vite.config.ts` 代理 target 改为 `http://127.0.0.1:5100`，强制走 IPv4，与后端绑定一致；`/hubs` 的 `ws:true` 同样指向 127.0.0.1。仅开发代理配置，不影响生产（生产走 nginx `/api/` 反代）。
