# ADR-010: CI 工作流（DevOps 收尾）

- 日期: 2026-08-07 | 状态: 已实现（2026-08-10，ci.yml 双 job：build-server ubuntu + build-windows windows，顺序跑测试；按 ADR-028 P1-1 调整） | 用途: 解决 Q-03（.github/workflows 为空、无 CI），承接 D-02
- 范围: `.github/workflows/ci.yml` + README

## 设计
- P1 新增 `ci.yml`（trigger: push + pull_request，job: build-test on ubuntu-latest）
  - checkout@v4 → setup-dotnet@v4（dotnet-version: 10.0.x）→ `dotnet build NitroGateway.slnx -c Release` → `dotnet test tests/NitroGateway.UnitTests -c Release --no-build`
  - NuGet 缓存 `~/.nuget/packages`（key: runner.os + 项目引用指纹，无 lock 文件时退化为简单 key）
- P2 集成测试另起 job `test-integration`：GitHub service container `eclipse-mosquitto:2`（端口 1883），跑 `dotnet test tests/NitroGateway.IntegrationTests`；依赖真实 broker 的用例（MqttClientWrapperTests 直连 localhost:1883）在容器下通过
- P3 边界: 不构建 slnx 外工程（Verification/Mitsubishi/OpcUa 未入 slnx，遵循 AGENTS.md 雷区）
- P4 README 补 CI 徽章与「本地验证 = build + 全量测试」说明（可选）
