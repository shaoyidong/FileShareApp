﻿﻿using LocalSend.Core.Crypto;
using LocalSend.Core.Discovery;
using LocalSend.Core.Http.Client;
using LocalSend.Core.Http.Dto;
using LocalSend.Core.Http.DtoV2;
using LocalSend.Core.Http.Server;
using LocalSend.Core.Http.Server.Common;
using LocalSend.Core.Http.Server.V2;
using LocalSend.Core.Http.State;
using LocalSend.Core.Model;
using LocalSend.Core.WebRtc;
using System.Threading.Channels;





// =====================================================================
// LocalSend.Example — 控制台演示程序
// 展示 LocalSend.Core 的核心用法：密钥生成、TLS 证书、HTTP 服务器、
// HTTP 客户端扫描（HTTPS+mTLS 对接原版 LocalSend）、UDP 组播发现、
// WebRTC Loopback 数据通道演示、事件通道消费。
// =====================================================================

Console.WriteLine("=== LocalSend.Core 示例程序 ===");
Console.WriteLine();

// --------------------------
// 1. 生成签名密钥 + TLS 证书
// --------------------------
Console.WriteLine("[1] 生成签名密钥与 TLS 证书...");
var signingKey = SigningTokenKey.Generate();
string privateKeyPem = signingKey.ExportPrivateKey();
string publicKeyPem = signingKey.ExportPublicKey();
Console.WriteLine($"    Ed25519 签名密钥已生成。公钥 PEM 长度: {publicKeyPem.Length}");

// 生成 RSA 自签名 TLS 证书（与原版 LocalSend 一致：CN=LocalSend User, RSA 2048, 10 年有效期）
var (tlsCertPem, tlsKeyPem, tlsFingerprint) = TlsCertificateGenerator.Generate();
Console.WriteLine($"    TLS 证书已生成。指纹: {tlsFingerprint}");
Console.WriteLine();

// --------------------------
// 2. 准备 HTTP 服务器配置
// --------------------------
Console.WriteLine("[2] 准备 HTTP 服务器...");

const ushort port = 53317;

var clientInfo = new ClientInfo
{
    Alias = "我的电脑",
    Version = "2.3",
    DeviceType = DeviceType.Desktop,
    Token = tlsFingerprint  // HTTPS 模式下指纹 = 证书 SHA-256
};

var v2Config = new ServerConfigV2();
// v2Config.Pin = "123456"; // 可选：设置 PIN

var server = new LocalSendServer(
    port: port,
    tlsConfig: null,
    info: clientInfo,
    internalConfig: null,
    v2Config: v2Config,
    webSendConfig: null);

// 启动服务器（后台线程）
var serverCts = new CancellationTokenSource();
var serverTask = server.StartAsync(serverCts.Token);

// 等待服务器真正开始监听（最多 3 秒）
using var httpClientHealth = new System.Net.Http.HttpClient();
var healthy = false;
for (int i = 0; i < 30; i++)
{
    try
    {
        // 只要能连上就说明服务器已就绪（即使返回 403 也是服务器在响应）
        var resp = await httpClientHealth.GetAsync($"http://127.0.0.1:{port}/", serverCts.Token);
        healthy = true;
        break;
    }
    catch { }
    await Task.Delay(100, serverCts.Token);
}

Console.WriteLine($"    HTTP 服务器已在端口 {port} 启动" + (healthy ? "" : "（未检测到响应，但继续执行）"));
Console.WriteLine();

// --------------------------
// 3. 消费服务器事件（后台）
// --------------------------
Console.WriteLine("[3] 启动事件监听...");

