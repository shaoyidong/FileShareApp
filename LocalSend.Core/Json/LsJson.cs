using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LocalSend.Core.WebRtc;
using LocalSend.Core.WebRtc.Signaling;

namespace LocalSend.Core.Json;

/// <summary>
/// LocalSend 协议 JSON 收发的唯一入口（AOT 安全）。
/// <para>
/// 所有类型信息都来自 <see cref="LocalSendJsonContext"/> 的源生成元数据，
/// 不使用 <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> 这类依赖反射的重载。
/// 需要新增报文类型时，只需在 context 上加一行 <c>[JsonSerializable]</c>。
/// </para>
/// <para>
/// <b>内部标签 union 的坑</b>：<c>[JsonConverter]</c> 只在声明它的抽象基类上生效，
/// 源生成为具体子类生成的元数据不会继承该转换器。若按子类元数据序列化，
/// 会丢掉协议必需的 <c>status</c> / <c>type</c> 标签。因此这里维护一张
/// 「子类 → 带转换器的基类」映射：业务侧序列化一律走 <see cref="SerializeRuntime"/> /
/// <see cref="Serialize{T}"/>（自动改用基类元数据），
/// 转换器内部展开子类字段则走 <see cref="SerializeToUtf8BytesVariant"/>（严格用子类元数据，避免递归）。
/// </para>
/// </summary>
public static class LsJson
{
    private static readonly Dictionary<Type, Type> UnionBaseTypes = BuildUnionBaseTypes();

    /// <summary>源生成上下文实例。</summary>
    public static LocalSendJsonContext Context => LocalSendJsonContext.Default;

    /// <summary>
    /// 统一的序列化选项（camelCase / Web 语义，TypeInfoResolver 指向源生成上下文）。
    /// 供自定义 JsonConverter 内部的辅助调用与需要 options 的 API 使用。
    /// </summary>
    public static JsonSerializerOptions Options => LocalSendJsonContext.Default.Options;

    /// <summary>取得编译期类型 T 的源生成元数据；未注册时立即报错，避免 AOT 下运行时才失败。</summary>
    public static JsonTypeInfo<T> Info<T>() =>
        LocalSendJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"JSON 类型未在 LocalSendJsonContext 注册: {typeof(T).FullName}");

    /// <summary>按运行时类型取得源生成元数据（按原始类型，不做 union 映射）。</summary>
    public static JsonTypeInfo Info(Type type) =>
        LocalSendJsonContext.Default.GetTypeInfo(type)
        ?? throw new InvalidOperationException($"JSON 类型未在 LocalSendJsonContext 注册: {type.FullName}");

    /// <summary>序列化时实际使用的元数据：union 子类改用带转换器的基类。</summary>
    private static JsonTypeInfo SerializeInfoFor(Type runtimeType) =>
        Info(UnionBaseTypes.TryGetValue(runtimeType, out var baseType) ? baseType : runtimeType);

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize((object?)value, SerializeInfoFor(value?.GetType() ?? typeof(T)));

    public static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes((object?)value, SerializeInfoFor(value?.GetType() ?? typeof(T)));

    /// <summary>按运行时类型序列化（业务侧使用；union 子类会自动带上基类转换器）。</summary>
    public static string SerializeRuntime(object value) =>
        JsonSerializer.Serialize(value, SerializeInfoFor(value.GetType()));

    /// <summary>按运行时类型序列化为 UTF-8 字节（业务侧使用）。</summary>
    public static byte[] SerializeToUtf8BytesRuntime(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, SerializeInfoFor(value.GetType()));

    /// <summary>
    /// 转换器内部专用：严格按子类元数据展开字段（不含 tag，也不触发基类转换器）。
    /// </summary>
    public static byte[] SerializeToUtf8BytesVariant(object value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Info(value.GetType()));

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, Info<T>());

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, Info<T>());

    public static object? Deserialize(string json, Type type) =>
        JsonSerializer.Deserialize(json, Info(type));

    public static ValueTask<T?> DeserializeAsync<T>(Stream utf8Json, CancellationToken ct = default) =>
        JsonSerializer.DeserializeAsync(utf8Json, Info<T>(), ct);

    /// <summary>
    /// 「内部标签」多态报文的子类 → 声明 <c>[JsonConverter]</c> 的抽象基类。
    /// 新增 union 变体时，除了在 context 注册，也要在此登记（一处，AOT 安全、无反射）。
    /// </summary>
    private static Dictionary<Type, Type> BuildUnionBaseTypes()
    {
        var map = new Dictionary<Type, Type>();

        void Add(Type baseType, params Type[] variants)
        {
            foreach (var variant in variants)
            {
                map[variant] = baseType;
            }
        }

        Add(typeof(WsServerMessage),
            typeof(WsServerMessage.Hello), typeof(WsServerMessage.Join),
            typeof(WsServerMessage.Update), typeof(WsServerMessage.Left),
            typeof(WsServerMessage.Offer), typeof(WsServerMessage.Answer),
            typeof(WsServerMessage.Error));
        Add(typeof(WsClientMessage),
            typeof(WsClientMessage.Update), typeof(WsClientMessage.Offer),
            typeof(WsClientMessage.Answer));

        Add(typeof(RtcTokenResponse),
            typeof(RtcTokenResponse.Ok), typeof(RtcTokenResponse.PinRequired),
            typeof(RtcTokenResponse.InvalidSignature));
        Add(typeof(RtcPinReceivingResponse),
            typeof(RtcPinReceivingResponse.Ok), typeof(RtcPinReceivingResponse.PinRequired),
            typeof(RtcPinReceivingResponse.TooManyAttempts));
        Add(typeof(RtcPinSendingResponse),
            typeof(RtcPinSendingResponse.Ok), typeof(RtcPinSendingResponse.PinRequired),
            typeof(RtcPinSendingResponse.TooManyAttempts));
        Add(typeof(RtcFileListResponse),
            typeof(RtcFileListResponse.Ok), typeof(RtcFileListResponse.Pair),
            typeof(RtcFileListResponse.Declined), typeof(RtcFileListResponse.InvalidSignature));
        Add(typeof(RtcPairResponse),
            typeof(RtcPairResponse.Ok), typeof(RtcPairResponse.PairDeclined),
            typeof(RtcPairResponse.InvalidSignature));

        return map;
    }
}
