using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;
using NitroGateway.Protocols.Modbus;
using NitroGateway.Shared;
using System.IO;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>ADR-023：PingAsync 必须把 HSL 原始错误转成用户可读文案，不透传内部细节</summary>
public class ModbusDriverBaseTests
{
    [Fact]
    public async Task Ping_TimeoutRawMessage_ReturnsFriendlyText()
    {
        var driver = new ThrowingPingDriver(NullLogger<ThrowingPingDriver>.Instance);
        var r = await driver.PingAsync();

        Assert.False(r.IsSuccess);
        Assert.DoesNotContain("PipeTcpNet", r.Error!.Message);
        Assert.DoesNotContain("Socket Exception", r.Error.Message);
        Assert.DoesNotContain("读取 Int16", r.Error.Message);
        Assert.Contains("从站无响应", r.Error.Message);
        Assert.Contains("从站地址", r.Error.Message);
    }

    [Fact]
    public async Task Ping_ExceptionRawMessage_ReturnsFriendlyText()
    {
        var driver = new ThrowingPingDriver(NullLogger<ThrowingPingDriver>.Instance, "读取 Int16失败: The remote server return error code: 02");
        var r = await driver.PingAsync();

        Assert.False(r.IsSuccess);
        Assert.DoesNotContain("error code", r.Error!.Message);
        Assert.Contains("从站无响应", r.Error.Message);
    }

    /// <summary>可控 Ping 失败路径：模拟 HSL 读取抛原始内部错误</summary>
    private sealed class ThrowingPingDriver : ModbusDriverBase
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly string _raw;

        public ThrowingPingDriver(ILogger logger, string raw = "读取 Int16失败: PipeTcpNet[127.0.0.1:502] : Socket Exception -> 接收数据超时：5000")
            : base(logger)
        {
            _raw = raw;
            State = DriverState.Connected;
        }

        protected override SemaphoreSlim ReadGate => _gate;

        protected override Task<object[]?> ReadBatchTypedAsync(string address, DataType type, int count)
            => throw new NotSupportedException();

        protected override Task<object> ReadSingleTypedAsync(DataType type, string address)
            => throw new IOException(_raw);

        protected override Task<OperationResult> WriteSingleValueAsync(DevicePoint point, string address, object value)
            => throw new NotSupportedException();

        public override Task<OperationResult> ConnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public override Task<OperationResult> DisconnectAsync(CancellationToken ct = default)
            => Task.FromResult(OperationResult.Success());

        public override void Dispose() { }
    }
}