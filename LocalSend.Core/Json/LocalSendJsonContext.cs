using System.Text.Json.Serialization;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server;
using LocalSend.Core.Http.Server.Web;
using LocalSend.Core.Model;
using LocalSend.Core.WebRtc;
using LocalSend.Core.WebRtc.Signaling;

namespace LocalSend.Core.Json;

/// <summary>
/// LocalSend 协议所有网络报文的 JSON 源生成上下文。
/// <para>
/// AOT / TrimMode=full 下反射序列化被禁用，任何 <c>JsonSerializer.Serialize(value)</c>
/// 或 <c>Deserialize&lt;T&gt;(json)</c> 都会在运行时抛出 InvalidOperationException。
/// 因此这里显式注册 HTTP(v2/v3)、Web 分享、信令与 WebRTC 数据通道的全部报文类型，
/// 所有收发统一经 <see cref="LsJson"/> 取得本上下文生成的 <c>JsonTypeInfo</c>。
/// </para>
/// <para>
/// 语义与原 <c>JsonSerializerDefaults.Web</c> 保持一致：camelCase 命名、
/// 属性名大小写不敏感、忽略 null 字段、允许字符串读取数字。
/// 事件对象（ServerEventV2 / WebSendEvent 等）只在进程内传递，不参与 JSON 收发，
/// 且含有 Channel/TaskCompletionSource 等不可序列化成员，故不注册。
/// </para>
/// <para>
/// 嵌套的 union 子类存在大量同名类型（Ok / Update / Offer…），
/// 必须用 <c>TypeInfoPropertyName</c> 显式消除冲突（SYSLIB1031），
/// 否则后注册的类型不会生成元数据，AOT 下按运行时类型分发时会失败。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    GenerationMode = JsonSourceGenerationMode.Default)]
// ---- v1/v3 HTTP DTO ----
[JsonSerializable(typeof(RegisterDto))]
[JsonSerializable(typeof(RegisterResponseDto))]
[JsonSerializable(typeof(PrepareUploadRequestDto))]
[JsonSerializable(typeof(PrepareUploadResponseDto))]
[JsonSerializable(typeof(NonceRequest))]
[JsonSerializable(typeof(NonceResponse))]
[JsonSerializable(typeof(ErrorResponse))]
// ---- v2 HTTP / UDP 组播 DTO ----
[JsonSerializable(typeof(RegisterDtoV2))]
[JsonSerializable(typeof(RegisterResponseDtoV2))]
[JsonSerializable(typeof(PrepareUploadRequestDtoV2))]
[JsonSerializable(typeof(PrepareUploadResponseDtoV2))]
[JsonSerializable(typeof(PrepareDownloadResponseDtoV2))]
[JsonSerializable(typeof(InfoResponseDtoV2))]
[JsonSerializable(typeof(MulticastMessageV2))]
// ---- 内部端点 / Web 分享 ----
[JsonSerializable(typeof(ShowRequest))]
[JsonSerializable(typeof(WebSendI18n))]
// ---- 通用载荷类型（被上面的 DTO 递归引用，显式注册便于按运行时类型分发）----
[JsonSerializable(typeof(FileDto))]
[JsonSerializable(typeof(List<FileDto>))]
[JsonSerializable(typeof(Dictionary<string, FileDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
// ---- 信令服务器报文 ----
[JsonSerializable(typeof(WsClientInfo))]
[JsonSerializable(typeof(WsClientInfoWithoutId))]
[JsonSerializable(typeof(WsServerSdpMessage))]
[JsonSerializable(typeof(WsClientSdpMessage))]
[JsonSerializable(typeof(List<WsClientInfo>))]
[JsonSerializable(typeof(WsServerMessage))]
[JsonSerializable(typeof(WsServerMessage.Hello), TypeInfoPropertyName = "WsServerHello")]
[JsonSerializable(typeof(WsServerMessage.Join), TypeInfoPropertyName = "WsServerJoin")]
[JsonSerializable(typeof(WsServerMessage.Update), TypeInfoPropertyName = "WsServerUpdate")]
[JsonSerializable(typeof(WsServerMessage.Left), TypeInfoPropertyName = "WsServerLeft")]
[JsonSerializable(typeof(WsServerMessage.Offer), TypeInfoPropertyName = "WsServerOffer")]
[JsonSerializable(typeof(WsServerMessage.Answer), TypeInfoPropertyName = "WsServerAnswer")]
[JsonSerializable(typeof(WsServerMessage.Error), TypeInfoPropertyName = "WsServerError")]
[JsonSerializable(typeof(WsClientMessage))]
[JsonSerializable(typeof(WsClientMessage.Update), TypeInfoPropertyName = "WsClientUpdate")]
[JsonSerializable(typeof(WsClientMessage.Offer), TypeInfoPropertyName = "WsClientOffer")]
[JsonSerializable(typeof(WsClientMessage.Answer), TypeInfoPropertyName = "WsClientAnswer")]
// ---- WebRTC 数据通道报文 ----
[JsonSerializable(typeof(RtcNonceMessage))]
[JsonSerializable(typeof(RtcTokenRequest))]
[JsonSerializable(typeof(RtcTokenResponse))]
[JsonSerializable(typeof(RtcTokenResponse.Ok), TypeInfoPropertyName = "RtcTokenOk")]
[JsonSerializable(typeof(RtcTokenResponse.PinRequired), TypeInfoPropertyName = "RtcTokenPinRequired")]
[JsonSerializable(typeof(RtcTokenResponse.InvalidSignature), TypeInfoPropertyName = "RtcTokenInvalidSignature")]
[JsonSerializable(typeof(RtcPinMessage))]
[JsonSerializable(typeof(RtcPinReceivingResponse))]
[JsonSerializable(typeof(RtcPinReceivingResponse.Ok), TypeInfoPropertyName = "RtcPinReceivingOk")]
[JsonSerializable(typeof(RtcPinReceivingResponse.PinRequired), TypeInfoPropertyName = "RtcPinReceivingPinRequired")]
[JsonSerializable(typeof(RtcPinReceivingResponse.TooManyAttempts), TypeInfoPropertyName = "RtcPinReceivingTooManyAttempts")]
[JsonSerializable(typeof(RtcPinSendingResponse))]
[JsonSerializable(typeof(RtcPinSendingResponse.Ok), TypeInfoPropertyName = "RtcPinSendingOk")]
[JsonSerializable(typeof(RtcPinSendingResponse.PinRequired), TypeInfoPropertyName = "RtcPinSendingPinRequired")]
[JsonSerializable(typeof(RtcPinSendingResponse.TooManyAttempts), TypeInfoPropertyName = "RtcPinSendingTooManyAttempts")]
[JsonSerializable(typeof(RtcFileListResponse))]
[JsonSerializable(typeof(RtcFileListResponse.Ok), TypeInfoPropertyName = "RtcFileListOk")]
[JsonSerializable(typeof(RtcFileListResponse.Pair), TypeInfoPropertyName = "RtcFileListPair")]
[JsonSerializable(typeof(RtcFileListResponse.Declined), TypeInfoPropertyName = "RtcFileListDeclined")]
[JsonSerializable(typeof(RtcFileListResponse.InvalidSignature), TypeInfoPropertyName = "RtcFileListInvalidSignature")]
[JsonSerializable(typeof(RtcPairResponse))]
[JsonSerializable(typeof(RtcPairResponse.Ok), TypeInfoPropertyName = "RtcPairOk")]
[JsonSerializable(typeof(RtcPairResponse.PairDeclined), TypeInfoPropertyName = "RtcPairPairDeclined")]
[JsonSerializable(typeof(RtcPairResponse.InvalidSignature), TypeInfoPropertyName = "RtcPairInvalidSignature")]
[JsonSerializable(typeof(RtcSendFileHeaderRequest))]
[JsonSerializable(typeof(RtcSendFileResponse))]
public partial class LocalSendJsonContext : JsonSerializerContext
{
}
