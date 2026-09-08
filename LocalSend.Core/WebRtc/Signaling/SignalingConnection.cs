using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LocalSend.Core.Util;

namespace LocalSend.Core.WebRtc.Signaling;

/// <summary>
/// 与信令服务器的低层连接，对应 Rust 端
/// <c>src/webrtc/signaling.rs</c> 中的 <c>SignalingConnection</c>。
/// </summary>
/// <remarks>
/// 该类负责：
/// <list type="bullet">
///   <item>建立到信令服务器（<c>ws://</c> / <c>wss://</c>）的 WebSocket 连接；</item>
///   <item>启动一个后台发送任务，从 <see cref="_sendChannel"/> 读取消息并发往服务器，
///         空闲 120 秒后发送 Ping 保持连接（对应 Rust 端 <c>tokio::time::timeout</c> 循环）；</item>
///   <item>启动一个后台接收任务，把服务器返回的 <see cref="WsServerMessage"/> 写入
///         <see cref="ReceiveChannel"/>，供上层消费；</item>
///   <item>等待服务器返回的第一条 <see cref="WsServerMessage.Hello"/>，得到服务器分配的
///         <see cref="WsClientInfo"/> 后即认为握手完成。</item>
/// </list>
/// 调用方拿到本对象后，可以：
/// <list type="bullet">
///   <item>直接调用 <see cref="SendUpdate"/> / <see cref="SendOffer"/> / <see cref="SendAnswer"/>
///         发送消息；</item>
///   <item>调用 <see cref="StartListener"/> 升级为 <see cref="ManagedSignalingConnection"/>，
///         该高级 API 内部会把 <c>Answer</c> 消息路由给已注册的回调。</item>
/// </list>
/// </remarks>
public sealed class SignalingConnection : IAsyncDisposable
{
    /// <summary>发送任务空闲超过该时间后发送一次 Ping，对应 Rust 端的 <c>Duration::from_secs(120)</c>。</summary>
    private static readonly TimeSpan SendIdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>底层 WebSocket 连接。</summary>
    private readonly ClientWebSocket _ws;

    /// <summary>发送通道：上层把要发出去的 <see cref="WsClientMessage"/> 写入这里。</summary>
    private readonly Channel<WsClientMessage> _sendChannel;

    /// <summary>接收通道：后台接收任务把收到的 <see cref="WsServerMessage"/> 写入这里。</summary>
    /// <remarks>对应 Rust 端 <c>receive_tx/receive_rx</c>，容量 1 与原实现保持一致。</remarks>
    private readonly Channel<WsServerMessage> _receiveChannel;

    /// <summary>取消令牌，<see cref="DisposeAsync"/> 时触发以通知后台任务退出。</summary>
    private readonly CancellationTokenSource _cts;

    /// <summary>后台发送 / 接收任务句柄，便于 <see cref="DisposeAsync"/> 时等待退出。</summary>
    private readonly Task _sendTask;
    private readonly Task _receiveTask;

    /// <summary>由服务器在 Hello 消息里分配的本端客户端信息。</summary>
    public WsClientInfo Client { get; }

    /// <summary>仅供 <see cref="ConnectAsync"/> 内部使用，构造已握手的连接对象。</summary>
    private SignalingConnection(
        ClientWebSocket ws,
        WsClientInfo client,
        Channel<WsClientMessage> sendChannel,
        Channel<WsServerMessage> receiveChannel,
        CancellationTokenSource cts,
        Task sendTask,
        Task receiveTask)
    {
        _ws = ws;
        Client = client;
        _sendChannel = sendChannel;
        _receiveChannel = receiveChannel;
        _cts = cts;
        _sendTask = sendTask;
        _receiveTask = receiveTask;
    }

    /// <summary>
    /// 连接信令服务器并完成握手，对应 Rust 端
    /// <c>SignalingConnection::connect</c>。
    /// </summary>
    /// <param name="uri">信令服务器 WebSocket 地址，例如 <c>wss://example.com/</c>。</param>
    /// <param name="info">本端客户端信息（不含 id），会被 JSON 序列化后 base64-url 编码，
    /// 作为查询参数 <c>d=</c> 附加到 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>握手完成后的 <see cref="SignalingConnection"/>。</returns>
    /// <exception cref="InvalidOperationException">服务器未在合理时间内返回 Hello 消息。</exception>
    public static async Task<SignalingConnection> ConnectAsync(
        string uri,
        WsClientInfoWithoutId info,
        CancellationToken cancellationToken = default)
    {
        // 把 ClientInfoWithoutId 序列化为 JSON 后做无填充的 URL 安全 base64 编码，
        // 作为查询参数 d= 附加到 URL。对应 Rust 端：
        //   let encoded_info = base64::encode(&serde_json::to_string(info)?);
        //   let uri = format!("{}?d={}", uri.into(), encoded_info);
        string json = JsonSerializer.Serialize(info);
        string encoded = Base64Url.Encode(Encoding.UTF8.GetBytes(json));
        string fullUri = $"{uri}?d={encoded}";

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(fullUri), cancellationToken);

