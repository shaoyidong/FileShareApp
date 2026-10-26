using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace FileShare.Core.Network.Discovery;

/// <summary>
/// 解析用户输入的对端地址。支持下列写法：
/// <list type="bullet">
/// <item><c>192.168.1.20</c>（使用默认端口）</item>
/// <item><c>192.168.1.20:5237</c></item>
/// <item><c>[fe80::1]:5237</c>、<c>fe80::1</c>（IPv6）</item>
/// <item><c>my-pc.local</c>、<c>nas</c>（主机名，由 DNS/mDNS 解析）</item>
/// <item>从浏览器或日志里复制来的 <c>http://192.168.1.20:5237/</c>（自动剥离协议与路径）</item>
/// </list>
/// 这是「直接输入 IP 连接」功能的基础：跨子网、广播被防火墙拦截、或对端不响应广播时，
/// 用户仍然可以凭已知地址直连。
/// </summary>
public sealed class DeviceAddress
{
    public DeviceAddress(string host, int port)
    {
        Host = host;
        Port = port;
    }

    /// <summary>主机部分（IP 字面量或主机名）。</summary>
    public string Host { get; }

    /// <summary>端口。</summary>
    public int Port { get; }

    /// <summary>是否直接是 IP 字面量（否则需要 DNS 解析）。</summary>
    public bool IsIpLiteral => IPAddress.TryParse(Host, out _);

    /// <summary>
    /// 解析用户输入。
    /// </summary>
    /// <param name="input">用户输入，如 <c>192.168.1.20:5237</c>。</param>
    /// <param name="defaultPort">输入未指定端口时使用的端口。</param>
    /// <param name="address">解析结果。</param>
    /// <param name="error">失败原因（面向用户的中文提示）。</param>
    public static bool TryParse(
        string? input,
        int defaultPort,
        [NotNullWhen(true)] out DeviceAddress? address,
        out string? error)
    {
        address = null;
        error = null;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "请输入设备 IP 地址";
            return false;
        }

        // 容忍 "http://host:port/path" 这类粘贴内容
        foreach (var scheme in new[] { "http://", "https://" })
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text[scheme.Length..];
                break;
            }
        }

        var slashIndex = text.IndexOf('/');
        if (slashIndex >= 0)
        {
            text = text[..slashIndex];
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            error = "请输入设备 IP 地址";
            return false;
        }

        string host;
        var port = defaultPort;

        if (text[0] == '[')
        {
            // IPv6 字面量：[fe80::1] 或 [fe80::1]:5237
            var end = text.IndexOf(']');
            if (end < 0)
            {
                error = "IPv6 地址缺少右方括号，例如 [fe80::1]:5237";
                return false;
            }

            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.StartsWith(':'))
            {
                if (!TryParsePort(rest[1..], out port, out error))
                {
                    return false;
                }
            }
            else if (rest.Length > 0)
            {
                error = "无法识别的地址格式";
                return false;
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            // IPv4:port 或 host:port
            var index = text.IndexOf(':');
            host = text[..index];
            if (!TryParsePort(text[(index + 1)..], out port, out error))
            {
                return false;
            }
        }
        else
        {
            // 纯 IPv4 / 裸 IPv6 / 主机名
            host = text;
        }

        host = host.Trim();
        if (host.Length == 0)
        {
            error = "请输入设备 IP 地址";
            return false;
        }

        foreach (var ch in host)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':'))
            {
                error = $"地址中包含非法字符 '{ch}'";
                return false;
            }
        }

        if (port is < 1 or > 65535)
        {
            error = "端口必须在 1~65535 之间";
            return false;
        }

        address = new DeviceAddress(host, port);
        return true;
    }

    private static bool TryParsePort(string text, out int port, out string? error)
    {
        port = 0;
        error = null;

        if (!int.TryParse(text.Trim(), out var value))
        {
            error = "端口号必须是数字";
            return false;
        }

        if (value is < 1 or > 65535)
        {
            error = "端口必须在 1~65535 之间";
            return false;
        }

        port = value;
        return true;
    }

    public override string ToString()
    {
        if (IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{Host}]:{Port}";
        }

        return $"{Host}:{Port}";
    }
}
