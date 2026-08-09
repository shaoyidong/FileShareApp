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

### 第三阶段（长期目标，未实施）
- [ ] P2: TLS 加密传输（`SslStream`，需证书交换设计）
- [ ] P3: mDNS/SSDP 替代发现机制
- [ ] P3: 基于 `Channel<T>` 的生产者-消费者背压模型

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
