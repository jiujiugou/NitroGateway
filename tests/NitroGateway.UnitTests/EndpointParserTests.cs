using NitroGateway.Protocols;
using Xunit;

namespace NitroGateway.UnitTests;

public class EndpointParserTests
{
    [Theory]
    [InlineData("192.168.1.100:502", "192.168.1.100", 502)]
    [InlineData("192.168.1.100", "192.168.1.100", null)]
    [InlineData("[::1]:502", "::1", 502)]
    [InlineData("[fe80::1]", "fe80::1", null)]
    [InlineData("10.0.0.5:102", "10.0.0.5", 102)]
    public void Split_ParsesHostAndPort(string endpoint, string host, int? port)
    {
        var (h, p) = EndpointParser.Split(endpoint);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[::1")]
    [InlineData("host:notaport")]
    [InlineData("[::1]:abc")]
    public void Split_Invalid_Throws(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => EndpointParser.Split(endpoint));
    }
}
