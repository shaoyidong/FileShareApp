namespace LocalSend.Core.Http.Server;

/// <summary>
/// Web 发送页面所需的静态资源，对应 Rust 端 <c>include_str!("../../../assets/web/...")</c>。
/// </summary>
/// <remarks>
/// 出于移植简洁性考虑，这里直接嵌入最小可用的 HTML 占位，
/// 真实页面对应原仓库 <c>assets/web/index.html</c> 与 <c>assets/web/main.js</c>。
/// 实际项目中可使用 <c>EmbeddedFile</c> 或资源文件加载。
/// </remarks>
internal static class WebAssets
{
    /// <summary>Web 首页 HTML。</summary>
    public const string IndexHtml = @"<!doctype html>
<html><head><meta charset=""utf-8""><title>LocalSend</title></head>
<body>
<div id=""app"">LocalSend Web (C# port placeholder)</div>
<script src=""/main.js""></script>
</body></html>";

    /// <summary>Web 主 JS。</summary>
    public const string MainJs = "// LocalSend web client placeholder (C# port)\nconsole.log('LocalSend web');";
}
