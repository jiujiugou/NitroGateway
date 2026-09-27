# 05 · JSON 序列化配置统一

- 类别：完全统一
- 优先级：低
- 状态：待办

## 现状

| 位置 | 文件 | 配置 |
|---|---|---|
| 转发 | `src/NitroGateway.Forwarder/JsonMessageSerializer.cs` | camelCase / UTF-8 |
| 命令回执 | `src/NitroGateway.Command/CommandAckSerializer.cs` | 自配 JSON |
| 命令解析 | `src/NitroGateway.Command/CommandRequestParser.cs` | 自配 JSON |
| 传输(HTTP) | `src/NitroGateway.Transport/HTTP/HttpClientWrapper.cs:22` | 独立 `JsonSerializerOptions { CamelCase }` |

同一份 camelCase 约定被抄了多份，易漂移。

## 统一形态

共享 `JsonSerializerOptions` 单例 + 少量帮助方法（序列化到 UTF-8、反序列化带错误分类），放通用机制层。

## 差异是参数还是语义

camelCase/编码是约定（参数）；无语义差异。

## 涉及改动文件（待办）

- [ ] 新增共享 `Json` 帮助（或 `NitroGatewayJson.Options`）
- [ ] 上述 4 处改引用

## 验收 / 测试

- 各模块现有序列化/命令解析单测保持绿。
- 确认输出字节与旧实现逐字节一致（回归）。

## 备注

收益小但零风险，可作为"低垂果实"顺手做。
