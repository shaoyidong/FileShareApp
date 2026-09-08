using LocalSend.Core.Model;

namespace LocalSend.Core.Http.Dto;

/// <summary>
/// v3 -> v2 协议的转换扩展方法，对应 Rust 端 <c>dto.rs</c> 中
/// <c>impl From&lt;RegisterDto&gt; for RegisterDtoV2</c> 等 impl 块。
/// </summary>
public static class DtoConverters
{
    public static DtoV2.ProtocolTypeV2 ToV2(this ProtocolType p) => p switch
    {
        ProtocolType.Http => DtoV2.ProtocolTypeV2.Http,
        ProtocolType.Https => DtoV2.ProtocolTypeV2.Https,
        _ => throw new ArgumentOutOfRangeException(nameof(p))
    };

    /// <summary>v3 <see cref="RegisterDto"/> 转 v2 <see cref="DtoV2.RegisterDtoV2"/>。</summary>
    public static DtoV2.RegisterDtoV2 ToV2(this RegisterDto v3)
    {
        return new DtoV2.RegisterDtoV2
        {
            Alias = v3.Alias,
            Version = v3.Version,
            DeviceModel = v3.DeviceModel,
            DeviceType = v3.DeviceType,
            Fingerprint = v3.Token,
            Port = v3.Port,
            Protocol = v3.Protocol.ToV2(),
            Download = v3.HasWebInterface
        };
    }

    /// <summary>v2 <see cref="DtoV2.RegisterResponseDtoV2"/> 转 v3 <see cref="RegisterResponseDto"/>。</summary>
    public static RegisterResponseDto ToV3(this DtoV2.RegisterResponseDtoV2 v2)
    {
        return new RegisterResponseDto
        {
            Alias = v2.Alias,
            Version = v2.Version,
            DeviceModel = v2.DeviceModel,
            DeviceType = v2.DeviceType,
            Token = v2.Fingerprint,
            HasWebInterface = v2.Download
        };
    }

    /// <summary>v3 <see cref="PrepareUploadRequestDto"/> 转 v2 <see cref="DtoV2.PrepareUploadRequestDtoV2"/>。</summary>
    public static DtoV2.PrepareUploadRequestDtoV2 ToV2(this PrepareUploadRequestDto v3)
    {
        return new DtoV2.PrepareUploadRequestDtoV2
        {
            Info = v3.Info.ToV2(),
            Files = v3.Files
        };
    }

    /// <summary>v2 <see cref="DtoV2.PrepareUploadResponseDtoV2"/> 转 v3 <see cref="PrepareUploadResponseDto"/>。</summary>
    public static PrepareUploadResponseDto ToV3(this DtoV2.PrepareUploadResponseDtoV2 v2)
    {
        return new PrepareUploadResponseDto
        {
            SessionId = v2.SessionId,
            Files = v2.Files
        };
    }

    /// <summary>v2 <see cref="DtoV2.PrepareUploadResultV2"/> 转 v3 <see cref="PrepareUploadResult"/>。</summary>
    public static PrepareUploadResult ToV3(this DtoV2.PrepareUploadResultV2 v2)
    {
        return new PrepareUploadResult
        {
            StatusCode = v2.StatusCode,
            Response = v2.Response?.ToV3()
        };
    }
}
