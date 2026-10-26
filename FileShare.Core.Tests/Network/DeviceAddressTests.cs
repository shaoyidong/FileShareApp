using FileShare.Core.Network.Discovery;

namespace FileShare.Core.Tests.Network;

/// <summary>
/// 「直接输入 IP 连接」功能的地址解析测试。
/// </summary>
public class DeviceAddressTests
{
    [Theory]
    [InlineData("192.168.1.5", "192.168.1.5", 5237)]
    [InlineData(" 192.168.1.5 ", "192.168.1.5", 5237)]
    [InlineData("192.168.1.5:6000", "192.168.1.5", 6000)]
    [InlineData("http://192.168.1.5:6000/download", "192.168.1.5", 6000)]
    [InlineData("https://192.168.1.5", "192.168.1.5", 5237)]
    [InlineData("[fe80::1]:5237", "fe80::1", 5237)]
    [InlineData("fe80::1", "fe80::1", 5237)]
    [InlineData("nas.local", "nas.local", 5237)]
    [InlineData("my-pc", "my-pc", 5237)]
    public void TryParse_AcceptsSupportedFormats(string input, string expectedHost, int expectedPort)
    {
        var ok = DeviceAddress.TryParse(input, 5237, out var address, out var error);

        Assert.True(ok, error);
        Assert.NotNull(address);
        Assert.Equal(expectedHost, address!.Host);
        Assert.Equal(expectedPort, address.Port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("1.2.3.4:")]
    [InlineData("bad$host")]
    [InlineData("[fe80::1")]
    public void TryParse_RejectsInvalidFormats(string? input)
    {
        var ok = DeviceAddress.TryParse(input, 5237, out var address, out var error);

        Assert.False(ok);
        Assert.Null(address);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_UsesProvidedDefaultPort()
    {
        var ok = DeviceAddress.TryParse("10.0.0.8", 8080, out var address, out _);

        Assert.True(ok);
        Assert.Equal(8080, address!.Port);
    }

    [Fact]
    public void IsIpLiteral_DetectsHostNames()
    {
        DeviceAddress.TryParse("192.168.1.5", 5237, out var ipAddress, out _);
        DeviceAddress.TryParse("name.local", 5237, out var hostName, out _);

        Assert.True(ipAddress!.IsIpLiteral);
        Assert.False(hostName!.IsIpLiteral);
    }
}
