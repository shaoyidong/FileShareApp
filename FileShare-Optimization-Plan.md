# FileShare 优化计划

## 概述

基于与 LocalSend（Rust/axum/WebRTC）的对比分析，针对 FileShare（C# / TCP直连架构）制定以下优化计划。优化目标：**保持"局域网内简单可靠文件传输"的核心定位**，同时提升健壮性、安全性和可维护性。

---

## 优先级分级

| 级别 | 说明 |
|------|------|
| **P0** | 正确性 / 安全性修复（必须修复） |
| **P1** | 性能 / 并发优化（显著改进） |
| **P2** | 功能增强（TLS、校验、日志） |
| **P3** | 体验优化（多网卡、mDNS） |

---

## P0 - 正确性与安全性修复

### 1.1 修复同步阻塞 / 死锁风险

**文件**: `FileShare.Core/Services/FileShareServiceManager.cs`

- **问题**: `Dispose()` 方法使用 `.GetAwaiter().GetResult()` 进行同步-异步混合调用，在 UI 上下文或 ASP.NET 中可能导致死锁。
- **修复**: 改为完全异步的 `DisposeAsync()` 模式，或使用 `Task.Run` + `ConfigureAwait(false)` 确保不在 UI 线程阻塞。
- **行号**: [L217-L226](file:///d:/tmp/localSend/FileShare.Core/Services/FileShareServiceManager.cs#L217-L226)

### 1.2 移除 .Wait() 同步阻塞

**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`

- **问题**: `SendDiscoveryPacket()` 使用 `.Wait()` 同步阻塞异步发送操作，可能导致死锁。
- **修复**: 改为 `async Task SendDiscoveryPacketAsync()`，在调用方使用 `await`。
- **行号**: [L61-L77](file:///d:/tmp/localSend/FileShare.Core/Network/UdpDiscoveryService.cs#L61-L77)

### 1.3 修复嵌套锁（重入问题）

**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`

- **问题**: `AdjustBroadcastInterval()` 方法中存在 `lock(_lock)` 嵌套（L340 外层 + L353 内层），虽然 .NET 同一线程可重入但设计上不安全。
- **修复**: 消除嵌套锁，将 `deviceCount` 读取移到锁外或使用单独的锁。
- **行号**: [L338-L376](file:///d:/tmp/localSend/FileShare.Core/Network/UdpDiscoveryService.cs#L338-L376)

### 1.4 修复异常过滤器字符串硬编码

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: 异常过滤器使用中文硬编码字符串 `"你的主机中的软件中止了一个已建立的连接"`，在英文系统上无法匹配。
- **修复**: 同时匹配 `SocketError.ConnectionReset` 或 `WinSockConnectionReset`。
- **行号**: [L232](file:///d:/tmp/localSend/FileShare.Core/Network/TcpFileTransferService.cs#L232), [L696](file:///d:/tmp/localSend/FileShare.Core/Network/TcpFileTransferService.cs#L696)

### 1.5 添加输入校验防止恶意请求

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: `TransferRequest` 接收的 `FileSize` 字段未校验，恶意客户端可发送超大/负数文件大小。
- **修复**: 添加合理的文件大小上限（如 100GB）和非负校验。
- **位置**: `HandleSendFileRequest()` 和 `HandleFileData()` 方法入口。

---

## P1 - 性能与并发优化

### 2.1 UdpDiscoveryService：List + lock → ConcurrentDictionary

**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`

- **问题**: `_discoveredDevices` 使用 `List<DeviceInfo>` + `lock(_lock)` 保护，在高并发事件回调中成为瓶颈。
- **修复**: 替换为 `ConcurrentDictionary<string, DeviceInfo>`（Key = DeviceId），移除大部分 lock 语句。
- **影响行**: L41, L160-L178, L209-L224, L286-L289, L429-L451, L456-L462

### 2.2 TcpFileTransferService：添加并发连接上限

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: `AcceptConnectionsAsync()` 为每个连接启动 `Task.Run`，无并发上限，恶意客户端可耗尽资源。
- **修复**: 使用 `SemaphoreSlim` 限制最大并发连接数（默认 50），同时添加每 IP 速率限制。
- **行号**: [L161-L179](file:///d:/tmp/localSend/FileShare.Core/Network/TcpFileTransferService.cs#L161-L179)

### 2.3 移除 DataAvailable 轮询

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: `HandleClientAsync()` 使用 `stream.DataAvailable` + `Task.Delay(100)` 轮询等待数据，浪费 CPU 且增加延迟。
- **修复**: 改为直接基于协议帧的阻塞式 `ReadAsync`，配合读取超时机制（使用 `CancellationToken` + 超时）。
- **行号**: [L197-L203](file:///d:/tmp/localSend/FileShare.Core/Network/TcpFileTransferService.cs#L197-L203)

### 2.4 日志记录替换 Console.WriteLine

**涉及文件**:
- `FileShare.Core/Network/UdpDiscoveryService.cs`
- `FileShare.Core/Network/TcpFileTransferService.cs`
- `FileShare.Core/Services/FileShareServiceManager.cs`

- **问题**: 大量使用 `Console.WriteLine` / `Debug.WriteLine` 输出日志，无日志级别、无结构化输出，生产环境不可控。
- **修复**: 引入 `Microsoft.Extensions.Logging` 接口（不直接依赖具体实现），添加 `ILogger` 注入。
- **注意**: 项目使用 AOT 发布，确保日志实现兼容 AOT（如 `Microsoft.Extensions.Logging.Console`）。

### 2.5 GetLocalIpAddress 支持多网卡

**文件**: `FileShare.Core/Services/FileShareServiceManager.cs`

- **问题**: `GetLocalIpAddress()` 仅返回第一个 IPv4 地址，多网卡/VPN 环境下可能获取错误 IP。
- **修复**: 返回所有活跃网卡的 IP 列表，或优先选择物理网卡的 IP。
- **行号**: [L107-L125](file:///d:/tmp/localSend/FileShare.Core/Services/FileShareServiceManager.cs#L107-L125)

---

## P2 - 功能增强

### 3.1 可选 TLS 加密传输

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: 传输层为裸 TCP，在非受信任网络上通信未加密。
- **修复**: 添加 `SslStream` 可选支持，通过配置开关启用 TLS。使用证书可由设备首次配对时交换（简化版）。
- **注意**: TLS 证书交换方案需要额外设计，建议后续迭代实现。

### 3.2 文件校验（Checksum）

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: 传输完成后无完整性校验，网络错误可能导致文件损坏。
- **修复**: 在 `TransferRequest` 中添加 `Checksum` 字段（SHA256），接收方完成后比对校验。
- **涉及模型**: `TransferRequest` 类（添加 `Checksum` 属性）

### 3.3 传输进度与度量

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: 进度事件仅在 UI 需要时有用，缺少服务端级别的传输统计。
- **修复**: 添加传输速率（KB/s）统计、总传输量统计，方便监控。

### 3.4 优雅关闭与资源清理

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: `Stop()` 方法直接调用 `_listener.Stop()`，可能中断正在进行的传输。
- **修复**: 实现优雅关闭：先停止接受新连接，等待现有传输完成或超时后再释放资源。

---

## P3 - 体验优化

### 4.1 多网卡广播支持

**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`

- **问题**: 使用 `IPAddress.Broadcast` 广播，在多网卡/VPN 环境下可能无法到达所有子网。
- **修复**: 枚举所有活跃网卡，对每个网卡的子网广播。

### 4.2 mDNS/SSDP 替代发现机制

**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`

- **问题**: 纯 UDP 广播在某些受限网络（如隔离 Wi-Fi）下可能被阻止。
- **修复**: 添加 mDNS（Bonjour）作为可选发现机制，提高发现成功率。

### 4.3 传输数据限流与背压

**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`

- **问题**: 发送方发送速度不受接收方处理能力限制，可能导致内存溢出。
- **修复**: 实现基于 `Channel<T>` 的生产者-消费者模型，提供天然的背压控制。

---

## 实施路线图

### 第一阶段（立即实施）
- [x] P0: 修复 Dispose 同步阻塞、.Wait()、嵌套锁、异常过滤器、输入校验
- [x] P1: ConcurrentDictionary 替换、SemaphoreSlim 并发限制、移除 DataAvailable 轮询
- [x] P1: 每 IP 并发连接上限（补充实现）、移除死代码 `_ipLastRequestTimes`

### 第二阶段（后续迭代）
- [x] P1: 引入 ILogger（`Microsoft.Extensions.Logging.Abstractions`，可选注入，默认 `NullLogger`，Desktop/Mobile 已接线）
- [x] P1: 多网卡 IP 支持（`NetworkInterface` 枚举 + 网关优先排序）
- [x] P2: 文件校验（SHA256）、优雅关闭（停止接受 → 等待活跃传输 → 强制取消）
- [x] P3: 多网卡定向广播（枚举子网，`ip | ~mask`，兜底有限广播）

### 第三阶段（已全部落地）
- [x] P2: TLS 加密传输（`SslStream` + 自签名证书 + 指纹 TOFU + 向后兼容探测）
- [x] P3: mDNS 服务发现（`_fileshare._tcp.local`，作为 UDP 广播的可选补充）
- [x] P3: 基于 `Channel<T>` 的生产者-消费者背压模型（重构 `SendFileDataAsync`，移除 50ms 轮询）
- [x] P2: 传输度量（速率 KB/s、累计字节统计，通过 `FileTransferInfo.TransferRateBytesPerSec` / `AverageRateBytesPerSec` 暴露）
- [x] 补充：Serilog 日志实现（Desktop 与 Avalonia 日志集成，Core 仍仅依赖 `Microsoft.Extensions.Logging` 抽象）

---

## 第四阶段实施详情（本轮）

### 4.1 Serilog 日志实现（Desktop）

**涉及文件**:
- [FileShare.Desktop/Logging/SerilogSetup.cs](file:///d:/StudyNode/localsend/FileShare.Desktop/Logging/SerilogSetup.cs)
- [FileShare.Desktop/App.axaml.cs](file:///d:/StudyNode/localsend/FileShare.Desktop/App.axaml.cs)
- [FileShare.Desktop/FileShare.Desktop.csproj](file:///d:/StudyNode/localsend/FileShare.Desktop/FileShare.Desktop.csproj)

- **设计**：Core 层只依赖 `Microsoft.Extensions.Logging.ILogger` 抽象（AOT 安全），具体实现由宿主注入。Desktop 使用 Serilog 作为统一后端。
- **输出**：Debug 输出窗口 + 按天滚动文件（`fileshare-YYYYMMDD.log`，保留 7 天，单文件 10MB 上限）。
- **Avalonia 集成**：`AvaloniaSerilogSink` 实现 `Avalonia.Logging.ILogSink`，将 Avalonia 内部日志（Information 及以上）转发到 Serilog，统一日志出口。
- **注入**：`App.axaml.cs` 创建 Serilog Logger → `LoggerFactory.Create(builder => builder.AddSerilog(...))` → 传入 `FileShareServiceManager(loggerFactory)`。
- **NuGet**：`Serilog`、`Serilog.Sinks.File`、`Serilog.Extensions.Logging`、`Serilog.Sinks.Debug`。

### 4.2 TLS 加密传输（P2）

**涉及文件**:
- [FileShare.Core/Network/Tls/TlsOptions.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Tls/TlsOptions.cs) — 配置（Enabled / 证书目录 / 指纹库路径 / 密码 / 有效期）
- [FileShare.Core/Network/Tls/SelfSignedCertificateProvider.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Tls/SelfSignedCertificateProvider.cs) — 自签名证书生成与持久化
- [FileShare.Core/Network/Tls/FingerprintStore.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Tls/FingerprintStore.cs) — 指纹 TOFU 信任库
- [FileShare.Core/Network/Tls/PrependedStream.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Tls/PrependedStream.cs) — 预读字节回放流
- [FileShare.Core/Network/TcpFileTransferService.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/TcpFileTransferService.cs) — TLS 探测/升级/校验

- **协议协商**：接收方在 `_tlsEnabled` 时探测首字节：若为 `0x16 0x03`（TLS ClientHello）则升级到 `SslStream`，否则用 `PrependedStream` 回放已读字节走裸 TCP（与旧版本完全兼容）。
- **证书策略**：每设备生成 RSA 自签名证书（CN=设备ID），持久化为 PFX。
- **Windows Schannel 兼容**：关键修复 — 生成证书后从 PFX 重新加载（`X509KeyStorageFlags.PersistKeySet | Exportable | UserKeySet`），使私钥进入 Windows 密钥存储区，否则 Schannel 报"主机中的软件中止了一个已建立的连接"。
- **指纹 TOFU**：首次连接记录对端证书 SHA256 指纹；后续连接指纹不一致则判定 MITM 并拒绝。存储格式 `deviceId:指纹`（纯文本，AOT 安全）。
- **降级容错**：TLS 初始化失败时降级为裸 TCP，不阻断文件传输服务启动。
- **握手超时**：15 秒（`TlsHandshakeTimeoutMs`），避免恶意连接挂起。

### 4.3 mDNS 服务发现（P3）

**涉及文件**:
- [FileShare.Core/Network/Mdns/MdnsMessage.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Mdns/MdnsMessage.cs) — mDNS 报文编解码（PTR/SRV/TXT/A）
- [FileShare.Core/Network/Mdns/MdnsService.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/Mdns/MdnsService.cs) — mDNS 服务发布与发现
- [FileShare.Core/Network/UdpDiscoveryService.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/UdpDiscoveryService.cs) — `RegisterExternalDevice` 接入 mDNS 设备

- **服务类型**：`_fileshare._tcp.local`。
- **多播**：加入 `224.0.0.251:5353`，TTL=255。
- **行为**：启动时主动公告 + 查询；周期公告 + 过期清理（与 UDP 广播互补，提高受限网络发现成功率）。
- **容错**：多播组绑定失败时仅记录日志降级，不影响主服务。
- **集成**：发现设备后通过 `UdpDiscoveryService.RegisterExternalDevice` 注入统一设备列表。

### 4.4 Channel<T> 背压模型（P3）

**涉及文件**:
- [FileShare.Core/Network/TcpFileTransferService.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/TcpFileTransferService.cs) — `SendFileDataAsync`

- **模型**：生产者（读文件 → 写 Channel）+ 消费者（读 Channel → 写网络），容量 4。
- **收益**：天然背压（网络慢时阻塞读文件，避免内存膨胀）；移除原 50ms 轮询取消检查，CPU 与延迟双降。
- **取消**：Channel 关闭 + CancellationToken 协同，取消时生产者/消费者均快速退出。

### 4.5 传输度量（P2）

**涉及文件**:
- [FileShare.Core/Network/TcpFileTransferService.cs](file:///d:/StudyNode/localsend/FileShare.Core/Network/TcpFileTransferService.cs) — `TotalBytesSent` / `TotalBytesReceived` / 速率采样
- [FileShare.Core/Models/FileTransferInfo.cs](file:///d:/StudyNode/localsend/FileShare.Core/Models/FileTransferInfo.cs) — `TransferRateBytesPerSec` / `AverageRateBytesPerSec`

- **累计统计**：`Interlocked` 原子计数，线程安全。
- **实时速率**：按传输 ID 采样（时间 + 字节），进度事件中计算瞬时速率与平均速率。

### 4.6 验证基线

- Core: 50/50 通过（含 `TlsHandshakeDiagnosticTests`、`TlsFileTransferIntegrationTests`、`TcpFileTransferIntegrationTests`、`MdnsCodecTests`）
- Desktop: 16/16 通过
- Mobile（MAUI）：本机未构建验证（需 workload），改动仅 `MauiProgram.cs` 传 `loggerFactory`，低风险

---

## 补充优化（实施过程中发现并修复，原计划未列出）

### A.1 修复手动刷新失效 Bug（P0 正确性）
**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`
- **问题**: `SendDiscoveryPacketAsync()` 原守卫为 `if (_isRunning || ...)`，在服务运行时直接返回，导致 `FileShareServiceManager.RefreshDevicesAsync()` 手动刷新完全失效（被测试以"不抛异常"掩盖）。
- **修复**: 改为 `if (!_isRunning || _isDisposed || _udpClient == null) return;`，仅在运行时发送。

### A.2 UDP 发送串行化（健壮性）
**文件**: `FileShare.Core/Network/UdpDiscoveryService.cs`
- **问题**: 自动广播、回应、手动刷新三处可能并发调用 `UdpClient.SendAsync`，存在释放时序竞争。
- **修复**: 引入 `SemaphoreSlim _sendLock`，统一通过 `SendLockedAsync()` 序列化所有发送。

### A.3 每并 IP 发连接上限（原 2.2 计划的"每 IP 速率限制"落地）
**文件**: `FileShare.Core/Network/TcpFileTransferService.cs`
- **问题**: 仅有全局并发上限，单一主机可耗尽全部 50 个连接；`_ipLastRequestTimes` 字段为死代码。
- **修复**: 新增 `_ipConnectionCounts`（`ConcurrentDictionary<string,int>`），单 IP 最大并发 10；移除死代码字段。

### A.4 异步释放 IAsyncDisposable
**文件**: `FileShare.Core/Services/FileShareServiceManager.cs` / `IFileShareServiceManager.cs`
- **问题**: `Dispose()` 仍以 `Task.Run().GetAwaiter().GetResult()` 同步阻塞。
- **修复**: 接口与实现新增 `IAsyncDisposable`，推荐 `DisposeAsync()` 异步停止；保留同步 `Dispose()` 作为回退。


---

## 技术约束

1. **AOT 兼容**: 项目发布配置 `PublishAot=true`，所有新增依赖必须 AOT 兼容（避免使用 `System.Reflection.Emit`、动态编译等）。
2. **.NET 10.0**: 目标框架 `net10.0`，可使用最新 BCL API。
3. **跨平台**: 支持 Windows / Linux / macOS / Android / iOS，避免平台特定 API。
4. **JSON 源生成**: 已使用 `System.Text.Json` 源生成器，新增模型需同步更新 `SourceGenerationContext`。