_ = Task.Run(async () =>
{
    if (server.State.V2 == null) return;

    await foreach (var evt in server.State.V2.EventTx.Reader.ReadAllAsync(serverCts.Token))
    {
        switch (evt)
        {
            case ServerEventV2.Register reg:
                Console.WriteLine($"    [事件] 设备注册: {reg.Info.Alias} ({reg.Ip})");
                break;

            case ServerEventV2.PrepareUpload prep:
                Console.WriteLine($"    [事件] 文件上传请求: {prep.Info.Alias}, 会话={prep.SessionId}");
                Console.WriteLine($"      文件数: {prep.Files.Count}");

                // 接受所有文件
                var acceptedIds = prep.Files.Keys.ToHashSet();
                prep.DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Accept(acceptedIds));
                Console.WriteLine("      已接受全部文件");
                break;

            case ServerEventV2.FileUpload upload:
                Console.WriteLine($"    [事件] 文件上传中: {upload.File.FileName} ({upload.File.Size} bytes)");
                // 使用 PathTarget 保存文件到临时目录
                var savePath = Path.Combine(Path.GetTempPath(), upload.File.FileName);
                var resultTcs = new TaskCompletionSource<Result<Unit, string>>();
                var target = new FileUploadTarget.PathTarget(savePath, resultTcs);
                upload.TargetTx.TrySetResult(target);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100);
                        resultTcs.TrySetResult(new Result<Unit, string>(new Unit()));
                    }
                    catch { }
                });
                break;

            case ServerEventV2.SessionEnd end:
                Console.WriteLine($"    [事件] 会话结束: {end.SessionId}, 原因={end.Reason}");
                break;

            case ServerEventV2.CancelReceived cancel:
                Console.WriteLine($"    [事件] 收到取消: {cancel.SessionId}");
                break;
        }
    }
}, serverCts.Token);
Console.WriteLine();

// --------------------------
// 4. HTTP 客户端：扫描本机（自测）
// --------------------------
Console.WriteLine("[4] HTTP 客户端注册演示...");

// 带 TLS 证书的客户端（用于 HTTPS + mTLS 连接原版 LocalSend）
using var httpClientHttps = new LsHttpClientV2(tlsKeyPem, tlsCertPem);
// 不带证书的客户端（用于 HTTP 自测本机服务器）
using var httpClientHttp = new LsHttpClientV2();

// --- 4a. 本机自测（HTTP，不需要 mTLS）---
Console.WriteLine("    [4a] 本机自测（HTTP）...");
var localPayload = new RegisterDtoV2
{
    Alias = "扫描端",
    Version = "2.3",
    DeviceType = DeviceType.Desktop,
    Fingerprint = tlsFingerprint,
    Port = port,
    Protocol = ProtocolTypeV2.Http,
    Download = false
};

try
{
    var regResult = await httpClientHttp.RegisterAsync(
        protocol: ProtocolType.Http,
        ip: "127.0.0.1",
        port: port,
        payload: localPayload,
        ct: serverCts.Token);

    Console.WriteLine($"    本机自测成功！对方设备: {regResult.Body.Alias}");
    Console.WriteLine($"    版本: {regResult.Body.Version}");
}
catch (Exception ex)
{
    Console.WriteLine($"    本机自测失败: {ex.Message}");
}

//---4b.连接原版 LocalSend（HTTPS + mTLS）---
// 修改为你的手机/设备 IP（需与电脑同一局域网）
// 先对 192.168.1.53 做一次直接探测（5 秒超时 + 完整异常链），用于诊断
//const string probeIp = "192.168.1.53";
//Console.WriteLine($"    [直接探测] {probeIp}:{port} (HTTPS, 超时 5s)...");
//var probePayload = new RegisterDtoV2
//{
//    Alias = clientInfo.Alias,
//    Version = clientInfo.Version,
//    DeviceType = clientInfo.DeviceType,
//    Fingerprint = tlsFingerprint,
//    Port = port,
//    Protocol = ProtocolTypeV2.Https,
//    Download = false
//};
//try
//{
//    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(serverCts.Token);
//    probeCts.CancelAfter(TimeSpan.FromSeconds(5));
//    var probeResult = await httpClientHttps.RegisterAsync(
//        protocol: ProtocolType.Https,
//        ip: probeIp,
//        port: port,
//        payload: probePayload,
//        ct: probeCts.Token);
//    Console.WriteLine($"    [直接探测] ✓ 成功！对方: {probeResult.Body.Alias}, 指纹: {probeResult.Body.Fingerprint}");
//}
//catch (Exception ex)
//{
//    var msg = ex.Message;
//    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
//        msg += $" -> {inner.GetType().Name}: {inner.Message}";
//    Console.WriteLine($"    [直接探测] ✗ 失败: {msg}");
//}
//Console.WriteLine();

