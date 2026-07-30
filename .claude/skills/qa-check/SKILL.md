# QA Check Skill
**手动调用**: 用户输入 `/qa-check` 或 "质量检查" 时激活。
**自动触发**: 禁用。必须用户显式调用。

## 做什么

扫描当前项目，生成一份 10 维度测试评估清单，输出为 `TESTING-CHECKLIST.md`。

## 执行步骤

### Step 1: 项目识别
- 检测语言（*.cs → C#, *.py → Python, *.ts → TypeScript, ...）
- 检测框架（.csproj → .NET, package.json → Node, requirements.txt → Python, ...）
- 检测项目类型（Web API / 桌面 / 库 / CLI / 前端）
- 统计文件数、模块数、测试文件数

### Step 2: 十维扫描

对每个维度，用以下方法评估：

**1. 单元测试 (Unit Testing)**
- 检测: 测试文件数量、框架(xUnit/NUnit/Jest/pytest...)
- 判断: 核心业务类是否有对应测试
- 输出: 统计 + 缺口列表

**2. 组件测试 (Component Testing)**
- 检测: 每个模块/包是否有独立测试
- 判断: IO 模块(数据库/MQTT/文件)是否有 mock 测试
- 输出: 列出所有模块，标注测试状态

**3. 集成测试 (Integration Testing)**
- 检测: 是否有集成测试文件、测试数据库/测试容器
- 判断: 跨模块调用链是否有测试覆盖
- 输出: 建议的具体集成测试场景

**4. 系统测试 (System Testing)**
- 检测: docker-compose / 启动脚本 / E2E 测试
- 判断: 是否有端到端验证方案
- 输出: 端到端场景列表

**5. 界面测试 (UI Testing)**
- 检测: Vue/React/WPF/WinForms 前端代码
- 判断: 页面/窗口数量，是否有 UI 测试
- 输出: 页面清单 + 测试状态

**6. 异常测试 (Exception Testing)**
- 检测: try/catch 覆盖、CircuitBreaker/Retry 模式
- 判断: 对关键异常场景是否有处理
- 输出: 异常处理清单

**7. 压力测试 (Stress Testing)**
- 检测: 并发限制(SemaphoreSlim)、Channel 有界容量
- 判断: 是否存在背压/限流设计
- 输出: 容量上限建议

**8. 性能测试 (Performance Testing)**
- 检测: Prometheus/Metrics/Stopwatch/Profiling 代码
- 判断: 是否有指标暴露
- 输出: 关键路径 + 基线参考值

**9. 可靠性测试 (Reliability Testing)**
- 检测: GracefulShutdown/Dispose/Channel.Complete
- 判断: 是否有长稳验证方案
- 输出: 资源泄漏风险点

**10. 验收测试 (Acceptance Testing)**
- 检测: 需求文档/API 文档/Swagger
- 判断: 功能是否可验证
- 输出: 功能清单(从 API/模块推导)

### Step 3: 生成报告

输出格式:
```
# {项目名} 测试评估报告

生成时间: {日期}
项目规模: {文件数}源文件, {测试数} 测试

## 总体评分

██████░░░░░░░░░░░░░░  约 55%

## 十维详细

每个维度:
- 状态: ✅ 充分 / ⚠️ 部分 / ❌ 缺失 / — 不适用
- 发现: 自动检测结果
- 建议: 具体改进项
- 验证方法: 怎么测

## 优先修复项 (Top 3)
```

### Step 4: 输出文件
写入 `TESTING-CHECKLIST.md` 到项目根目录。

## 约束
- 不修改任何源代码
- 不运行任何命令(除了检测文件结构)
- 对无法自动判断的维度，标注"需人工验证"
- 评估要诚实——缺的就是缺的，不要试图掩盖
