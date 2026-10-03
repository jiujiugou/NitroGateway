using NitroGateway.Forwarder;
using NitroGateway.Storage.Buffer;
using Xunit;

namespace NitroGateway.UnitTests.Forwarding;

/// <summary>ForwarderOption / HttpForwarderOption 的默认值与通道解析（mqtt/http/both，大小写不敏感）。</summary>
public class ForwarderOptionTests
{
    [Fact]
    public void Defaults_AreExpected()
    {
        var option = new ForwarderOption();
        Assert.Equal(5000, option.IntervalMs);
        Assert.Equal("mqtt", option.Channels);

        var http = new HttpForwarderOption();
        Assert.Equal("", http.BaseUrl);
        Assert.Equal("/api/measurements/batch", http.Path);
        Assert.Equal("/health", http.HealthPath);
        Assert.Equal(30_000, http.TimeoutMs);
        Assert.Equal(3, http.MaxRetries);
    }

    [Theory]
    [InlineData("mqtt")]
    [InlineData("MQTT")]
    [InlineData(" mqtt ")]
    public void ResolveChannels_Mqtt(string channels)
        => Assert.Equal(
            new[] { IForwardBuffer.MqttChannel },
            new ForwarderOption { Channels = channels }.ResolveChannels());

    [Theory]
    [InlineData("http")]
    [InlineData("HTTP")]
    public void ResolveChannels_Http(string channels)
        => Assert.Equal(
            new[] { IForwardBuffer.HttpChannel },
            new ForwarderOption { Channels = channels }.ResolveChannels());

    [Theory]
    [InlineData("both")]
    [InlineData("Both")]
    [InlineData(" both ")]
    public void ResolveChannels_Both(string channels)
        => Assert.Equal(
            new[] { IForwardBuffer.MqttChannel, IForwardBuffer.HttpChannel },
            new ForwarderOption { Channels = channels }.ResolveChannels());

    [Fact]
    public void ResolveChannels_Invalid_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ForwarderOption { Channels = "carrier-pigeon" }.ResolveChannels());
        Assert.Contains("Channels", ex.Message);
    }
}