// --------------------------
// 5. UDP 组播设备发现
// --------------------------
Console.WriteLine("[5] UDP 组播设备发现...");

var multicastCts = new CancellationTokenSource();

using (var multicastService = new MulticastDiscoveryService(
    localInfo: clientInfo,
    httpPort: port,
    protocol: ProtocolTypeV2.Http,    // 本机服务器是 HTTP，声明 http 让对端能连回来
    multicastGroup: MulticastDiscoveryService.DefaultMulticastGroup,
    multicastPort: MulticastDiscoveryService.DefaultMulticastPort,
    serverRunning: true,
    httpClient: httpClientHttps))     // 带 mTLS 证书的客户端（连接 HTTPS 对端时用）
{
    multicastService.DeviceDiscovered += (_, peer) =>
    {
        Console.WriteLine($"    [发现] {peer}");
    };

    // 启动组播监听（后台任务）
    var multicastTask = multicastService.StartListeningAsync(multicastCts.Token);

    Console.WriteLine($"    组播组: {MulticastDiscoveryService.DefaultMulticastGroup}:{MulticastDiscoveryService.DefaultMulticastPort}");
    Console.WriteLine("    正在发送 announce 并监听...");
    Console.WriteLine("    （对端收到 announce 后会通过 HTTP 注册到本机 → 看到下方的 [事件] 日志）");

    // 等待几秒让发现发生
    await Task.Delay(3000, multicastCts.Token);

    // 列出已发现设备
    var devices = multicastService.DiscoveredDevices.ToList();
    if (devices.Count > 0)
    {
        Console.WriteLine($"    共发现 {devices.Count} 台设备:");
        foreach (var d in devices)
        {
            Console.WriteLine($"      - {d}");
        }
    }
    else
    {
        Console.WriteLine("    未发现其他设备");
        Console.WriteLine("    提示：如果同网段有其他 LocalSend 设备但未发现，请检查：");
        Console.WriteLine("      1. Windows 防火墙是否放行了 UDP 53317 端口");
        Console.WriteLine("         netsh advfirewall firewall add rule name=\"LocalSend Multicast\" dir=in action=allow protocol=UDP localport=53317");
        Console.WriteLine("      2. 确认对端 LocalSend 正在运行且未最小化");
        Console.WriteLine("      3. 确认两端在同一子网（如 192.168.1.x）");
    }

    Console.WriteLine("    等待 4 秒后停止组播监听...");
    await Task.Delay(4000, multicastCts.Token);
    Console.WriteLine("    停止组播监听...");
    multicastCts.Cancel();
    try { await multicastTask; } catch { }
}
Console.WriteLine();

// --------------------------
// 5b. HTTP 子网扫描（原版 LocalSend "Search devices" 机制，组播的保底方案）
// --------------------------
// 当 UDP 组播因防火墙/虚拟网卡/路由等原因失效时，HTTP 子网扫描是最可靠的发现方式。
// 原理：遍历本机所在 /24 子网的所有 IP（排除自己），并发 POST /api/localsend/v2/register，
// 能成功响应的就是 LocalSend 设备。对齐 Dart 端 http_scan_discovery.dart（并发 50）。
Console.WriteLine("[5b] HTTP 子网扫描设备发现（原版 Search devices 机制）...");
Console.WriteLine("    使用 HTTPS + mTLS 客户端证书（与原版 LocalSend 默认配置一致）");

//var scanService = new HttpScanDiscoveryService(
//    httpClient: httpClientHttps,      // 带 mTLS 证书，能连原版 LocalSend 的 HTTPS 服务
//    localInfo: clientInfo,
//    port: port,
//    protocol: ProtocolTypeV2.Https,   // 原版 LocalSend 默认 HTTPS
//    ownFingerprint: tlsFingerprint,
//    perRequestTimeout: TimeSpan.FromMilliseconds(3500));  // 5 秒，留足 TLS 握手时间

