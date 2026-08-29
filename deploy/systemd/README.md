# NitroGateway systemd 服务化 + 看门狗

> 分支：`feat/edge-device-systemd`。目标：把"Docker compose 部署的服务器应用形态"往"Linux 边缘设备形态"靠一层——开机自启 + 崩溃自愈 + 健康看门狗。半天时间盒。

## 这个目录是什么

| 文件 | 作用 |
| --- | --- |
| `nitrogateway.service` | 开机拉起整套 compose 栈 / 关机优雅停栈（容器级崩溃自愈仍由 compose 的 `restart: unless-stopped` 负责） |
| `nitrogateway-watchdog.service` + `.sh` | 主机级看门狗：周期探测 `http://localhost:5100/healthz`，连续 3 次失败 → `docker compose restart gateway` |
| `install.sh` | 一键安装 + 开机自启 + 验证 |

分层逻辑（面试一句话版）：
- **容器级**：compose `restart: unless-stopped` → 单容器崩溃自动拉起（已有）
- **服务级**：systemd `nitrogateway.service` → 断电重启/开机后整套栈自愈
- **健康级**：`nitrogateway-watchdog` → 进程活着但 `/healthz` 不健康时主动重启

## 安装（在你的 Linux 部署机上）

```bash
# 把 deploy/systemd 拷到部署机（或直接 clone 本分支），然后：
sudo bash install.sh /opt/nitrogateway   # 第二个参数 = 你放 docker-compose.yml 的目录
```

要求：宿主机有 `curl`、`docker compose`（v2 插件）；栈目录里有 `docker-compose.yml`。

## 验证（故障注入，证明它真能自愈）

```bash
# 1) 看门狗活着
systemctl status nitrogateway-watchdog

# 2) 拔掉 gateway 容器（模拟崩溃/被杀）
docker compose -f /opt/nitrogateway/docker-compose.yml kill gateway

# 3) 观察：compose restart 或 watchdog 在 ~30s 内把它拉回来
watch docker compose -f /opt/nitrogateway/docker-compose.yml ps
journalctl -u nitrogateway-watchdog -f

# 4) 模拟断电重启
sudo reboot
# 开机后：systemctl status nitrogateway 应为 active(exited)，三个容器自动起来
```

把这个验证过程截图 + 贴 `journalctl` 输出 → 这就是"断电重启后它能自己活过来吗"的证据。

## 卸载

```bash
sudo systemctl disable --now nitrogateway nitrogateway-watchdog
sudo rm /etc/systemd/system/nitrogateway.service /etc/systemd/system/nitrogateway-watchdog.service
sudo rm /usr/local/bin/nitrogateway-watchdog.sh /etc/default/nitrogateway
sudo systemctl daemon-reload
```

## 面试怎么讲（60 秒版）

"我的网关默认以 Docker compose 部署，崩溃自愈靠 `restart: unless-stopped` + 4 个健康检查（MQTT/SQLite/Disk/HTTP `/healthz`）。为了往设备形态靠，我加了一层 systemd：`nitrogateway.service` 负责开机拉起整栈，`nitrogateway-watchdog` 主机级周期探测 `/healthz`，连续 3 次失败就 `docker compose restart gateway`。故障注入验证过：`kill` 掉 gateway 容器 30 秒内自愈，`reboot` 后整栈自动恢复。"

## 下一步（可选，非本分支范围）

真正的"瘦设备"形态 = 不用 Docker，`dotnet publish` 自包含二进制 + mosquitto 原生 systemd 服务直接跑，OS 裁剪到 buildroot/精简 Linux。属于 roadmap，不必现在做。
