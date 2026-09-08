using System.Text;

namespace LocalSend.Core.Http.Client;

/// <summary>
/// 目标 URL 构造器，对应 Rust 端 <c>src/http/client/url.rs</c> 中的 <c>TargetUrl</c>。
/// </summary>
/// <remarks>
/// LocalSend 的所有 API URL 形如：
/// <c>{protocol}://{host}:{port}/api/localsend/{v2|v3}{path}?{query}</c>
/// 对于 IPv6 地址，host 会被包裹在方括号里。
/// </remarks>
public readonly struct TargetUrl
{
    public ApiVersion Version { get; }
    public string Protocol { get; }
    public string Host { get; }
    public ushort Port { get; }
    public string Path { get; }

    /// <summary>查询参数（key, value）对。调用方需自行 URL 编码。</summary>
    public IReadOnlyList<(string Key, string Value)> Params { get; }

    public TargetUrl(ApiVersion version, string protocol, string host, ushort port, string path,
        IReadOnlyList<(string Key, string Value)>? parameters = null)
    {
        Version = version;
        Protocol = protocol;
        Host = host;
        Port = port;
        Path = path;
        Params = parameters ?? Array.Empty<(string, string)>();
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Protocol).Append("://");

        // IPv6 地址需要被方括号包住，避免与端口分隔的 ":" 产生歧义。
        if (Host.Contains(':'))
        {
            sb.Append('[').Append(Host).Append(']');
        }
        else
        {
            sb.Append(Host);
        }

        sb.Append(':').Append(Port)
          .Append("/api/localsend/")
          .Append(Version switch
          {
              ApiVersion.V2 => "v2",
              ApiVersion.V3 => "v3",
              _ => throw new ArgumentOutOfRangeException()
          })
          .Append(Path);

        if (Params.Count > 0)
        {
            sb.Append('?');
            for (int i = 0; i < Params.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('&');
                }
                sb.Append(Params[i].Key).Append('=').Append(Params[i].Value);
            }
        }
        return sb.ToString();
    }
}

public enum ApiVersion
{
    V2,
    V3
}