//var scanFound = new System.Collections.Concurrent.ConcurrentDictionary<string, MulticastPeerInfo>(StringComparer.OrdinalIgnoreCase);
//scanService.DeviceDiscovered += (_, peer) =>
//{
//    scanFound.TryAdd(peer.Fingerprint, peer);
//};

//try
//{
//    var scannedDevices = await scanService.ScanAllSubnetsAsync(serverCts.Token);

//    if (scannedDevices.Count > 0)
//    {
//        Console.WriteLine($"    [HTTP扫描] 共发现 {scannedDevices.Count} 台设备:");
//        foreach (var d in scannedDevices)
//        {
//            Console.WriteLine($"      - {d}");
//        }
//        // 特别提示 192.168.1.53
//        var target = scannedDevices.FirstOrDefault(d => d.IP.ToString() == "192.168.1.53");
//        if (target != null)
//        {
//            Console.WriteLine($"    ✓✓✓ 已成功扫描到 192.168.1.53: {target.Alias} (指纹 {target.Fingerprint.Substring(0, Math.Min(12, target.Fingerprint.Length))}...)");
//        }
//    }
//    else
//    {
//        Console.WriteLine("    [HTTP扫描] 未发现任何设备");
//    }
//}
//catch (Exception ex)
//{
//    Console.WriteLine($"    [HTTP扫描] 扫描异常: {ex.Message}");
//}
//Console.WriteLine();

// --------------------------
// 5c. HTTPS 文件传输演示（发送 + 接收）
// --------------------------
// 演示 LocalSend v2.1 协议的文件传输 API：
//   发送端：POST /prepare-upload（声明文件清单）→ 对端接受 → POST /upload（传文件内容）
//   接收端：服务端收到 prepare-upload/upload 后通过事件通道通知应用（见 step 3 事件监听）
Console.WriteLine("[5c] HTTPS 文件传输演示...");
Console.WriteLine("    准备测试文件...");
var transferFile = Path.Combine(Path.GetTempPath(), "localsend_example_hello.txt");
var transferText = "Hello from LocalSend C# example!" + Environment.NewLine + DateTime.Now.ToString("s");
await File.WriteAllTextAsync(transferFile, transferText, serverCts.Token);
var transferBytes = new FileInfo(transferFile).Length;
Console.WriteLine($"    测试文件: {transferFile} ({transferBytes} 字节)");

// --- 5c.1 发送文件到原版 LocalSend（HTTPS + mTLS）---
// 目标：192.168.1.53（上一步扫描到的"高效的蘑菇"）
// 注意：prepare-upload 需要对端用户在弹窗中点击"接收"；无人值守时可能被拒或超时。
const string sendTargetIp = "192.168.1.50";
Console.WriteLine($"    [发送] 向 {sendTargetIp}:{port} 发起 prepare-upload (HTTPS+mTLS)...");
var sendPayload = new PrepareUploadRequestDtoV2
{
    Info = new RegisterDtoV2
    {
        Alias = clientInfo.Alias,
        Version = clientInfo.Version,
        DeviceType = clientInfo.DeviceType,
        Fingerprint = tlsFingerprint,
        Port = port,
        Protocol = ProtocolTypeV2.Https,
        Download = false
    },
    Files = new Dictionary<string, FileDto>
    {
        ["1"] = new FileDto
        {
            Id = "1",
            FileName = "localsend_example_hello.txt",
            Size = (ulong)transferBytes,
            FileType = "text/plain"
        }
    }
};