        // send 通道容量 1，与 Rust mpsc::channel(1) 一致。
        var sendChannel = Channel.CreateBounded<WsClientMessage>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false
            });

        // receive 通道容量 1，与 Rust mpsc::channel(1) 一致。
        var receiveChannel = Channel.CreateBounded<WsServerMessage>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true
            });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 启动后台发送任务。
        var sendTask = Task.Run(() => SendLoopAsync(ws, sendChannel.Reader, cts.Token));

        // 启动后台接收任务；Hello 通道用于把首条 Hello 消息中的 ClientInfo 传回主流程。
        var helloChannel = Channel.CreateBounded<WsClientInfo>(
            new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        var receiveTask = Task.Run(() => ReceiveLoopAsync(
            ws, receiveChannel.Writer, helloChannel.Writer, cts.Token));

        // 等待首条 Hello 消息，拿到服务器分配的 ClientInfo。
        // 对应 Rust 端：let client = client_rx.recv().await.unwrap();
        if (!await helloChannel.Reader.WaitToReadAsync(cancellationToken))
        {
            // 接收任务在收到 Hello 前就结束了，通常意味着服务器关闭了连接。
            cts.Cancel();
            try { await sendTask; } catch { /* 忽略 */ }
            try { await receiveTask; } catch { /* 忽略 */ }
            ws.Dispose();
            cts.Dispose();
            throw new InvalidOperationException("Signaling server closed connection before sending Hello.");
        }

        var client = await helloChannel.Reader.ReadAsync(cancellationToken);

        return new SignalingConnection(
            ws, client, sendChannel, receiveChannel, cts, sendTask, receiveTask);
    }

    /// <summary>
    /// 后台发送循环：从 <paramref name="reader"/> 读取消息并发往服务器。
    /// 空闲超过 <see cref="SendIdleTimeout"/> 时发送一次 Ping 保活。
    /// 对应 Rust 端 <c>tokio::spawn</c> 起的那个 <c>loop { tokio::time::timeout(...) }</c> 块。
    /// </summary>
    private static async Task SendLoopAsync(
        ClientWebSocket ws,
        ChannelReader<WsClientMessage> reader,
        CancellationToken ct)
    {
        // 用 ValueTask 的 TryRead 配合带超时的等待来实现 Rust 端的
        // tokio::time::timeout(timeout, send_rx.recv()) 语义。
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(SendIdleTimeout);

            bool got;
            try
            {
                // 等到可读或超时。
                got = await reader.WaitToReadAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时：发送 Ping 保活。对应 Rust 端 write.send(Message::Ping(Bytes::new()))。
                try
                {
                    await ws.SendAsync(
                        Array.Empty<byte>(),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
                catch (WebSocketException)
                {
                    return;
                }

                continue;
            }

            if (!got)
            {
                // 通道关闭（上层释放了连接），退出循环。
                return;
            }

            while (reader.TryRead(out var message))
            {
                string json = JsonSerializer.Serialize(message);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    await ws.SendAsync(
                        bytes,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
                catch (WebSocketException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// 后台接收循环：读取服务器发来的文本帧，反序列化为 <see cref="WsServerMessage"/>，
    /// 写入 <paramref name="receiveWriter"/>；若为 Hello，则把其中的 ClientInfo 写入
    /// <paramref name="helloWriter"/>。对应 Rust 端 <c>read.for_each(...)</c> 任务。
    /// </summary>
    private static async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        ChannelWriter<WsServerMessage> receiveWriter,
        ChannelWriter<WsClientInfo> helloWriter,
        CancellationToken ct)
    {
        // 复用一块缓冲区接收分帧数据。
        var buffer = new byte[16 * 1024];
        var textBuilder = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                textBuilder.Clear();

                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    textBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // 忽略二进制帧（Rust 端也只处理 Message::Text）。
                    continue;
                }

                WsServerMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<WsServerMessage>(textBuilder.ToString());
                }
                catch (JsonException)
                {
                    // Rust 端只是 tracing::error!，这里同样吞掉以保持循环。
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                // 首条 Hello 消息额外把 ClientInfo 传给等待握手的主流程。
                if (message is WsServerMessage.Hello hello)
                {
                    await helloWriter.WriteAsync(hello.Client, ct);
                    helloWriter.TryComplete();
                }

                await receiveWriter.WriteAsync(message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (WebSocketException)
        {
            // 连接异常关闭。
        }
        finally
        {
            receiveWriter.TryComplete();
            helloWriter.TryComplete();
        }
    }

    /// <summary>
    /// 升级为 <see cref="ManagedSignalingConnection"/>：再启动一个分发任务，把
    /// <see cref="WsServerMessage.Answer"/> 路由给通过 <see cref="ManagedSignalingConnection.OnAnswerAsync"/>
    /// 注册的回调；其它消息原样写入返回的 <see cref="Channel{WsServerMessage}"/>。
    /// 对应 Rust 端 <c>SignalingConnection::start_listener</c>。
    /// </summary>
    public (ManagedSignalingConnection managed, ChannelReader<WsServerMessage> listener) StartListener()
    {
        // 16 容量的广播通道，对应 Rust 端 mpsc::channel::<WsServerMessage>(16)。
        var listener = Channel.CreateBounded<WsServerMessage>(
            new BoundedChannelOptions(16)
            {
                SingleReader = true,
                SingleWriter = true
            });

        var onAnswer = new Dictionary<string, Func<WsServerSdpMessage, Task>>();
        var onAnswerLock = new object();

        // 分发任务：从 _receiveChannel 读消息，Answer 命中已注册的 session_id 时调用回调，
        // 其它消息转发给 listener。
        _ = Task.Run(async () =>
        {
            await foreach (var message in _receiveChannel.Reader.ReadAllAsync(_cts.Token))
            {
                if (message is WsServerMessage.Answer answer)
                {
                    Func<WsServerSdpMessage, Task>? callback = null;
                    lock (onAnswerLock)
                    {
                        if (onAnswer.Remove(answer.Sdp.SessionId, out var cb))
                        {
                            callback = cb;
                        }
                    }

                    if (callback is not null)
                    {
                        // 回调内不应抛错；这里不 await 是为了不阻塞分发循环，
                        // 但为简单起见直接 await，与 Rust 端 callback(sdp) 同步调用语义一致。
                        try { await callback(answer.Sdp); } catch { /* 忽略回调异常 */ }
                    }
                }

                await listener.Writer.WriteAsync(message, _cts.Token);
            }

            listener.Writer.TryComplete();
        }, _cts.Token);

        var managed = new ManagedSignalingConnection(
            Client,
            (info, ct) => SendUpdateAsync(info, ct),
            (sessionId, target, sdp, ct) => SendOfferAsync(sessionId, target, sdp, ct),
            (sessionId, target, sdp, ct) => SendAnswerAsync(sessionId, target, sdp, ct),
            (sessionId, callback) =>
            {
                lock (onAnswerLock)
                {
                    onAnswer[sessionId] = callback;
                }

                return Task.CompletedTask;
            });

        return (managed, listener.Reader);
    }

    /// <summary>发送 Update 消息。对应 Rust 端 <c>send_update</c>。</summary>
    public Task SendUpdateAsync(WsClientInfoWithoutId info, CancellationToken ct = default)
        => _sendChannel.Writer.WriteAsync(new WsClientMessage.Update { Info = info },
            ct.IsCancellationRequested ? ct : _cts.Token).AsTask();

    /// <summary>发送 SDP Offer。对应 Rust 端 <c>send_offer</c>。</summary>
    public Task SendOfferAsync(string sessionId, Guid target, string sdp, CancellationToken ct = default)
        => _sendChannel.Writer.WriteAsync(
            new WsClientMessage.Offer
            {
                Sdp = new WsClientSdpMessage { SessionId = sessionId, Target = target, Sdp = sdp }
            }, ct.IsCancellationRequested ? ct : _cts.Token).AsTask();

    /// <summary>发送 SDP Answer。对应 Rust 端 <c>send_answer</c>。</summary>
    public Task SendAnswerAsync(string sessionId, Guid target, string sdp, CancellationToken ct = default)
        => _sendChannel.Writer.WriteAsync(
            new WsClientMessage.Answer
            {
                Sdp = new WsClientSdpMessage { SessionId = sessionId, Target = target, Sdp = sdp }
            }, ct.IsCancellationRequested ? ct : _cts.Token).AsTask();

    /// <summary>
    /// 关闭连接并等待后台任务退出。对应 Rust 端 <c>SignalingConnection</c> 被丢弃时的隐式清理。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sendChannel.Writer.TryComplete();
        _receiveChannel.Writer.TryComplete();

        // 优雅关闭 WebSocket。
        if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closed", CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // 忽略关闭时的网络错误。
            }
        }

        try { await _sendTask; } catch { /* 忽略任务异常 */ }
        try { await _receiveTask; } catch { /* 忽略任务异常 */ }

        _ws.Dispose();
        _cts.Dispose();
    }
}
