using System.Net;

namespace LocalSend.Core.Http.Server.Common;

/// <summary>
/// URL 查询字符串解析工具，对应 Rust 端 <c>src/http/server/common/query.rs</c> 中的 <c>parse_query</c>。
/// </summary>
public static class QueryParser
{
    /// <summary>
    /// 解析查询字符串为 key-value 字典，键值都做 percent-decoding。
    /// 对应 Rust 端的 <c>form_urlencoded::parse</c>。
    /// </summary>
    public static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }
        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;
            int eq = pair.IndexOf('=');
            string key, value;
            if (eq < 0)
            {
                // 无值参数（例如 "flag&pin=..."中的 flag）。
                key = WebUtility.UrlDecode(pair);
                value = string.Empty;
            }
            else
            {
                key = WebUtility.UrlDecode(pair.Substring(0, eq));
                value = WebUtility.UrlDecode(pair.Substring(eq + 1));
            }
            result[key] = value;
        }
        return result;
    }
}