try
{
    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(serverCts.Token);
    sendCts.CancelAfter(TimeSpan.FromSeconds(8));
    var prepResult = await httpClientHttps.PrepareUploadAsync(
        protocol: ProtocolType.Https,
        ip: sendTargetIp,
        port: port,
        publicKey: null,
        payload: sendPayload,
        pin: null,
        ct: sendCts.Token);

    if (prepResult.Response != null && prepResult.Response.Files.TryGetValue("1", out var uploadToken))
    {
        Console.WriteLine($"    [发送] 对端已接受！sessionId={prepResult.Response.SessionId}");
        Console.WriteLine("    [发送] 开始上传文件内容...");
        var sendContent = new FileContent.FilePath(transferFile);
        ulong sentBytes = 0;
        await httpClientHttps.UploadAsync(
            protocol: ProtocolType.Https,
            ip: sendTargetIp,
            port: port,
            publicKey: null,
            sessionId: prepResult.Response.SessionId,
            fileId: "1",
            token: uploadToken,
            content: sendContent,
            progress: b => sentBytes = b,
            cancel: serverCts.Token);
        Console.WriteLine($"    [发送] ✓ 上传完成，已发送 {sentBytes} 字节到 {sendTargetIp}");
    }
    else
    {
        Console.WriteLine("    [发送] 对端未接受任何文件（可能被拒绝）");
    }
}
catch (Exception ex)
{
    var msg = ex.Message;
    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
        msg += $" -> {inner.GetType().Name}: {inner.Message}";
    Console.WriteLine($"    [发送] 失败（对端可能需手动点\"接收\"或已拒绝）: {msg}");
}
Console.WriteLine();

// --- 5c.2 接收文件（本机 HTTP 自测，演示服务端接收事件链）---
// 用 httpClientHttp 向本机服务器发起 prepare-upload + upload，
// 上方 step 3 的事件监听会收到 PrepareUpload / FileUpload 事件并保存文件到临时目录。
// 说明：本机服务器当前以 HTTP 模式运行（tlsConfig=null）；
//       若配置 TlsConfig，则接收亦走 HTTPS+mTLS，服务端事件处理逻辑完全一致。
Console.WriteLine("    [接收] 本机自测：向本机服务器发起 prepare-upload + upload (HTTP)...");
var recvPayload = new PrepareUploadRequestDtoV2
{
    Info = new RegisterDtoV2
    {
        Alias = "自测发送端",
        Version = clientInfo.Version,
        DeviceType = clientInfo.DeviceType,
        Fingerprint = tlsFingerprint,
        Port = port,
        Protocol = ProtocolTypeV2.Http,
        Download = false
    },
    Files = new Dictionary<string, FileDto>
    {
        ["1"] = new FileDto
        {
            Id = "1",
            FileName = "localsend_recv_test.txt",
            Size = (ulong)transferBytes,
            FileType = "text/plain"
        }
    }
};

