# ADR-058: CI/CD 流水线（CD 扩展，GHCR 镜像发布）

- 日期: 2026-08-19 | 状态: 已实施
- 背景: ADR-010 已落地 CI（push/PR 的 build+test）

## Context

2026-08-19 Docker 部署实机验证通过后，用户问「如何构建 CI/CD 流水线」——需要把「构建镜像 + 推送 GHCR」接进流水线，让边缘网关部署机直接 pull 发布产物，不再现场构建，消除「本地能跑、部署机编译环境不一致」风险。

## Decision

- D1 在 ci.yml 上扩展为 CI/CD（不改名，触发仍统一入口）：触发 push: [master] + tags: ['v*'] + pull_request（PR 只跑 CI，不发布镜像）。
- D2 新增 validate-compose job（CI）：占位环境变量下 docker compose config -q 校验 docker-compose.yml / +cd / center / monitoring 4 种形态，仅校验不运行。
- D3 新增 build-images job（CD）：if push && (refs/heads/master || refs/tags/v*)，needs: [validate-compose, build-server, build-windows]（测试全绿才发布）；Buildx + type=gha 缓存 + docker/login-action@v3 用 secrets.GITHUB_TOKEN（默认可用，无需额外 PAT）登录 GHCR。
- D4 镜像与 tag：gateway（根 Dockerfile）+ web（web/Dockerfile）→ ghcr.io/jiujiugou/nitrogateway-{gateway,web}；master → latest + sha-<7>，vX.Y.Z tag → vX.Y.Z + sha-<7>（tag 即版本，发布可追溯）。
- D5 新增 docker-compose.cd.yml（部署机覆盖文件）：仅覆盖 image: + build: !reset + pull_policy: always（Compose v5.1.1 支持 !reset，已实测合并通过）；mqtt/端口/卷/环境变量仍由 docker-compose.yml 定义；本地开发仍 docker compose up -d（构建路径不变）。

## Alternatives

- 保持 CI 只 build+test：镜像仍需部署机现场构建，无法消除编译环境不一致。
- 发布到其他 registry：GHCR 与 GitHub Actions 集成最顺、无需额外凭据。

## Rationale

部署机直接 pull 发布产物消除编译环境不一致；tag 即版本、sha-<7> 可追溯；PR 只跑 CI 保证合并前质量门；本地开发构建路径不变。

## Consequences

- master/v* push 自动构建并发布镜像到 GHCR；PR 仅跑 CI。
- 发布版本以 git tag vX.Y.Z 触发；回滚 = 部署机 pull 上一版本 tag 或 sha-<7>。
