using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using NitroGateway.CoyoteTests;

// 独立 Exe 跑批：每个不变量一个正例（期望 0 bug）+ 可选负控（期望 >0 bug）。
// 流程由 run.ps1 驱动：build → coyote rewrite → 运行本程序。
// 注：ProtocolDriverPool 用 System.Threading.Lock，周期死锁检测会误报，故默认关闭 potential-deadlock 上报；
// R1（Dispose 不排水）依赖死锁检测，则按用例开启。

var cases = new (string Name, bool ExpectBug, bool ReportDeadlocks, Func<Task> Body)[]
{
    // ProtocolDriverPool I1–I5
    ("[Pool] I1 正例：并发 GetOrCreate 只建一次且同实例", false, false, ProtocolDriverPoolInvariants.I1_Positive),
    ("[Pool] I1 负控：无锁坏池", true, false, ProtocolDriverPoolInvariants.I1_Negative_Unlocked),
    ("[Pool] I1 负控：持 Lock 但临界区过小", true, false, ProtocolDriverPoolInvariants.I1_Negative_LockedCheckThenAct),
    ("[Pool] I3 正例：混合并发后创建==释放且每驱动恰一次", false, false, ProtocolDriverPoolInvariants.I3_Positive),
    ("[Pool] I3 负控：Evict 不释放（泄漏）", true, false, ProtocolDriverPoolInvariants.I3_Negative_LeakyEvict),
    ("[Pool] I4 正例：并发 Dispose 幂等 + 销毁后 ODE/no-op", false, false, ProtocolDriverPoolInvariants.I4_Positive),
    ("[Pool] I4 负控：非幂等 Dispose", true, false, ProtocolDriverPoolInvariants.I4_Negative_NonIdempotentDispose),
    ("[Pool] I5 正例：Create 异常不泄漏、换键失败保留旧条目", false, false, ProtocolDriverPoolInvariants.I5_Positive),
    ("[Pool] I6 正例：释放不在临界区（阻塞释放时仍能获取其他设备）", false, true, ProtocolDriverPoolInvariants.I6_Positive),
    ("[Pool] I6 负控：锁内释放 → 全池冻结（死锁）", true, true, ProtocolDriverPoolInvariants.I6_Negative_DisposeInLock),

    // ReliableProtocolDriver I7 / R1
    ("[Reliable] I7/G2 正例：释放后快速失败、不触达内层", false, false, ReliableDriverInvariants.I7_G2_Positive),
    ("[Reliable] I7/G2 负控：无释放守卫", true, false, ReliableDriverInvariants.I7_G2_Negative_NoGuard),
    ("[Reliable] 释放幂等 正例：并发 Dispose+DisposeAsync 内层恰一次", false, false, ReliableDriverInvariants.DisposeIdempotent_Positive),
    ("[Reliable] 释放幂等 负控：非幂等守卫", true, false, ReliableDriverInvariants.DisposeIdempotent_Negative_NonIdempotent),
    ("[Reliable] R1 正例：同步 Dispose 不排水", false, true, ReliableDriverInvariants.R1_Positive),
    ("[Reliable] R1 负控：带排水（Dispose 阻塞在途）", true, true, ReliableDriverInvariants.R1_Negative_Drain),

    // ModbusDriverBase I1/I6
    ("[Modbus] 基类 I1 正例：读/写/Ping/批量/连断 共用单闸门串行", false, false, ModbusDriverBaseInvariants.I1_GateSerialization_Positive),
    ("[Modbus] 基类 I1 负控：无闸门坏驱动 → 受保护体并发进入", true, false, ModbusDriverBaseInvariants.I1_GateSerialization_Negative_NoGate),
    ("[Modbus] 基类 I1 负控：仅 Ping 绕过闸门 → 并发进入", true, false, ModbusDriverBaseInvariants.I1_GateSerialization_Negative_BypassPing),
    ("[Modbus] 基类 I6 正例：并发同步/异步释放核心恰拆一次", false, false, ModbusDriverBaseInvariants.I6_DisposeIdempotent_Positive),
    ("[Modbus] 基类 I6 负控：无幂等位 → 重复拆除", true, false, ModbusDriverBaseInvariants.I6_DisposeIdempotent_Negative_NonIdempotent),

    // ModbusTcpDriver I1/X1（注入 Hsl 客户端替身）
    ("[Modbus] TCP I1 正例：并发读经驱动闸门串行", false, false, ModbusTcpDriverInvariants.I1_ReadSerialized_Positive),
    ("[Modbus] TCP I1 负控：无闸门坏驱动 → 并发进入", true, false, ModbusTcpDriverInvariants.I1_ReadSerialized_Negative_NoGate),
    ("[Modbus] TCP X1 正例：异步释放取闸门等在途读", false, false, ModbusTcpDriverInvariants.X1_AsyncDisposeDrains_Positive),
    ("[Modbus] TCP X1 负控：异步释放不排水 → 在途读未完成即拆", true, false, ModbusTcpDriverInvariants.X1_AsyncDisposeDrains_Negative_NoDrain),

    // ModbusRtuDriver I3/X2（注入串口管理器/租约）
    ("[Modbus] RTU I3 正例：并发生命周期后租约账平、串口恰释放一次", false, false, ModbusRtuDriverInvariants.I3_LeaseAccounted_Positive),
    ("[Modbus] RTU I3 负控：只取不还 → 残留租约", true, false, ModbusRtuDriverInvariants.I3_LeaseAccounted_Negative_Leaky),
    ("[Modbus] RTU X2 正例：异步释放取共享闸门等在途帧", false, false, ModbusRtuDriverInvariants.X2_AsyncDisposeDrains_Positive),
    ("[Modbus] RTU X2 负控：异步释放不取闸门 → 在途帧未完成即拆串口", true, false, ModbusRtuDriverInvariants.X2_AsyncDisposeDrains_Negative_NoDrain),

    // SerialPortManager 租约
    ("[Serial] I2/I3 正例：同端口共享句柄/闸门 + 引用计数账平", false, false, SerialPortInvariants.I2I3_Positive),
    ("[Serial] I3 正例：异参 Acquire 抛异常", false, false, SerialPortInvariants.I3_ParamMismatch_Throws),
    ("[Serial] I5 正例：租约并发 Dispose 只归还一次", false, false, SerialPortInvariants.I5_ConcurrentDispose_ReleasesOnce),

    // MqttClientWrapper 并发不变量
    ("[Mqtt] I2 守卫契约 正例：并发持权 ≤1", false, false, MqttWrapperInvariants.I2_Guard_Positive),
    ("[Mqtt] I2 守卫契约 负控：无去重 → 多流持权", true, false, MqttWrapperInvariants.I2_Guard_Negative_NoDedup),
    ("[Mqtt] I2 wrapper 正例：并发触发重连循环持权 ≤1", false, false, MqttWrapperInvariants.I2_Wrapper_Positive),
    ("[Mqtt] I2 wrapper 负控：无去重守卫 → 多循环", true, false, MqttWrapperInvariants.I2_Wrapper_Negative_NoDedup),
    ("[Mqtt] I3 正例：重连 CTS 恰 Dispose 一次", false, false, MqttWrapperInvariants.I3_ReconnectCts_DisposedExactlyOnce),
    ("[Mqtt] I4 正例：释放后 ConnectAsync 失败", false, false, MqttWrapperInvariants.I4_AfterDispose_ConnectFails),
    ("[Mqtt] I5 正例：Disabled 不触达、Enable 可恢复", false, false, MqttWrapperInvariants.I5_Disabled_DoesNotConnect_EnableRecovers),
    ("[Mqtt] I6 正例：重连后重放订阅", false, false, MqttWrapperInvariants.I6_Reconnect_ReplaysSubscriptions),
    ("[Mqtt] I7 正例：监听重入读 State 不死锁", false, true, MqttWrapperInvariants.I7_Listener_ReentrantState_NoDeadlock),
};

const uint defaultIterations = 100;
var iterations = args.Length > 0 && uint.TryParse(args[0], out var parsed) ? parsed : defaultIterations;
var filter = args.Length > 1 ? args[1] : null;
var failed = 0;

foreach (var (name, expectBug, reportDeadlocks, body) in cases)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

    var config = Configuration.Create()
        .WithTestingIterations(iterations)
        .WithPotentialDeadlocksReportedAsBugs(reportDeadlocks);

    var engine = TestingEngine.Create(config, body);
    engine.Run();

    var bugs = engine.TestReport.NumOfFoundBugs;
    var pass = expectBug ? bugs > 0 : bugs == 0;
    if (!pass) failed++;

    if (!pass)
    {
        foreach (var report in engine.TestReport.BugReports)
            Console.WriteLine($"    >> {report}");
    }

    var expectation = expectBug ? ">0" : "=0";
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}  bugs={bugs} (期望 {expectation})");
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? "全部通过" : $"{failed} 项失败");
return failed == 0 ? 0 : 1;