try
{
    var recvPrep = await httpClientHttp.PrepareUploadAsync(
        protocol: ProtocolType.Http,
        ip: "127.0.0.1",
        port: port,
        publicKey: null,
        payload: recvPayload,
        pin: null,
        ct: serverCts.Token);

    if (recvPrep.Response != null && recvPrep.Response.Files.TryGetValue("1", out var recvToken))
    {
        Console.WriteLine($"    [接收] 本机服务器已接受！sessionId={recvPrep.Response.SessionId}");
        Console.WriteLine("    [接收] 上传文件内容（触发服务端 FileUpload 事件 → 保存到临时目录）...");
        var recvContent = new FileContent.FilePath(transferFile);
        ulong recvSent = 0;
        await httpClientHttp.UploadAsync(
            protocol: ProtocolType.Http,
            ip: "127.0.0.1",
            port: port,
            publicKey: null,
            sessionId: recvPrep.Response.SessionId,
            fileId: "1",
            token: recvToken,
            content: recvContent,
            progress: b => recvSent = b,
            cancel: serverCts.Token);
        Console.WriteLine($"    [接收] ✓ 上传完成 {recvSent} 字节；服务端事件已触发（见上方 [事件] 日志）");
        await Task.Delay(500, serverCts.Token); // 等待事件处理完成
        var savedPath = Path.Combine(Path.GetTempPath(), "localsend_recv_test.txt");
        if (File.Exists(savedPath))
        {
            var savedContent = await File.ReadAllTextAsync(savedPath, serverCts.Token);
            Console.WriteLine($"    [接收] 服务端保存文件: {savedPath}");
            Console.WriteLine($"    [接收] 内容: {savedContent.Trim()}");
        }
    }
    else
    {
        Console.WriteLine("    [接收] 本机服务器未接受（异常）");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    [接收] 失败: {ex.Message}");
}
// 清理测试文件
try { File.Delete(transferFile); } catch { }
Console.WriteLine();

// --------------------------
// 6. WebRTC Loopback 传输演示
// --------------------------
Console.WriteLine("[6] WebRTC Loopback 传输演示...");

var webrtcCts = new CancellationTokenSource();

// 创建一对互连的 Loopback PeerConnection（A=发送端, B=接收端）
var (peerA, peerB) = LoopbackPeerConnectionFactory.CreatePair();

// 6.1 在 peerA（发送端）创建 data channel
var dataChannelA = peerA.CreateDataChannel("data");

// 6.2 在 peerB（接收端）注册 data channel 回调
IRtcDataChannel? dataChannelB = null;
var dataChannelBTcs = new TaskCompletionSource<IRtcDataChannel>();
peerB.OnDataChannel(ch =>
{
    dataChannelB = ch;
    dataChannelBTcs.TrySetResult(ch);
});

// 6.3 交换 SDP（模拟信令交换）
// 发送端生成 offer
string offer = await peerA.CreateOfferAsync(webrtcCts.Token);
await peerA.SetLocalDescriptionAsync(offer, webrtcCts.Token);

// 接收端设置远端 offer，生成 answer
await peerB.SetRemoteDescriptionAsync(offer, isOffer: true, ct: webrtcCts.Token);
string answer = await peerB.CreateAnswerAsync(webrtcCts.Token);
await peerB.SetLocalDescriptionAsync(answer, webrtcCts.Token);

// 发送端设置远端 answer
await peerA.SetRemoteDescriptionAsync(answer, isOffer: false, ct: webrtcCts.Token);

Console.WriteLine("    SDP 交换完成");

// 6.4 等待 data channel 打开
await dataChannelA.Opened.WaitAsync(webrtcCts.Token);
Console.WriteLine("    发送端数据通道已打开");

var recvChannel = await dataChannelBTcs.Task.WaitAsync(webrtcCts.Token);
await recvChannel.Opened.WaitAsync(webrtcCts.Token);
Console.WriteLine("    接收端数据通道已打开");

// 6.5 发送端：发送文本消息
await dataChannelA.SendTextAsync("hello from LocalSend!", webrtcCts.Token);
Console.WriteLine("    发送端已发送消息");

// 6.6 接收端：接收消息
var receivedMsg = await recvChannel.Messages.ReadAsync(webrtcCts.Token);
if (receivedMsg.IsString)
{
    var text = System.Text.Encoding.UTF8.GetString(receivedMsg.Data);
    Console.WriteLine($"    接收端收到消息: {text}");
}

// 6.7 发送端：发送二进制数据
byte[] binaryData = System.Text.Encoding.UTF8.GetBytes("Hello, WebRTC!");
await dataChannelA.SendBinaryAsync(binaryData, webrtcCts.Token);
Console.WriteLine($"    发送端已发送 {binaryData.Length} 字节二进制数据");

// 6.8 接收端：接收二进制数据
var receivedBinary = await recvChannel.Messages.ReadAsync(webrtcCts.Token);
if (!receivedBinary.IsString)
{
    Console.WriteLine($"    接收端收到 {receivedBinary.Data.Length} 字节二进制数据");
    var text = System.Text.Encoding.UTF8.GetString(receivedBinary.Data);
    Console.WriteLine($"    内容: {text}");
}

// 6.9 关闭
await dataChannelA.CloseAsync(webrtcCts.Token);
await peerA.CloseAsync(webrtcCts.Token);
await peerB.CloseAsync(webrtcCts.Token);

Console.WriteLine("    Loopback 演示完成");
Console.WriteLine();

// --------------------------
// 7. 清理
// --------------------------
Console.WriteLine("[7] 清理资源...");
serverCts.Cancel();
webrtcCts.Dispose();

try
{
    await server.StopAsync();
}
catch { /* ignore */ }

server.Dispose();
Console.WriteLine("    已清理完成");

Console.WriteLine();
Console.WriteLine("=== 示例程序结束 ===");