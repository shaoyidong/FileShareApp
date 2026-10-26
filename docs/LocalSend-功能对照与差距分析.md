# FileShare vs LocalSend：功能对照与差距分析

> 对照对象：[localsend/localsend](https://github.com/localsend/localsend)（Flutter + Rust 实现，协议 LocalSend Protocol v2）
> 被评估项目：本仓库 `FileShare.Core`（net10.0）+ `FileShare.Desktop`（Avalonia 11）+ `FileShare.Mobile`（.NET MAUI）
> 评估方式：通读本仓库全部源码（Core / Desktop / Mobile），逐模块与 LocalSend 的能力清单对照。

---

## 0. 结论摘要

1. **核心链路是通的**：三路并行发现（mDNS + UDP 组播 + UDP 广播）、TCP 直连传输、分块 + SHA-256 校验、TLS 可选升级（双向证书 + TOFU 指纹）、SQLite 接收历史、Avalonia/MAUI 双端 UI —— 这些是 LocalSend 也有的主干能力，本项目的实现质量（背压 Channel、并发连接限流、每 IP 限流、优雅关闭）甚至比多数教学项目更扎实。
2. **最大的"能力缺口"不是某一个按钮，而是交互范式**：LocalSend 是「发现即连接、选中即发送、多选/文件夹/文本一起发、收藏设备免确认、找不到就用 HTTP 扫描兜底」；本项目目前是「单选一个设备 + 发一个文件」，缺少批量、收藏、免确认、兜底连接这几条常用路径。
3. **最大的"生态缺口"是协议不互通**：LocalSend 用 HTTP(S) REST v2（`/api/localsend/v2/prepare-upload` → `/upload`）跑在 53317，本项目用自定义 TCP + 4 字节长度头 + JSON 帧跑在 5237。**两者互相看不见、传不了**。仓库里已有的 `LocalSend.Core`（协议移植）可以做成桥接层，但这属于"可选加分项"而非必须。
4. **本次已交付的补强**（详见第 4 节）：
   - ✅ 手动输入 IP / IP:端口 / IPv6 / 主机名 **直连**（含 UDP 身份探测 + TCP 探活兜底）
   - ✅ **网段扫描**（CIDR，并发 64，带进度与取消，自动跳过本机地址）
   - ✅ **多文件批量发送**（顺序队列 + 批次进度）
   - ✅ **设置持久化**（JSON，AOT 友好）：设备名/类型/端口/TLS/保存目录/自动接收/收藏/皮肤
   - ✅ **收藏设备 + 对收藏设备自动接收**（对应 Quick Save）
   - ✅ **接收历史记录修复**：手动直连的设备也能正确落历史（原先按 ReceiverId 比对会漏记）
   - ✅ **发现服务 Stop→Start 事件链丢失缺陷修复**
   - ✅ **传输进度跨线程改 UI 属性缺陷修复**（原代码在后台线程直接 set 绑定属性）
   - ✅ **统一皮肤系统（Windows 7 / Windows XP 一键切换）** + 设置页 + 主界面重做
   - ✅ 修复了与实现脱节的旧测试（引用已删除 API），补充了地址解析 / 网段扫描 / 设置的单元测试

---

## 1. 本项目现状盘点

### 1.1 架构

```
FileShare.Core
├─ Network
│  ├─ CompositeDiscoveryService      // 三路发现的聚合器（去重/过期/上限 200）
│  ├─ Discovery/UdpBroadcastDiscoveryService   // 5236，含子网定向广播 + 255.255.255.255
│  ├─ Discovery/UdpMulticastDiscoveryService   // 224.0.0.167:53317
│  ├─ Mdns/MdnsService                          // _fileshare._tcp.local，自实现编解码
│  ├─ TcpFileTransferService        // 5237，长度前缀 JSON 帧 + 可升级 TLS
│  ├─ Discovery/DeviceProbeService  // 【新增】单设备探测
│  ├─ Discovery/SubnetScanner       // 【新增】网段扫描
│  └─ LocalNetworkHelper            // 【新增】网卡/掩码/CIDR 工具
├─ Crypto           // RSA 自签证书、TLS 指纹库（TOFU）、Ed25519/RSA-PSS 令牌签名
├─ Services         // FileShareServiceManager、SQLite(Dapper)、设置(AppSettings)
└─ Models           // DeviceInfo / FileTransferInfo / 传输状态机
```

### 1.2 传输协议（自定义）

| 帧 | 内容 |
|---|---|
| 4 字节小端长度 + JSON `TransferRequest` | `SendFileRequest` / `SendFileData` / `FileChecksum` |
| 4 字节小端长度 + JSON `TransferResponse` | `Accepted` + `Message` |

流程：`SendFileRequest` → 等用户确认（60s 超时）→ `SendFileData`（64KB 块，读的同时 `GlobalKill`）→ 接收方本地算 SHA-256 → 发送方发 `FileChecksum` → 比对 → `Completed`。

**与 LocalSend 协议对比**

| 维度 | LocalSend Protocol v2 | 本项目 |
|---|---|---|
| 载体 | HTTPS（自签证书）REST | 裸 TCP，可选升级 TLS |
| 端口 | 53317（HTTP + 组播同端口） | 5237（传输）+ 5236/53317（发现） |
| 准备阶段 | `POST /prepare-upload` → `fileId/sessionId/token` | `SendFileRequest` 帧 |
| 上传 | `POST /upload?sessionId&fileId&token`（multipart，可并发） | `SendFileData` 帧（单文件串行） |
| 取消 | `POST /cancel` | 关闭连接 / 错误响应 |
| 元数据 | 支持文件夹结构、多文件、文本/剪贴板 | 单文件、平铺 |
| 互通 | 与官方客户端互通 | **不互通** |

---

## 2. 功能对照表

图例：✅ 已有且完善 · 🟡 有但不足 · ❌ 缺失 · 🆕 本次新增

### 2.1 传输能力

| 功能 | LocalSend | 本项目（本次前） | 现状 |
|---|---|---|---|
| 单文件发送/接收 | ✅ | ✅ | ✅ |
| 多文件一次发送 | ✅ | ❌（只能一次一个） | 🆕 已支持（顺序队列 + 批次进度） |
| 文件夹（保留目录结构） | ✅ | ❌ | ❌ P1 |
| 文本 / 剪贴板发送 | ✅ | ❌ | ❌ P2 |
| 拖拽发送 | ✅ | ❌ | ❌ P1 |
| 发送队列可视化 / 重排 | ✅ | 🟡（有任务列表，无队列概念） | 🟡 |
| 进度 + 速率 | ✅ | 🟡（模型有速率字段，UI 不显示） | 🆕 已显示速率 |
| 断点续传 | ❌（官方也没有） | ❌ | — |
| 暂停/恢复 | ❌（官方也没有） | ❌ | — |
| 取消传输 | ✅ | ✅ | ✅ |
| 完整性校验 | ✅（可选） | ✅（强制 SHA-256） | ✅ 更强 |
| 同名文件策略 | 有交互提示 | ❌ 直接覆盖（`FileMode.Create`） | ❌ P0（数据丢失风险） |
| 大文件（>4GB） | ✅ | ✅（long + 64KB 块） | ✅ |
| 多文件并发流 | ✅（可并发 session） | ❌ 单连接串行 | ❌ P2 |
| 传输历史（发送侧） | ✅ | 🟡（只有接收历史） | ❌ P2 |

### 2.2 发现与连接

| 功能 | LocalSend | 本项目（本次前） | 现状 |
|---|---|---|---|
| UDP 组播发现 | ✅ | ✅ | ✅ |
| UDP 广播发现 | ✅ | ✅（还做了子网定向广播，更稳） | ✅ |
| mDNS 发现 | ✅（Bonjour） | ✅（自实现） | ✅ |
| HTTP 扫描兜底（跨子网/组播被拦） | ✅（legacy v1 scan） | ❌ | 🆕 网段扫描（自研报文，非 HTTP） |
| **手动输入 IP 连接** | 🟡（官方无独立入口，靠扫描） | ❌ | 🆕 已支持（IP / IP:端口 / IPv6 / 主机名） |
| 收藏 / 置顶设备 | ✅ | ❌ | 🆕 已支持（★ 收藏 + 置顶） |
| 设备离线标记 | ✅ | 🟡（直接移除，45s 过期） | 🟡 |
| 多网卡 / 网段选择 | ✅（网络白名单/黑名单） | 🟡（自动选最优网卡） | ❌ P2 |
| 自定义组播地址/端口 | ✅ | 🟡（`DiscoveryOptions` 可配，无 UI） | 🟡 |
| 关闭自动发现 | ✅ | ❌ | ❌ P2 |
| 发现超时配置 | ✅ | ❌ | ❌ P2 |

### 2.3 安全

| 功能 | LocalSend | 本项目 | 现状 |
|---|---|---|---|
| 传输加密 | ✅ HTTPS | ✅ 可选 TLS1.2（双方开启才升级） | ✅ |
| 指纹校验（防中间人） | ✅ | ✅ TOFU 指纹库 | 🟡 指纹变化时只拒绝，不提示用户 |
| 首用信任弹窗 | ✅ | ❌（静默信任） | ❌ P1 |
| PIN / 收件确认码 | ✅（Web Share） | ❌ | ❌ P2 |
| 发送方需接收方确认 | ✅ | ✅（60s 超时） | ✅ |
| 收藏设备免确认（Quick Save） | ✅ | ❌ | 🆕 已支持（按收藏 ID 自动接收） |
| 发现报文防伪造 | 🟡（无签名） | ❌（明文 JSON 前缀） | ❌ P2 |

### 2.4 接收体验

| 功能 | LocalSend | 本项目 | 现状 |
|---|---|---|---|
| 接收确认弹窗 | ✅ | 🟡（在任务列表内联，易被忽略） | 🟡 |
| 按文件类型自动归类保存 | ✅ | ✅（`FileTypeHelper`） | ✅ |
| 固定保存目录 | ✅ | ❌ | 🆕 已支持 |
| 接收完成打开文件 / 打开目录 | ✅ | ✅（历史页右键） | ✅ |
| 接收历史 | ✅ | ✅ | ✅ |
| 历史去重/筛选/删除 | ✅ | 🟡（可删单条/清空） | 🟡 |
| 系统通知 | ✅ | ❌ | ❌ P1 |
| 托盘常驻 / 最小化到托盘 | ✅ | ❌ | ❌ P2 |
| 开机自启 | ✅ | ❌ | ❌ P2 |

### 2.5 平台与生态

| 功能 | LocalSend | 本项目 | 现状 |
|---|---|---|---|
| Windows / Linux / macOS | ✅ | ✅（Avalonia） | ✅ |
| Android / iOS | ✅ | ✅（MAUI） | ✅ |
| **Web 分享（浏览器收发）** | ✅ | ❌ | ❌ P2 |
| 与官方 LocalSend 互通 | ✅ | ❌（协议不同） | ❌ 可选 |
| 多语言 | ✅（~50 种） | ❌（硬编码中文） | ❌ P1 |
| 主题切换 | ✅（浅色/深色/跟随系统） | 🟡（仅 Fluent，硬编码颜色） | 🆕 Win7/WinXP 皮肤切换 |
| CLI / headless | ✅（可选） | ❌ | ❌ P3 |
| 协议文档 | ✅ | 🟡（仅代码注释） | 🟡 |

### 2.6 工程化

| 项 | LocalSend | 本项目 | 现状 |
|---|---|---|---|
| 单元测试 | ✅ | 🟡（测试与实现脱节，编译失败） | 🆕 已修复已知失配并新增测试 |
| 集成测试（回环传输） | ✅ | ✅（Tcp/Tls 回环） | ✅ |
| CI | ✅ | ❌ | ❌ P2 |
| i18n 资源 | ✅ ARB/JSON | ❌ | ❌ P1 |
| AOT 发布 | ✅（Rust 部分） | ✅（`PublishAot` + `IsAotCompatible`） | ✅ |

---

## 3. 代码级缺陷清单（本次修复 / 仍待处理）

### 3.1 已修复

| # | 问题 | 位置 | 说明 |
|---|---|---|---|
| 1 | **发现服务 Stop 后无法再次发现** | `FileShare.Core/Network/CompositeDiscoveryService.cs` | 原 `Stop()` 把子服务的 `OnDeviceDiscovered/OnDeviceRemoved` 全部退订，之后再 `StartAsync` 也不会重新订阅 → 设备列表永久空白。改为 `Subscribe()/Unsubscribe()` 幂等管理，`StartAsync` 重新挂载。 |
| 2 | **后台线程直接改绑定属性** | `FileShare.Desktop/ViewModels/MainViewModel.cs` | 原 `OnTransferProgressUpdated` / `OnTransferCompleted` 在传输线程上直接 `viewModel.Status = ...`，Avalonia 绑定在非 UI 线程更新会抛异常/丢更新（原代码把 `_uiContext.Send` 注释掉了）。现在结构性变更用 `Send`、高频更新用 `Post`。 |
| 3 | **手动直连的设备不收历史** | `FileShare.Core/Services/FileShareServiceManager.cs` | 原判定 `info.ReceiverId == _localDevice.DeviceId`；手动直连时发送方拿不到真实 ID，历史永远不落库。改为按 `Direction == Receive && Completed` 判定。 |
| 4 | **测试项目引用已删除 API** | `FileShare.Core.Tests/**`、`FileShare.Desktop.Tests/**` | `TcpFileTransferService(dirSvc, port)`、`UdpMulticastDiscoveryService(device)`、`GetDiscoveredDevices()`、`SendDiscoveryPacketAsync()`、`MainWindowViewModel` 都不再存在 → 整个 solution `dotnet build` 失败。已补兼容构造函数并重写/更正测试。 |
| 5 | **收藏/设置无处落地** | 全局 | 原设备名硬编码 `Environment.MachineName`、TLS 硬编码 `true`、保存目录每次弹窗。现统一走 `IAppSettingsService`。 |

### 3.2 仍建议处理（按风险排序）

| # | 问题 | 影响 | 建议 |
|---|---|---|---|
| 1 | **同名文件直接覆盖** | 数据丢失 | 接收前检测重名，按 `(1)` 递增重命名；或在 UI 给出「覆盖/重命名/跳过」三选。`TcpFileTransferService.HandleFileData` 里建 `FileMode.Create` 处修改。 |
| 2 | 接收确认是"列表内联"而非模态 | 用户可能错过 60s 窗口导致发送失败 | 主窗口弹出模态确认（`IDialogService` 已有）；或加系统通知。 |
| 3 | `UdpMulticast/BroadcastDiscoveryService` 用 `async void` 处理报文 | 异常会直接打崩进程（未捕获的 async void） | 改 `async Task` + `_ = HandleAsync(...)` 包装 try/catch。 |
| 4 | 发现报文是明文前缀 + JSON，无条件信任 | 局域网内可伪造设备/诱导发送 | 给设备信息加签名（项目已有 Ed25519/RSA-PSS 令牌能力），或至少校验 `DeviceId` 与指纹一致。 |
| 5 | TLS 固定 TLS1.2 | 安全性/兼容性 | 允许 TLS1.3（客户端证书时序问题可用 `RemoteCertificate` 读取时机规避）。 |
| 6 | 传输线程 `ReadTimeout` 与 `CancellationToken` 混用 | 极端情况下 30s 卡顿 | 统一用 `CancellationTokenSource.CancelAfter`。 |
| 7 | 设备列表无"离线/在线"区分 | 发到已离线设备会失败 | 保留设备并置灰，发送前探活。 |
| 8 | 无 i18n | 无法国际化 | 引入 `.resx` 或 `Avalonia` 本地化，先抽 UI 文案常量。 |

---

## 4. 本次改动清单

### 4.1 新增（Core）

| 文件 | 作用 |
|---|---|
| `FileShare.Core/Network/Discovery/DeviceAddress.cs` | 解析 `IP`、`IP:端口`、`[IPv6]:端口`、主机名；容忍 `http://ip:port/path` 粘贴 |
| `FileShare.Core/Network/Discovery/DeviceProbeService.cs` | 单设备探测：UDP 发现报文（可拿到完整设备信息）→ 无应答退化 TCP 探活（可发送但无身份） |
| `FileShare.Core/Network/Discovery/SubnetScanner.cs` | CIDR 网段扫描：并发 64、跳过本机、逐地址进度、可取消、上限 4096 地址 |
| `FileShare.Core/Network/LocalNetworkHelper.cs` | 网卡/掩码枚举、本机地址判定、按网关优先推荐默认网段 |
| `FileShare.Core/Services/AppSettings.cs` | 设置模型（设备名/类型/端口/TLS/保存目录/自动接收/收藏/皮肤）+ 校验 + 收藏切换 |
| `FileShare.Core/Services/JsonAppSettingsService.cs` | 设置持久化（临时文件 + 原子替换，损坏自动回落默认值） |
| `FileShare.Core/Models/FileSendProgress.cs` | 批量发送进度 |

### 4.2 修改（Core）

| 文件 | 改动 |
|---|---|
| `CompositeDiscoveryService.cs` | 事件订阅幂等化（修 Stop→Start 丢失）；新增 `InjectDevice`（把直连/扫描结果并入设备表，复用去重与过期） |
| `FileShareServiceManager.cs` | 新增 `ConnectByAddressAsync` / `ScanSubnetAsync` / `SendFilesAsync`；修接收历史判定 |
| `IFileShareServiceManager.cs` | 上述接口声明 |
| `TcpFileTransferService.cs` | 补两个兼容构造函数（便于测试与快速集成） |
| `Common/SourceGenerationContext.cs` | 注册 `AppSettings`（AOT 下可序列化） |

### 4.3 新增/修改（Desktop）

| 文件 | 说明 |
|---|---|
| `Themes/Win7.axaml` | Windows 7 / Aero 皮肤（渐变按钮、1px 描边、Segoe UI、方角面板） |
| `Themes/WinXP.axaml` | Windows XP / Luna 皮肤（Tahoma、`#ECE9D8` 底色、深蓝默认按钮、3px 圆角） |
| `Services/ThemeService.cs` | 运行时替换 `Application.Styles` 中的皮肤 `StyleInclude` |
| `ViewModels/DeviceItemViewModel.cs` | 设备列表项（收藏、中文类型名、副标题） |
| `ViewModels/SettingsViewModel.cs` | 设置页（含皮肤即时切换） |
| `Views/SettingsView.axaml(.cs)` | 设置页视图 |
| `Views/MainView.axaml` | 重做：工具栏（刷新 / IP 直连 / 网段扫描）+ 设备列表（收藏）+ 传输任务 + 状态栏 |
| `Views/HistoryView.axaml` | 改用皮肤 `Classes`，去掉硬编码颜色 |
| `Views/MainWindow.axaml` | 去掉窗口级硬编码样式，统一由皮肤提供 |
| `App.axaml` | 加载默认皮肤；固定浅色变体（避免深色模式与经典皮肤冲突） |
| `App.axaml.cs` | 启动时应用已保存皮肤；注入设置/皮肤服务 |
| `Program.cs` | 注册 `IAppSettingsService`（`%APPDATA%\FileShare\settings.json`）与 `IThemeService`；设备名/类型/端口/TLS 取自设置 |
| `ViewModels/MainViewModel.cs` | 直连/扫描/批量发送/收藏/自动接收/设置入口 + UI 线程调度修复 |
| `ViewModels/FileTransferViewModel.cs` | 速率显示 |

### 4.4 测试

| 文件 | 说明 |
|---|---|
| `FileShare.Core.Tests/Network/DeviceAddressTests.cs` | 地址解析正/反例 |
| `FileShare.Core.Tests/Network/SubnetScannerTests.cs` | CIDR 解析、主机枚举、扫描（假探测器）、取消 |
| `FileShare.Core.Tests/Services/AppSettingsTests.cs` | 规范化、收藏、克隆隔离、JSON 往返、损坏回落 |
| `FileShare.Core.Tests/Network/UdpDiscoveryServiceTests.cs` | 按当前 API 重写 |
| `FileShare.Desktop.Tests/ViewModels/MainWindowViewModelTests.cs` | 适配 `MainViewModel` 新签名 |

---

## 5. 建议路线图

### P0（正确性/数据安全，建议尽快）

1. **同名文件不覆盖**（重命名或询问）。
2. **接收请求模态化**，并加「最小化到托盘 + 通知」。
3. `async void` 事件处理改为带捕获的 `async Task`。
4. 输入校验：文件名去掉路径分隔符/控制字符（`TcpFileTransferService.ValidateRequest` 已有长度校验，缺非法字符校验）。

### P1（补齐 LocalSend 的"常用手感"）

1. **文件夹发送**（递归打包协议：`FileDto` 带相对路径，接收端 `Directory.CreateDirectory`）。
2. **拖拽发送**（Avalonia `DragDrop`）。
3. **i18n**（先抽 `Strings.resx`，再补 en/zh）。
4. **首用信任弹窗**（TOFU 指纹首次记录时提示；变化时红色警告）。
5. **发送历史**（与接收历史共用表，加 `Direction` 列）。
6. 手机端接入手动 IP / 网段扫描（Core 已就绪，只需 MAUI 页面加两个输入框 + 同一套 ViewModel 命令）。

### P2（生态与进阶）

1. **LocalSend Protocol v2 互操作**：仓库里已有 `LocalSend.Core`（Http/Dto/V2/V3/WebRtc 移植），建议做成"协议适配层"：对外暴露 `ILocalSendClient`，发现到握手成功再决定走本项目 TCP 还是 LocalSend HTTP。这是唯一能让 FileShare 与官方客户端互传的路。
2. **Web Share**：本地内置一个极简 HTTP 服务（复用 `LocalSend.Core/Http/Server`），生成一次性链接 + PIN。
3. 文本/剪贴板发送（协议加 `TextRequest` 帧，或复用文件通道发 `.txt`）。
4. 多网卡白名单/黑名单、自定义组播地址、关闭自动发现。
5. 传输并发（多 session）+ 速率限制。

---

## 6. 如何验证本次改动

> 说明：本次改动是在**无法运行编译器的环境**中完成的，逻辑经过逐行核对，但仍请以实际构建为准。

```powershell
# 1) 构建（先 Core，再 Desktop；移动端需 MAUI workload）
dotnet build FileShare.Core/FileShare.Core.csproj -c Debug
dotnet build FileShare.Desktop/FileShare.Desktop.csproj -c Debug

# 2) 跑单元测试
dotnet test FileShare.Core.Tests/FileShare.Core.Tests.csproj

# 3) 手工验证「直连 / 扫描」
#    - 两台机器各开一个实例（或在同一机器上改端口开第二个）
#    - 主机 A：工具栏输入 主机B的IP（或 IP:5237）→ 连接 → 设备列表出现 B
#    - 主机 A：工具栏输入 192.168.x.0/24 → 扫描 → 进度条走完，列出网段内所有实例
#    - 选中 B → 发送文件（可多选）→ B 侧确认 → 双方任务列表出现多条记录
#    - 设置 → 切换 Win7 / WinXP → 界面即时变化，重启后保持
```

界面皮肤切换要点：

- 皮肤文件只放"外观"，视图只放"布局 + 语义化 `Classes`；
- 新增第三套皮肤 = 复制 `Win7.axaml` 改颜色/字体 + 在 `ThemeService.SkinRelativePaths` 与 `AppSkin` 各加一项。
- 详见 [UI-皮肤系统说明.md](./UI-皮肤系统说明.md)。

---

## 7. 一句话总结

> 本项目在「传输内核」上已经接近甚至局部超过 LocalSend（校验、限流、优雅关闭、TOFU、双端 UI），
> 真正缺的是**连接方式的兜底（直连/扫描 → 本次已补）**、**批量化与免确认的交互（多文件/收藏/Quick Save → 本次已补）**、
> **接收体验（通知/托盘/同名策略）**、**国际化**，以及**与官方 LocalSend 的协议互操作**。
> UI 方面本次已把「现代 Fluent 混搭」改造成**可切换的经典双皮肤（Win7 / WinXP）**，后续加皮肤只需新增样式文件。
