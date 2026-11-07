using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Json;
using LocalSend.Core.WebRtc;
using LocalSend.Core.WebRtc.Signaling;

namespace FileShare.Desktop.Tests.Services;

/// <summary>
/// AOT JSON 源生成元数据自检：确保 HTTP DTO、内部标签 union（WebRTC / 信令）
/// 都能在不使用反射的前提下正确往返，且序列化结果带协议要求的 type/status 标签。
/// </summary>
public class LocalSendJsonSourceGenTests
{
    [Fact]
    public void HttpDto_RoundTrip()
    {
        var dto = new RegisterDtoV2 { Alias = "device", Fingerprint = "abc", Port = 53317 };
        var json = LsJson.Serialize(dto);
        var back = LsJson.Deserialize<RegisterDtoV2>(json);

        Assert.NotNull(back);
        Assert.Equal("device", back!.Alias);
        Assert.Equal((ushort)53317, back.Port);
        Assert.Contains("\"alias\":\"device\"", json);
    }

    [Fact]
    public void TaggedUnion_BaseTyped_SerializesStatusTag()
    {
        RtcTokenResponse response = new RtcTokenResponse.Ok { Token = "abc" };
        var json = LsJson.Serialize(response);

        Assert.Contains("\"status\":\"OK\"", json);
        Assert.Contains("\"token\":\"abc\"", json);
        Assert.IsType<RtcTokenResponse.Ok>(LsJson.Deserialize<RtcTokenResponse>(json));
    }

    [Fact]
    public void TaggedUnion_SubclassTyped_SerializesStatusTag()
    {
        var json = LsJson.Serialize(new RtcTokenResponse.Ok { Token = "abc" });

        Assert.Contains("\"status\":\"OK\"", json);
    }

    [Fact]
    public void TaggedUnion_RuntimeDispatch_SerializesStatusTag()
    {
        object response = new RtcFileListResponse.Pair { PublicKey = "pem" };
        var json = LsJson.SerializeRuntime(response);

        Assert.Contains("\"status\":\"PAIR\"", json);
        Assert.Contains("\"publicKey\":\"pem\"", json);
        Assert.IsType<RtcFileListResponse.Pair>(LsJson.Deserialize<RtcFileListResponse>(json));
    }

    [Fact]
    public void SignalingMessage_RoundTrip()
    {
        WsClientMessage message = new WsClientMessage.Offer
        {
            Sdp = new WsClientSdpMessage { SessionId = "s", Sdp = "x" }
        };
        var json = LsJson.Serialize(message);

        Assert.Contains("\"type\":\"OFFER\"", json);
        var back = LsJson.Deserialize<WsClientMessage>(json);
        Assert.IsType<WsClientMessage.Offer>(back);
    }

    [Fact]
    public void EveryRegisteredUnionVariant_HasTypeInfo()
    {
        // 所有同名嵌套类型都必须生成元数据（SYSLIB1031 冲突会让部分类型缺失）
        Type[] variants =
        {
            typeof(RtcTokenResponse.Ok), typeof(RtcTokenResponse.PinRequired),
            typeof(RtcPinReceivingResponse.TooManyAttempts),
            typeof(RtcPinSendingResponse.Ok),
            typeof(RtcFileListResponse.Declined),
            typeof(RtcPairResponse.Ok),
            typeof(WsServerMessage.Hello), typeof(WsClientMessage.Answer)
        };

        foreach (var type in variants)
        {
            Assert.NotNull(LsJson.Info(type));
        }
    }
}
