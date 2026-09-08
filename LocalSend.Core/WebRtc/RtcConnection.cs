using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Crypto;
using LocalSend.Core.Model;
using LocalSend.Core.WebRtc.Signaling;
using LocalSend.Core.Util;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// WebRTC 主流程：发起 / 接收传输，对应 Rust 端
/// <c>src/webrtc/webrtc.rs</c> 中的 <c>send_offer</c> 与 <c>accept_offer</c>。
/// </summary>
/// <remarks>
/// 由于 .NET 没有内置 WebRTC 库，本类通过 <see cref="IRtcPeerConnectionFactory"/>
/// 抽象出 peer connection 的创建。所有协议级细节（nonce 交换、token 交换、PIN 挑战、
/// 文件列表协商、配对、分块传输）都在本类内部完成，与具体 WebRTC 实现无关。
/// </remarks>
public static class RtcConnection
{
    /// <summary>数据通道标签，对应 Rust 端常量 <c>CHANNEL_LABEL</c>。</summary>
    private const string ChannelLabel = "data";

    // --------------------------------------------------------------------
    // send_offer：发送端流程
    // --------------------------------------------------------------------

    /// <summary>
    /// 作为发送端发起一次 WebRTC 传输，对应 Rust 端 <c>send_offer</c>。
    /// </summary>
    /// <param name="signaling">已升级的信令连接（用于收发 SDP offer / answer）。</param>
    /// <param name="peerConnectionFactory">WebRTC peer connection 工厂。</param>
    /// <param name="stunServers">STUN 服务器列表。</param>
    /// <param name="targetId">目标对端 ID。</param>
    /// <param name="signingKey">本端签名密钥，用于生成 token。</param>
    /// <param name="expectingPublicKey">可选的远端验签公钥；若提供则验证对方 token。</param>
    /// <param name="pin">可选的 PIN 配置；若提供则向接收端发起 PIN 挑战。</param>
    /// <param name="files">待发送的文件列表（用于告知接收端）。</param>
    /// <param name="statusChannel">向应用推送 <see cref="RtcStatus"/>。</param>
    /// <param name="selectedFilesChannel">把接收端选中的文件 ID 集合通知应用（一次性）。</param>
    /// <param name="errorChannel">向应用推送单文件错误。</param>
    /// <param name="pinChannel">应用通过该通道把用户输入的 PIN 传回（每次挑战一条）。</param>
    /// <param name="pairChannel">应用通过该通道决定是否接受配对（一次性）。</param>
    /// <param name="sendingChannel">应用通过该通道逐个喂入待发送文件的字节流。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task SendOfferAsync(
        ManagedSignalingConnection signaling,
        IRtcPeerConnectionFactory peerConnectionFactory,
        IReadOnlyList<string> stunServers,
        Guid targetId,
        SigningTokenKey signingKey,
        IVerifyingTokenKey? expectingPublicKey,
        PinConfig? pin,
        List<FileDto> files,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<HashSet<string>> selectedFilesChannel,
        ChannelWriter<RtcFileError> errorChannel,
        ChannelWriter<Func<string, Task>> pinChannel,    // 这里 -> 应用：把 PIN 回调交给应用
        ChannelWriter<Func<bool, Task>> pairChannel,     // 这里 -> 应用：把配对回调交给应用
        ChannelReader<RtcFile> sendingChannel,
        CancellationToken ct = default)
    {
        // 1. 创建 peer connection 与 data channel（本端创建）。
        await using var peer = peerConnectionFactory.Create(stunServers);
        var dataChannel = peer.CreateDataChannel(ChannelLabel);

        // 2. 后台发送任务：等 data channel 打开后，做 nonce / token / PIN / 文件传输。
        var sendTask = Task.Run(() => SendSideHandlerAsync(
            dataChannel, signingKey, expectingPublicKey, pin, files,
            statusChannel, selectedFilesChannel, errorChannel,
            pinChannel, pairChannel, sendingChannel, ct), ct);

        // 3. 本端生成 offer，等 ICE 收集完成，通过信令发出去。
        string offer = await peer.CreateOfferAsync(ct);
        await peer.SetLocalDescriptionAsync(offer, ct);

        string sessionId = Guid.NewGuid().ToString();
        await signaling.SendOfferAsync(sessionId, targetId, RtcProtocol.EncodeSdp(offer), ct);

        // 4. 注册 answer 回调；answer 到达后设置远端 SDP 并通知应用 SDP 已交换。
        var answerTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await signaling.OnAnswerAsync(sessionId, async sdp =>
        {
            answerTcs.TrySetResult(sdp.Sdp);
            await Task.CompletedTask;
        });

        string remoteSdpEncoded = await answerTcs.Task.WaitAsync(ct);
        string remoteSdp = RtcProtocol.DecodeSdp(remoteSdpEncoded);

        await peer.SetRemoteDescriptionAsync(remoteSdp, isOffer: true, ct);
        await statusChannel.WriteAsync(new RtcStatus.SdpExchanged(), ct);

        // 5. 等待发送任务结束或 peer connection 断开。
        var doneTcs = new TaskCompletionSource();
        peer.OnPeerConnectionStateChange(state =>
        {
            if (state == RtcPeerConnectionState.Disconnected)
            {
                doneTcs.TrySetResult();
            }
        });

        var finished = await Task.WhenAny(sendTask, doneTcs.Task);
        await statusChannel.WriteAsync(new RtcStatus.Finished(), ct);

        try { await dataChannel.CloseAsync(ct); } catch { /* 忽略关闭错误 */ }
        await peer.CloseAsync(ct);
    }

    /// <summary>
    /// 发送端的 data channel 协商与文件发送逻辑，对应 Rust 端 <c>send_offer</c> 中
    /// <c>tokio::spawn</c> 起来的那段 <c>send_task</c>。
    /// </summary>
    private static async Task SendSideHandlerAsync(
        IRtcDataChannel dataChannel,
        SigningTokenKey signingKey,
        IVerifyingTokenKey? expectingPublicKey,
        PinConfig? pin,
        List<FileDto> files,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<HashSet<string>> selectedFilesChannel,
        ChannelWriter<RtcFileError> errorChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        ChannelWriter<Func<bool, Task>> pairChannel,
        ChannelReader<RtcFile> sendingChannel,
        CancellationToken ct)
    {
        // 等待 data channel 打开。
        await dataChannel.Opened.WaitAsync(ct);
        await dataChannel.WaitBufferEmptyAsync(ct);

        // 6.1 Nonce 交换：本端先生成 nonce 并发出，再收对端 nonce，最终 nonce = local || remote。
        byte[] localNonce = Nonce.GenerateNonce();
        await RtcProtocol.SendJsonAsync(dataChannel, RtcNonceMessage.FromBytes(localNonce), ct);
        byte[] remoteNonce = await ReceiveNonceAsync(dataChannel.Messages, ct);
        byte[] nonce = [.. localNonce, .. remoteNonce];

        // 6.2 Token 交换：本端生成 token 发出，等对端响应。
        string localToken = Token.GenerateTokenNonce(signingKey, nonce);
        await RtcProtocol.SendJsonAsync(dataChannel, new RtcTokenRequest { Token = localToken }, ct);

        var tokenResponse = await ReceiveJsonAsync<RtcTokenResponse>(dataChannel.Messages, ct);
        string? remoteToken = tokenResponse switch
        {
            RtcTokenResponse.Ok ok => ok.Token,
            RtcTokenResponse.PinRequired pr => pr.Token,
            RtcTokenResponse.InvalidSignature => throw new InvalidOperationException(
                "Invalid token signature from receiving peer"),
            _ => null
        };

        if (remoteToken is null)
        {
            throw new InvalidOperationException("Invalid token response");
        }

        // 6.3 若对端要求 PIN 且本端配置了公钥，先验证对端 token 签名。
        if (expectingPublicKey is not null &&
            !Token.VerifyTokenNonce(expectingPublicKey, remoteToken, nonce))
        {
            throw new InvalidOperationException("Invalid token signature or nonce");
        }

        // 6.4 若对端要求 PIN 挑战，向应用请求 PIN 并发送给对端。
        if (tokenResponse is RtcTokenResponse.PinRequired)
        {
            await HandlePinChallengeAsync(
                dataChannel, statusChannel, pinChannel,
                receiveInChunks: false, ct);
        }

        // 6.5 若本端配置了 PIN，向对端发起 PIN 挑战。
        if (pin is not null)
        {
            await VerifyPinAsync(
                pin, dataChannel, statusChannel,
                sendInitialNotice: true,
                sendResult: async result =>
                {
                    RtcPinSendingResponse resp = result switch
                    {
                        VerifyPinResult.PinRequired => new RtcPinSendingResponse.PinRequired(),
                        VerifyPinResult.TooManyAttempts => new RtcPinSendingResponse.TooManyAttempts(),
                        _ => throw new InvalidOperationException()
                    };
                    await RtcProtocol.SendStringInChunksAsync(dataChannel,
                        JsonSerializer.Serialize(resp), ct);
                    await RtcProtocol.SendDelimiterAsync(dataChannel, ct);
                },
                ct: ct);
        }

        await statusChannel.WriteAsync(new RtcStatus.Connected(), ct);

        // 7. 发送文件列表（用 RtcPinSendingResponse.Ok 包装）。
        var fileListMsg = new RtcPinSendingResponse.Ok { Files = files };
        await RtcProtocol.SendStringInChunksAsync(
            dataChannel, JsonSerializer.Serialize(fileListMsg), ct);
        await RtcProtocol.SendDelimiterAsync(dataChannel, ct);

        // 8. 接收对端的 file list 响应。
        var fileMap = await ReceiveFileListResponseAsync(
            dataChannel, signalingStatusWriter: statusChannel,
            signingKey, nonce, remoteToken,
            pairChannel, ct);

        // 9. 把对端选中的文件 ID 集合通知应用。
        await selectedFilesChannel.WriteAsync(
            new HashSet<string>(fileMap.Keys), ct);

        // 10. 循环读应用喂入的文件，按 file_id 查 token 后发送。
        await foreach (var file in sendingChannel.ReadAllAsync(ct))
        {
            if (!fileMap.TryGetValue(file.FileId, out var fileToken))
            {
                await errorChannel.WriteAsync(new RtcFileError
                {
                    FileId = file.FileId,
                    Error = "Failed to get file token"
                }, ct);
                continue;
            }

            // 10.1 发送文件头（id + token）。
            await RtcProtocol.SendJsonAsync(dataChannel, new RtcSendFileHeaderRequest
            {
                Id = file.FileId,
                Token = fileToken
            }, ct);

            // 10.2 分块发送文件字节。
            try
            {
                await RtcProtocol.ProcessInChunksAsync(
                    file.BinaryRx.Reader,
                    (chunk, token) => dataChannel.SendBinaryAsync(chunk, token),
                    ct);
            }
            catch (Exception ex)
            {
                await errorChannel.WriteAsync(new RtcFileError
                {
                    FileId = file.FileId,
                    Error = ex.Message
                }, ct);
                continue;
            }
        }

        // 11. 所有文件发完，发分隔符并等对端断开。
        await dataChannel.WaitBufferEmptyAsync(ct);
        await RtcProtocol.SendDelimiterAsync(dataChannel, ct);
        // 对应 Rust 端 receive_rx.recv().await; 等对端关闭。
        try
        {
            await dataChannel.Messages.WaitToReadAsync(ct);
        }
        catch { /* 通道关闭即正常 */ }
    }

    /// <summary>
    /// 处理 file list 响应：可能是 OK / Pair / Declined / InvalidSignature。
    /// 对应 Rust 端 <c>send_offer</c> 中处理 <c>RTCFileListResponse</c> 的分支。
    /// </summary>
    private static async Task<Dictionary<string, string>> ReceiveFileListResponseAsync(
        IRtcDataChannel dataChannel,
        ChannelWriter<RtcStatus> signalingStatusWriter,
        SigningTokenKey signingKey,
        byte[] nonce,
        string remoteToken,
        ChannelWriter<Func<bool, Task>> pairChannel,
        CancellationToken ct)
    {
        while (true)
        {
            var (bytes, _) = await RtcProtocol.ReceiveBinaryUntilTextAsync(dataChannel.Messages, ct);
            var resp = JsonSerializer.Deserialize<RtcFileListResponse>(bytes)
                ?? throw new InvalidOperationException("Failed to deserialize file list response");

            switch (resp)
            {
                case RtcFileListResponse.Ok ok:
                    return ok.Files;

                case RtcFileListResponse.Pair pair:
                    // 配对请求：从对端 token 里推断签名算法标识，解析其公钥并验签。
                    string? signId = Token.ExtractSignatureIdentifier(remoteToken)
                        ?? throw new InvalidOperationException("Failed to extract signature identifier");
                    var parsedKey = SigningTokenKey.ParsePublicKey(pair.PublicKey, signId);
                    if (!Token.VerifyTokenNonce(parsedKey, remoteToken, nonce))
                    {
                        await dataChannel.WaitBufferEmptyAsync(ct);
                        await RtcProtocol.SendJsonAsync(
                            dataChannel, new RtcPairResponse.InvalidSignature(), ct);
                        throw new InvalidOperationException("Invalid token signature or nonce");
                    }

                    // 询问应用是否接受配对。
                    var pairReply = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    await pairChannel.WriteAsync(
                        async accept =>
                        {
                            pairReply.TrySetResult(accept);
                            await Task.CompletedTask;
                        }, ct);

                    bool accept = await pairReply.Task.WaitAsync(ct);
                    if (accept)
                    {
                        var ownPublicKeyPem = signingKey.ExportPublicKey();
                        await RtcProtocol.SendJsonAsync(dataChannel,
                            new RtcPairResponse.Ok { PublicKey = ownPublicKeyPem }, ct);
                    }
                    else
                    {
                        await RtcProtocol.SendJsonAsync(
                            dataChannel, new RtcPairResponse.PairDeclined(), ct);
                    }

                    // 配对成功后再次接收 file list 响应。
                    continue;

                case RtcFileListResponse.Declined:
                    await signalingStatusWriter.WriteAsync(new RtcStatus.Declined(), ct);
                    return new();

                case RtcFileListResponse.InvalidSignature:
                    throw new InvalidOperationException("Invalid signature (not expected)");
            }
        }
    }

    // --------------------------------------------------------------------
    // accept_offer：接收端流程
    // --------------------------------------------------------------------

    /// <summary>
    /// 作为接收端接受一次 WebRTC 传输，对应 Rust 端 <c>accept_offer</c>。
    /// </summary>
    public static async Task AcceptOfferAsync(
        ManagedSignalingConnection signaling,
        IRtcPeerConnectionFactory peerConnectionFactory,
        IReadOnlyList<string> stunServers,
        WsServerSdpMessage offer,
        SigningTokenKey signingKey,
        IVerifyingTokenKey? expectingPublicKey,
        PinConfig? pin,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<List<FileDto>> filesChannel,
        ChannelReader<HashSet<string>?> selectedFilesChannel,
        ChannelWriter<RtcFileError> errorChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        ChannelWriter<RtcFile> receivingChannel,
        ChannelReader<RtcSendFileResponse> userErrorChannel,
        CancellationToken ct = default)
    {
        await using var peer = peerConnectionFactory.Create(stunServers);

        // 接收端等对端创建的 data channel。
        var dataChannelTcs = new TaskCompletionSource<IRtcDataChannel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OnDataChannel(ch =>
        {
            if (ch.Label == ChannelLabel)
            {
                dataChannelTcs.TrySetResult(ch);
            }
        });

        // 后台接收任务：等 data channel 打开后做接收侧协商。
        var receiveTask = Task.Run(() => ReceiveSideHandlerAsync(
            () => dataChannelTcs.Task, signingKey, expectingPublicKey, pin,
            statusChannel, filesChannel, selectedFilesChannel,
            errorChannel, pinChannel, receivingChannel, userErrorChannel, ct), ct);

        // 设置远端 SDP（offer），生成 answer，通过信令回发。
        string remoteSdp = RtcProtocol.DecodeSdp(offer.Sdp);
        await peer.SetRemoteDescriptionAsync(remoteSdp, isOffer: true, ct);
        string answer = await peer.CreateAnswerAsync(ct);
        await peer.SetLocalDescriptionAsync(answer, ct);

        await signaling.SendAnswerAsync(
            offer.SessionId, offer.Peer.Id, RtcProtocol.EncodeSdp(answer), ct);

        await statusChannel.WriteAsync(new RtcStatus.SdpExchanged(), ct);

        // 等接收任务完成或 peer connection 断开。
        var doneTcs = new TaskCompletionSource();
        peer.OnPeerConnectionStateChange(state =>
        {
            if (state == RtcPeerConnectionState.Disconnected)
            {
                doneTcs.TrySetResult();
            }
        });

        await Task.WhenAny(receiveTask, doneTcs.Task);
        await statusChannel.WriteAsync(new RtcStatus.Finished(), ct);
        await peer.CloseAsync(ct);
    }

    /// <summary>
    /// 接收端的 data channel 协商与文件接收逻辑，对应 Rust 端
    /// <c>accept_offer</c> 中的 <c>receive_task</c>。
    /// </summary>
    private static async Task ReceiveSideHandlerAsync(
        Func<Task<IRtcDataChannel>> getDataChannel,
        SigningTokenKey signingKey,
        IVerifyingTokenKey? expectingPublicKey,
        PinConfig? pin,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<List<FileDto>> filesChannel,
        ChannelReader<HashSet<string>?> selectedFilesChannel,
        ChannelWriter<RtcFileError> errorChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        ChannelWriter<RtcFile> receivingChannel,
        ChannelReader<RtcSendFileResponse> userErrorChannel,
        CancellationToken ct)
    {
        var dataChannel = await getDataChannel().WaitAsync(ct);
        await dataChannel.Opened.WaitAsync(ct);

        // Nonce 交换：接收端先收对端 nonce，再发自己的。
        byte[] remoteNonce = await ReceiveNonceAsync(dataChannel.Messages, ct);
        byte[] localNonce = Nonce.GenerateNonce();
        await RtcProtocol.SendJsonAsync(dataChannel, RtcNonceMessage.FromBytes(localNonce), ct);

        // 接收端 nonce 顺序与发送端相反：remote || local
        byte[] nonce = [.. remoteNonce, .. localNonce];

        // Token 交换：先收对端 token，可选验签，再发自己的 token。
        var tokenReq = await ReceiveJsonAsync<RtcTokenRequest>(dataChannel.Messages, ct);
        string remoteToken = tokenReq.Token;

        if (expectingPublicKey is not null)
        {
            if (!Token.VerifyTokenNonce(expectingPublicKey, remoteToken, nonce))
            {
                await RtcProtocol.SendJsonAsync(
                    dataChannel, new RtcTokenResponse.InvalidSignature(), ct);
                await dataChannel.WaitBufferEmptyAsync(ct);
                throw new InvalidOperationException("Invalid token signature or nonce");
            }
        }

        // 发自己的 token：若本端要求 PIN，则用 PinRequired，否则 Ok。
        string localToken = Token.GenerateTokenNonce(signingKey, nonce);
        RtcTokenResponse tokenResp = pin is not null
            ? new RtcTokenResponse.PinRequired { Token = localToken }
            : new RtcTokenResponse.Ok { Token = localToken };
        await RtcProtocol.SendJsonAsync(dataChannel, tokenResp, ct);

        // 若本端配置了 PIN，先发起 PIN 挑战。
        if (pin is not null)
        {
            await VerifyPinAsync(
                pin, dataChannel, statusChannel,
                sendInitialNotice: false,
                sendResult: async result =>
                {
                    RtcPinReceivingResponse resp = result switch
                    {
                        VerifyPinResult.PinRequired => new RtcPinReceivingResponse.PinRequired(),
                        VerifyPinResult.TooManyAttempts => new RtcPinReceivingResponse.TooManyAttempts(),
                        _ => throw new InvalidOperationException()
                    };
                    await RtcProtocol.SendJsonAsync(dataChannel, resp, ct);
                },
                ct: ct);

            // 挑战通过后发 Ok 表示 PIN 完成。
            await RtcProtocol.SendJsonAsync(
                dataChannel, new RtcPinReceivingResponse.Ok(), ct);
        }

        // 等待发送端的 PIN 状态 + 文件列表。
        List<FileDto> fileList = await ReceiveFileListForReceiverAsync(
            dataChannel, statusChannel, pinChannel, pin is not null, ct);

        await statusChannel.WriteAsync(new RtcStatus.Connected(), ct);

        // 把文件列表交给应用。
        await filesChannel.WriteAsync(fileList, ct);

        // 等用户选择要接收的文件（或拒绝）。
        HashSet<string>? selected = await selectedFilesChannel.ReadAsync(ct);
        if (selected is null)
        {
            // 用户拒绝
            await RtcProtocol.SendStringInChunksAsync(dataChannel,
                JsonSerializer.Serialize(new RtcFileListResponse.Declined()), ct);
            await RtcProtocol.SendDelimiterAsync(dataChannel, ct);
            return;
        }

        // 为每个选中的文件分配 token。
        var fileTokens = selected.ToDictionary(id => id, _ => Guid.NewGuid().ToString());
        await RtcProtocol.SendStringInChunksAsync(dataChannel,
            JsonSerializer.Serialize(new RtcFileListResponse.Ok { Files = fileTokens }), ct);
        await RtcProtocol.SendDelimiterAsync(dataChannel, ct);

        // 主接收循环：交替处理"文本消息（文件头 / 结束标记）"与"二进制块（文件内容）"。
        RtcFileState? state = null;
        await foreach (var msg in dataChannel.Messages.ReadAllAsync(ct))
        {
            if (msg.IsString)
            {
                // 一个文件的二进制流结束。先等应用的写入结果。
                string? lastFileId = state?.FileId;
                state = null;

                if (lastFileId is not null)
                {
                    // 从应用拿到该文件的处理结果。
                    RtcSendFileResponse? userResult = null;
                    try
                    {
                        userResult = await userErrorChannel.ReadAsync(ct);
                    }
                    catch
                    {
                        userResult = null;
                    }

                    string? error = userResult is null
                        ? "Failed to receive file result"
                        : userResult.Success ? null : (userResult.Error ?? "Unknown error");

                    await RtcProtocol.SendJsonAsync(dataChannel, new RtcSendFileResponse
                    {
                        Id = lastFileId,
                        Success = error is null,
                        Error = error
                    }, ct);

                    if (RtcProtocol.IsDelimiter(msg))
                    {
                        // 所有文件传完。
                        try
                        {
                            await dataChannel.WaitBufferEmptyAsync(ct)
                                .WaitAsync(TimeSpan.FromSeconds(5), ct);
                        }
                        catch { /* 超时也无所谓 */ }
                        break;
                    }
                }

                // 解析下一个文件头。
                var header = JsonSerializer.Deserialize<RtcSendFileHeaderRequest>(msg.Data)
                    ?? throw new InvalidOperationException("Failed to deserialize file header");

                if (!fileTokens.TryGetValue(header.Id, out var expected))
                {
                    await errorChannel.WriteAsync(new RtcFileError
                    {
                        FileId = header.Id,
                        Error = "File not found"
                    }, ct);
                    continue;
                }

                if (header.Token != expected)
                {
                    await errorChannel.WriteAsync(new RtcFileError
                    {
                        FileId = header.Id,
                        Error = "Invalid token"
                    }, ct);
                    continue;
                }

                // 查文件大小，用于检测超长传输。
                var fileDto = fileList.FirstOrDefault(f => f.Id == header.Id);
                if (fileDto is null)
                {
                    await errorChannel.WriteAsync(new RtcFileError
                    {
                        FileId = header.Id,
                        Error = "Expected size to be available"
                    }, ct);
                    continue;
                }

                state = new RtcFileState
                {
                    FileId = header.Id,
                    Size = fileDto.Size,
                    Received = 0
                };

                // 把字节流通道交给应用。
                await receivingChannel.WriteAsync(new RtcFile
                {
                    FileId = header.Id,
                    BinaryRx = state.BinaryTx
                }, ct);
            }
            else
            {
                // 二进制块：转发给当前文件的字节流通道。
                if (state is null)
                {
                    await errorChannel.WriteAsync(new RtcFileError
                    {
                        FileId = "unknown",
                        Error = "Received binary data without a header"
                    }, ct);
                    continue;
                }

                state.Received += (ulong)msg.Data.Length;
                if (state.Received > state.Size)
                {
                    // 对端发了超出声明的字节，提前中断以避免写出损坏文件。
                    await errorChannel.WriteAsync(new RtcFileError
                    {
                        FileId = state.FileId,
                        Error = $"Received more bytes than expected (expected {state.Size}, got {state.Received})"
                    }, ct);
                    state = null;
                    continue;
                }

                await state.BinaryTx.Writer.WriteAsync(msg.Data, ct);
            }
        }
    }

    /// <summary>
    /// 接收端读取发送端发来的 PIN 状态 + 文件列表。
    /// 对应 Rust 端 <c>accept_offer</c> 中处理 <c>RTCPinSendingResponse</c> 的部分。
    /// </summary>
    private static async Task<List<FileDto>> ReceiveFileListForReceiverAsync(
        IRtcDataChannel dataChannel,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        bool localPinConfigured,
        CancellationToken ct)
    {
        var (bytes, _) = await RtcProtocol.ReceiveBinaryUntilTextAsync(dataChannel.Messages, ct);
        var resp = JsonSerializer.Deserialize<RtcPinSendingResponse>(bytes)
            ?? throw new InvalidOperationException("Failed to deserialize pin sending response");

        switch (resp)
        {
            case RtcPinSendingResponse.Ok ok:
                return ok.Files;

            case RtcPinSendingResponse.PinRequired:
                // 发送端反过来要求 PIN。交给 HandlePinChallengeAsync 处理。
                return await HandlePinChallengeForReceiverAsync(
                    dataChannel, statusChannel, pinChannel, ct);

            case RtcPinSendingResponse.TooManyAttempts:
                await statusChannel.WriteAsync(new RtcStatus.Error
                {
                    ErrorMessage = "Unexpected TooManyAttempts response"
                }, ct);
                throw new InvalidOperationException("Unexpected TooManyAttempts response");
        }

        throw new InvalidOperationException("Unknown pin sending response");
    }

    // --------------------------------------------------------------------
    // 共用：nonce / token / JSON 接收辅助
    // --------------------------------------------------------------------

    /// <summary>
    /// 从数据通道接收一条 nonce 消息，校验后返回原始字节。
    /// 对应 Rust 端 <c>receive_nonce</c>。
    /// </summary>
    private static async Task<byte[]> ReceiveNonceAsync(
        ChannelReader<RtcDataChannelMessage> input,
        CancellationToken ct)
    {
        var msg = await input.ReadAsync(ct);
        if (!msg.IsString)
        {
            throw new InvalidOperationException("Expected string message (nonce)");
        }

        var nonceMsg = JsonSerializer.Deserialize<RtcNonceMessage>(msg.AsText())
            ?? throw new InvalidOperationException("Failed to deserialize nonce message");
        byte[] nonce = nonceMsg.DecodeNonce();
        if (!Nonce.ValidateNonce(nonce))
        {
            throw new InvalidOperationException("Invalid remote nonce");
        }

        return nonce;
    }

    /// <summary>
    /// 从数据通道接收一条 JSON 文本消息并反序列化。
    /// </summary>
    private static async Task<T> ReceiveJsonAsync<T>(
        ChannelReader<RtcDataChannelMessage> input,
        CancellationToken ct) where T : class
    {
        var msg = await input.ReadAsync(ct);
        if (!msg.IsString)
        {
            throw new InvalidOperationException("Expected string message");
        }

        return JsonSerializer.Deserialize<T>(msg.AsText())
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}");
    }

    // --------------------------------------------------------------------
    // PIN 挑战：本端是被挑战方（需要输入正确 PIN）
    // --------------------------------------------------------------------

    /// <summary>
    /// 处理 PIN 挑战循环：被挑战方向应用请求 PIN，发送给对端，等响应。
    /// 对应 Rust 端 <c>handle_pin</c>。
    /// </summary>
    private static async Task<List<FileDto>> HandlePinChallengeForReceiverAsync(
        IRtcDataChannel dataChannel,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        CancellationToken ct)
    {
        while (true)
        {
            await statusChannel.WriteAsync(new RtcStatus.PinRequired(), ct);

            // 向应用请求 PIN：通过 pinChannel 把一个回调推给应用，应用用它返回 PIN。
            var pinTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await pinChannel.WriteAsync(async pin =>
            {
                pinTcs.TrySetResult(pin);
                await Task.CompletedTask;
            }, ct);

            string pin = await pinTcs.Task.WaitAsync(ct);
            await RtcProtocol.SendJsonAsync(dataChannel, new RtcPinMessage { Pin = pin }, ct);

            // 接收响应（分块拼接）。
            var (bytes, _) = await RtcProtocol.ReceiveBinaryUntilTextAsync(dataChannel.Messages, ct);
            var resp = JsonSerializer.Deserialize<RtcPinSendingResponse>(bytes)
                ?? throw new InvalidOperationException("Failed to parse pin response");

            switch (resp)
            {
                case RtcPinSendingResponse.Ok ok:
                    return ok.Files;
                case RtcPinSendingResponse.PinRequired:
                    continue;  // 继续挑战
                case RtcPinSendingResponse.TooManyAttempts:
                    await statusChannel.WriteAsync(new RtcStatus.TooManyAttempts(), ct);
                    throw new InvalidOperationException("Too many requests");
            }
        }
    }

    /// <summary>
    /// 通用 PIN 挑战循环（被挑战方）。对应 Rust 端 <c>handle_pin</c> 的另一个使用点。
    /// </summary>
    private static async Task HandlePinChallengeAsync(
        IRtcDataChannel dataChannel,
        ChannelWriter<RtcStatus> statusChannel,
        ChannelWriter<Func<string, Task>> pinChannel,
        bool receiveInChunks,
        CancellationToken ct)
    {
        while (true)
        {
            await statusChannel.WriteAsync(new RtcStatus.PinRequired(), ct);

            var pinTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await pinChannel.WriteAsync(async pin =>
            {
                pinTcs.TrySetResult(pin);
                await Task.CompletedTask;
            }, ct);

            string pin = await pinTcs.Task.WaitAsync(ct);
            await RtcProtocol.SendJsonAsync(dataChannel, new RtcPinMessage { Pin = pin }, ct);

            RtcDataChannelMessage respMsg;
            if (receiveInChunks)
            {
                var (bytes, _) = await RtcProtocol.ReceiveBinaryUntilTextAsync(
                    dataChannel.Messages, ct);
                respMsg = new RtcDataChannelMessage(true, bytes);
            }
            else
            {
                respMsg = await dataChannel.Messages.ReadAsync(ct);
                if (!respMsg.IsString)
                {
                    throw new InvalidOperationException("Expected string message");
                }
            }

            var resp = JsonSerializer.Deserialize<RtcPinReceivingResponse>(respMsg.AsText())
                ?? throw new InvalidOperationException("Failed to parse pin response");

            switch (resp)
            {
                case RtcPinReceivingResponse.Ok:
                    return;
                case RtcPinReceivingResponse.PinRequired:
                    continue;
                case RtcPinReceivingResponse.TooManyAttempts:
                    await statusChannel.WriteAsync(new RtcStatus.TooManyAttempts(), ct);
                    throw new InvalidOperationException("Too many requests");
            }
        }
    }

    // --------------------------------------------------------------------
    // PIN 挑战：本端是挑战方（验证对端发来的 PIN）
    // --------------------------------------------------------------------

    /// <summary>挑战方对每次失败时报告的结果。</summary>
    private enum VerifyPinResult
    {
        PinRequired,
        TooManyAttempts
    }

    /// <summary>
    /// 作为挑战方验证对端 PIN：先发挑战（可选），循环收 PIN 直到匹配或超限。
    /// 对应 Rust 端 <c>verify_pin</c>。
    /// </summary>
    private static async Task VerifyPinAsync(
        PinConfig pinConfig,
        IRtcDataChannel dataChannel,
        ChannelWriter<RtcStatus> statusChannel,
        bool sendInitialNotice,
        Func<VerifyPinResult, Task> sendResult,
        CancellationToken ct)
    {
        await statusChannel.WriteAsync(new RtcStatus.PinRequired(), ct);

        string remotePin = "";
        byte pinTry = 0;

        while (true)
        {
            if (remotePin == pinConfig.Pin)
            {
                return;
            }

            if (pinTry >= pinConfig.MaxTries)
            {
                try
                {
                    await Task.WhenAny(
                        sendResult(VerifyPinResult.TooManyAttempts),
                        Task.Delay(TimeSpan.FromSeconds(5), ct));
                    await dataChannel.WaitBufferEmptyAsync(ct);
                }
                catch { /* 超时无所谓 */ }

                await statusChannel.WriteAsync(new RtcStatus.TooManyAttempts(), ct);
                throw new InvalidOperationException("Too many requests");
            }

            if (sendInitialNotice || pinTry > 0)
            {
                sendInitialNotice = false;
                await sendResult(VerifyPinResult.PinRequired);
            }

            var msg = await dataChannel.Messages.ReadAsync(ct);
            var pinReq = JsonSerializer.Deserialize<RtcPinMessage>(msg.AsText())
                ?? throw new InvalidOperationException("Failed to parse pin message");
            remotePin = pinReq.Pin;
            pinTry++;
        }
    }
}
