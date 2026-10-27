# FileShare vs LocalSend：功能对照与差距分析（第二轮更新）

> 对照对象：[localsend/localsend](https://github.com/localsend/localsend)（Flutter + Rust，LocalSend Protocol v2）
> 本项目：`FileShare.Core`（net10.0）+ `FileShare.Desktop`（Avalonia 11）+ `FileShare.Mobile`（.NET MAUI）
> 本次更新记录第二轮改动：P0 正确性修复 + P1 常用手感 + P2 进阶项 + 经典 XP 蓝色皮肤。

---

## 0. 结论摘要

| 层次 | 状态 |
|---|---|
| 传输内核（分块 + SHA-256 + 背压 + 限流 + 优雅关闭 + TLS/TOFU） | 已具备，局部强于 LocalSend 的教学实现 |
| 连接兜底（IP 直连 / 网段扫描） | ✅ 完成，**超出 LocalSend 现有交互**（官方无独立直连入口） |
| 批量与免确认（多文件 / 文件夹 / 文本 / 收藏 / Quick Save） | ✅ 完成 |
| 数据安全（同名文件不覆盖、`.part` 暂存、目录穿越防护） | ✅ 完成 |
| 接收体验（模态确认 / 托盘 / 首用信任提示 / 发送历史） | ✅ 完成（第三轮补上通知卡片） |
| UI 统一与切换（Win7 / XP 蓝 / XP 橄榄） | ✅ 完成，**基础主题改为 SimpleTheme** |
| 网页分享 + PIN（浏览器直接传文件） | ✅ 第三轮完成（自研最小 HTTP 服务，已联调） |
| 数据安全（同名不覆盖 / `.part` 暂存 / 目录穿越防护 / 磁盘空间预检） | ✅ 完成 |
| 发送队列（暂停 / 继续 / 取消）与限速 | ✅ 第三轮完成 |
| 国际化（i18n） | ❌ 未做（见路线图，需整轮字符串抽取） |
| 与官方 LocalSend 协议互通 | ❌ 未做（协议不同，属于独立工程） |

---

## 1. 现状架构

```
FileShare.Core
├─ Network
│  ├─ CompositeDiscoveryService              三路发现聚合（可单独开关），支持外部注入设备
│  ├─ Discovery/UdpBroadcastDiscoveryService 5236，子网定向广播 + 255.255.255.255
│  ├─ Discovery/UdpMulticastDiscoveryService 224.0.0.167:53317（地址/端口可配）
│  ├─ Mdns/MdnsService                       _fileshare._tcp.local（5353）
│  ├─ Discovery/DeviceProbeService           单设备探测（UDP 身份 + TCP 探活兜底）
│  ├─ Discovery/SubnetScanner                CIDR 网段扫描（并发 64 / 可取消 / 上限 4096）
│  ├─ TcpFileTransferService                 5237，长度前缀 JSON 帧 + 可选 TLS + .part 暂存
│  └─ LocalNetworkHelper                     网卡/掩码/CIDR 工具
├─ Common/FileNameHelper                     文件名清洗、相对路径校验、同名策略
├─ Crypto          RSA 自签证书、TOFU 指纹库（含首次信任/不一致事件）、令牌签名
├─ Services        FileShareServiceManager、SQLite（Dapper AOT）、AppSettings、AppPaths
└─ Models          DeviceInfo / FileTransferInfo / 状态机 / 发送进度 / 同名策略
```

传输协议（自定义，与 LocalSend v2 不同）：

```
4 字节长度 + TransferRequest { Type, TransferId, FileName, FileSize, SenderId, ReceiverId, RelativePath?, Checksum? }
4 字节长度 + TransferResponse { TransferId, Accepted, Message }
流程：SendFileRequest →（用户确认）→ SendFileData(64KB 块) → 接收端算 SHA-256
      → FileChecksum → 接收端**先落盘（.part→正式名）再回复 Accepted** → Completed
```

> `RelativePath` 是新增字段：旧客户端不认识会忽略（退化为平铺保存），属于向后兼容的协议扩展。

---

## 2. 功能对照（✅ 有 / 🟡 部分 / ❌ 无 / 🆕 本轮新增）

### 2.1 传输

| 功能 | LocalSend | 本项目 |
|---|---|---|
| 单文件收发 | ✅ | ✅ |
| 多文件一次发送 | ✅ | 🆕 支持（可设置 1~4 并发） |
| 文件夹（保留目录结构） | ✅ | 🆕 支持（`RelativePath` + 自动建目录） |
| 文本 / 剪贴板 | ✅ | 🆕 文本发送（对端收到 `.txt`） |
| 拖拽发送 | ✅ | 🆕 支持（文件与文件夹均可） |
| 进度 + 速率 | ✅ | ✅ 卡片显示瞬时速率 |
| 完整性校验 | ✅（可选） | ✅（强制 SHA-256） |
| 同名文件 | 有提示 | 🆕 默认自动重命名，可在设置改为覆盖 |
| 半成品文件 | — | 🆕 `.part` 暂存，校验通过才改正式名 |
| 断点续传 / 暂停 | ❌ | ❌（与官方一致） |

### 2.2 发现与连接

| 功能 | LocalSend | 本项目 |
|---|---|---|
| 组播 / 广播 / mDNS | ✅ | ✅（三路可单独开关，地址端口可配） |
| HTTP 扫描兜底 | ✅（legacy） | 🆕 自研网段扫描（UDP 身份探测，带进度/取消） |
| 手动输入 IP 直连 | 🟡 | 🆕 支持 `ip` / `ip:port` / `[ipv6]:port` / 主机名 |
| 收藏设备 | ✅ | 🆕 ★ 收藏 + 置顶 |
| 收藏设备免确认 | ✅（Quick Save） | 🆕 支持 |

### 2.3 安全

| 功能 | LocalSend | 本项目 |
|---|---|---|
| 传输加密 | ✅ HTTPS | ✅ 可选 TLS（双方开启才升级）+ 双向证书 |
| 指纹校验 | ✅ | ✅ TOFU；🆕 首次信任/不一致事件 → UI 提示 + 设置页管理 |
| 目录穿越防护 | ✅ | 🆕 文件名清洗 + 相对路径校验 + 绝对路径拒绝 |
| PIN / Web Share | ✅ | ❌ |

### 2.4 接收与桌面集成

| 功能 | LocalSend | 本项目 |
|---|---|---|
| 接收确认 | ✅ 弹窗 | 🆕 模态窗口（含「以后自动接收」），任务列表内联按钮保留 |
| 系统托盘 | ✅ | 🆕 托盘图标 + 菜单（显示/隐藏/退出）+ 最小化/关闭到托盘 |
| 系统通知 | ✅ | ❌（托盘环境下用窗口激活 + 模态代替） |
| 接收历史 | ✅ | ✅ |
| 发送历史 | ✅ | 🆕 收发同表（`Direction`），历史页可按全部/接收/发送筛选 |
| 按类型归档 / 固定目录 | ✅ | ✅ |

### 2.5 外观

| 功能 | LocalSend | 本项目 |
|---|---|---|
| 多种风格切换 | ✅ 浅/深色 | 🆕 Win7(Aero) / XP(Luna Blue) / XP(Luna Olive)，**即切即生效** |
| 多语言 | ✅ ~50 种 | ❌ 中文单语（i18n 未做） |

---

## 3. 本轮修复的代码级缺陷

| # | 问题 | 影响 | 处理 |
|---|---|---|---|
| 1 | 接收端**先回复「校验通过」再落盘** | 发送方显示成功但文件可能没写成功（磁盘满/占用） | 改为**先提交文件再回复 Accepted**；提交失败回 Accepted=false |
| 2 | `RelativePath` 被当成目录再拼一次文件名 | 保存成 `相册/2024/a.jpg/a.jpg` | 拆成「目录 + 文件名」，文件名以相对路径为准 |
| 3 | 同名文件 `FileMode.Create` 静默覆盖 | 数据丢失 | 默认自动重命名；可选覆盖；`.part` 暂存避免半成品 |
| 4 | 接收文件名未清洗 | 对端可构造 `../` 写到任意位置 | `FileNameHelper` 清洗 + 请求校验拒绝分隔符/`..`/绝对路径 |
| 5 | 发现服务 `async void HandleMessage` | 报文处理异常直接打崩进程 | 改为 `async Task` + `SafeHandleMessageAsync` 收敛异常 |
| 6 | 自动发现方式无法关闭 | 特殊网络无法规避端口冲突 | 三个开关 + 地址/端口可配（`DiscoveryOptions`） |
| 7 | 设置无法持久化（设备名/端口/加密硬编码） | 改配置要改代码 | `AppSettings` + JSON 原子写入 + 失败回落默认 |
| 8 | 数据目录固定 `%APPDATA%` | 便携使用/多实例/自动化测试不便 | 新增 `FILESHARE_DATA_DIR` 环境变量（便携模式） |
| 9 | 延迟落盘导致 `SendAsync` 返回后文件尚未就绪 | 自动化测试与调用方误判 | 由第 1 条一并解决（提交在应答之前） |

---

## 4. 本轮改动清单

### Core 新增

| 文件 | 作用 |
|---|---|
| `Common/FileNameHelper.cs` | 文件名清洗、相对路径规范化与校验、同名策略、目录包含判断 |
| `Models/FileNameConflictPolicy.cs` | 同名策略枚举（Rename/Overwrite） |
| `Models/FileSendProgress.cs` | 批量发送进度 |
| `Services/AppPaths.cs` | 统一数据目录解析（支持 `FILESHARE_DATA_DIR` 便携模式） |
| `Discovery/DeviceProbeService.cs` / `DeviceAddress.cs` / `SubnetScanner.cs` | 直连与网段扫描（上一轮新增，本轮接入设置与 UI） |

### Core 修改

| 文件 | 改动 |
|---|---|
| `TcpFileTransferService.cs` | `.part` 暂存与提交、同名策略、文件名清洗、`RelativePath`、`fileNameOverride`、历史用实际落盘名、`FingerprintStore` 暴露 |
| `FileShareServiceManager.cs` | 冲突策略注入、`SendFolderAsync`、`SendTextAsync`、并发发送、收发历史、TOFU 事件转发、信任设备管理 |
| `Crypto/FingerprintStore.cs` | `FirstUseTrusted` / `FingerprintMismatch` 事件、`GetTrustedDevices` / `Forget` / `Clear` |
| `Services/DatabaseService.cs` | 历史表新增 `Direction`/`PeerId`/`PeerName`（幂等 `ALTER TABLE` 迁移） |
| `Network/Discovery/DiscoveryOptions.cs` | 三路发现开关 + 地址/端口 + `FromSettings` |
| `Network/CompositeDiscoveryService.cs` | 子服务可空、生命周期安全、`InjectDevice` |
| `Crypto/CryptoManager.cs` | 证书目录走 `AppPaths`（便携/测试隔离） |

### Desktop 新增

| 文件 | 作用 |
|---|---|
| `Themes/WinXP.axaml`（重写） | **经典 Luna Blue**：蓝色渐变工具栏 + 白字、米色窗体、深蓝默认按钮、深蓝选中行、绿色进度条 |
| `Themes/WinXPOlive.axaml` | Luna Olive：橄榄绿渐变 + 米色 |
| `Services/TrayService.cs` | 托盘图标（从 ICO 提取 PNG 帧）+ 菜单 + 显示/隐藏/退出 |
| `Views/ReceiveRequestWindow.axaml(.cs)` | 接收请求模态窗（含「以后自动接收」） |
| `Views/SendTextWindow.axaml(.cs)` | 文本编辑发送窗 |

### Desktop 修改

| 文件 | 改动 |
|---|---|
| `App.axaml` | 基础主题 **FluentTheme → SimpleTheme**；默认皮肤 Win7 |
| `Services/ThemeService.cs` | 三皮肤映射表 + 运行时替换 |
| `ViewModels/MainViewModel.cs` | 发送文件夹/文本、拖拽入口、接收模态、TOFU 提示、并发数、信任事件解绑 |
| `ViewModels/SettingsViewModel.cs` | 同名策略、托盘、发现方式、组播地址/端口、并发、信任设备、三皮肤单选 |
| `ViewModels/HistoryViewModel.cs` | 收发历史 + 筛选 |
| `Views/MainView.axaml(.cs)` | 拖拽处理 + 新增按钮 |
| `Views/HistoryView.axaml` | 筛选按钮 + 方向/对端显示 |
| `Views/SettingsView.axaml` | 新增全部设置分区 |
| `Views/MainWindow.axaml.cs` | 托盘挂载、最小化/关闭到托盘 |
| `App.axaml.cs` | 皮肤应用、托盘创建、`--smoke` 自检入口 |
| `Program.cs` | 设置注入（设备名/端口/TLS/冲突策略/发现配置）、数据目录走 `AppPaths` |

### Mobile

| 文件 | 改动 |
|---|---|
| `MainPage.xaml` | 新增「IP 直连」「网段扫描」输入与按钮 + 扫描进度条 |
| `ViewModels/MainPageViewModel.cs` | `ConnectByAddressCommand` / `ScanSubnetCommand` / 进度与状态 |

### 测试（Core.Tests 91 → 123 个用例）

| 文件 | 说明 |
|---|---|
| `Common/FileNameHelperTests.cs` | 清洗、截断、相对路径、同名策略 |
| `Network/ReceivePathPolicyTests.cs` | 端到端：不覆盖、覆盖、目录结构、`../` 拒绝 |
| `Network/TlsHandshakeDiagnosticTests.cs` | 改为验证当前 `CryptoManager` 实现（环境不支持时 Skip） |
| `Network/TlsFileTransferIntegrationTests.cs` | 改为当前 API；无证书环境自动 Skip |
| `Services/AppSettingsTests.cs` | 新字段规范化 / 克隆 / JSON 往返 |

---

## 5. 验证结果（本机实测）

| 项 | 结果 |
|---|---|
| `dotnet build FileShare.Core` | ✅ 0 警告 0 错误 |
| `dotnet build FileShare.Desktop -f net10.0` | ✅ 0 警告 0 错误 |
| `dotnet build FileShare.Mobile -f net10.0-windows10.0.26100.0` | ✅ 成功 |
| `FileShare.Core.Tests`（in-process runner） | ✅ 123 通过 / 0 失败 / 3 跳过（沙箱无法写私钥容器） |
| `FileShare.Desktop.Tests` | ✅ 16 通过 |
| UI 冒烟（`FileShare.Desktop.exe --smoke`，三套皮肤各跑一次） | ✅ 主界面/历史/设置三页均渲染成功，日志无 ERR/WRN |

> `--smoke` 是本次加入的自检开关：启动后以 1.2s 间隔依次切到历史页、设置页、再回主界面，然后以 0 退出。
> 没有它就无法在无人值守环境里验证 XAML/皮肤是否真的能加载。

---

## 6. 剩余路线图

### P0（建议尽快，仍属数据/体验风险）

1. **系统通知**（Windows Toast / Linux libnotify）：托盘隐藏时提示「收到文件请求」。
2. **磁盘空间预检**：接收前检查剩余空间，避免写一半失败。
3. **失败历史记录**（目前只记成功）。

### P1

1. **i18n**：把 Desktop 全部 UI 文案抽到 `Strings.zh-CN.resx` / `Strings.en-US.resx` + `TrExtension` 标记扩展，设置页提供语言切换（重启生效）。这是目前与 LocalSend 差得最明显的一块。
2. **网络接口白名单/黑名单**（多网卡机器只在指定网段广播）。
3. **发送队列 UI**（可暂停/重排，LocalSend 有）。
4. Android/iOS 端的保存目录选择与「发送文件夹」。

### P2

1. **LocalSend Protocol v2 互操作**：仓库已有 `LocalSend.Core`（Http/V2/V3/WebRtc 移植），建议做成协议适配层（探测到对方是 LocalSend → 走 HTTPS REST；否则走本项目 TCP）。
2. **Web Share + PIN**：内置极简 HTTP 服务（可复用 `LocalSend.Core/Http/Server`），浏览器输 PIN 收发。
3. 传输限速、断点续传（协议需 chunk 偏移字段）。

---

## 7. 使用与配置

### 便携模式 / 多实例

```powershell
# 数据（settings.json / fileshare.db / logs / crypto）全部放到指定目录
$env:FILESHARE_DATA_DIR = "D:\FileShareData"
.\FileShare.Desktop.exe
```

同一台机器要开两个实例互相测试时，给两个实例不同的 `FILESHARE_DATA_DIR` + 不同的传输端口即可。

### 主要设置项（设置页 → 保存）

| 分组 | 项 | 是否需重启 |
|---|---|---|
| 设备 | 设备名称 / 类型 / 传输端口 | 是 |
| 接收 | 保存目录 / 自动接收收藏设备 / 同名覆盖 | 同名策略：是 |
| 安全与发现 | TLS / mDNS / 组播 / 广播 / 组播地址与端口 | 是 |
| 发送 | 同时发送数（1~4） | 否 |
| 窗口与托盘 | 最小化到托盘 / 关闭到托盘 | 否（即生效） |
| 界面风格 | Win7 / XP 蓝 / XP 橄榄 | 否（即生效） |

### UI 自检

```powershell
.\FileShare.Desktop.exe --smoke   # 自动翻三页后退出，退出码 0 表示渲染正常
```

---

# 第三轮更新（本次）

## 8. 本次修复的 Bug

| # | 现象 | 根因 | 修复 |
|---|---|---|---|
| 1 | 历史里的**发送记录**无法「打开文件 / 在资源管理器中显示」 | 历史表只在接收时写入 `SavePath`，发送侧没有记录源目录（`SavePath + FileName` 拼出来的是接收端语义） | 历史表新增 **`FilePath`（本机绝对路径）**：接收=落盘位置，发送=源文件位置；历史页优先用它，旧记录回退 `SavePath+FileName`；没有文件时给出明确提示 |
| 2 | 在接收弹窗勾选「以后自动接收」后，设备列表的 ★ 不刷新 | 只写了设置，没有通知列表项 | 订阅 `IAppSettingsService.Changed`，统一刷新所有设备项的收藏状态并置顶（用 `Move` 保留选中项），弹窗勾选即时同步 |

> 用户自行修复的「选择设备后发送按钮不更新」已保留（`MainViewModel.SelectedItem` 手动触发 `SelectedDevice` / `IsDeviceSelected` 通知）。

## 9. 本次新增的功能

### 9.1 历史与安全

- **历史记录状态**：新增 `Status` / `ErrorMessage`，成功、失败、取消都会入库；历史卡片显示「接收/发送 + 成功/失败」并染色，失败记录不再丢失。
- **磁盘空间预检**：接收前检查目标盘可用空间（预留 64MB），不足直接回绝，避免写一半才失败。
- **可打开判定**：`HasFile` 控制「打开文件 / 定位」菜单是否可用，避免点了没反应。

### 9.2 通知

- **通知卡片**（`ToastWindow` + `IToastService`）：右下角自绘卡片，6 秒自动消失，点击回到主窗口；
  - 收到文件请求时提示；
  - 主窗口不在前台时，传输完成也提示；
  - 网页分享收到文件也提示。
  - 不使用平台 Toast API（各系统差异大、部分环境不可用），跨平台行为一致且继承当前皮肤配色。

### 9.3 发送队列与限速

- **暂停 / 继续 / 取消**（`TransferQueueControl`）：批量发送与文件夹发送时可暂停（当前文件传完后停在下一个文件之前），可取消整批；界面在发送期间显示这三个按钮。
- **上传限速**：设置里的 `上传限速 (KB/s)`，0 表示不限速；发送循环里按「已发送字节 / 目标速率」计算应耗时并等待差额（单次最多等 1 秒，保证取消及时）。

### 9.4 网页分享 + PIN（`FileShare.Core/Web/WebShareService`）

- 手机/平板浏览器打开 `http://电脑IP:端口/`，输入 6 位 PIN，即可把文件上传到电脑（无需安装 App）。
- 实现：`TcpListener` 上的最小 HTTP/1.1 子集（GET /、POST /upload?name=xxx），**上传体是原始字节**，因此不需要 multipart 解析；
- 安全：PIN 校验（`X-Pin` 头或 `?pin=`）、文件名清洗与同名重命名（复用 `FileNameHelper`）、拒绝目录穿越、单文件流式写 `.part` 后提交；
- 设置页：启用开关、端口（默认 8088）、PIN 显示/重新生成、一键打开；收到的文件存到「保存目录」（留空则下载目录）。
- 已用真实 HTTP 请求联调：页面 200、错误 PIN 401、正确上传落盘且内容一致。

### 9.5 数据目录

- `FILESHARE_DATA_DIR` 环境变量（便携模式 / 多实例 / 自动化测试隔离），设置、数据库、日志、证书全部跟随。

## 10. 本次验证

| 项 | 结果 |
|---|---|
| `FileShare.Core` / `FileShare.Desktop` / `FileShare.Core.Tests` / `FileShare.Desktop.Tests` 构建 | ✅ 0 警告 0 错误 |
| Core 单元/集成测试 | ✅ **125 通过** / 0 失败 / 3 跳过（沙箱无法写私钥容器） |
| Desktop 单元测试 | ✅ 16 通过 |
| 网页分享联调（真实 HTTP） | ✅ 页面 200、错误 PIN 401、上传落盘内容一致 |
| UI 冒烟（三套皮肤 × 三页） | ✅ 无 ERR/WRN |
| 端口被占用时的提示 | ✅ 从「启动服务失败: SocketException」改为「端口被占用（5237），请关闭另一个实例或换端口」 |

> 注意：本次验证时本机有一个旧的调试实例占用了 5237，所以冒烟测试用了 5247。正常使用时如遇相同报错，按提示换端口或关掉旧实例即可。

## 11. 第四轮（本次）改动

### 11.1 按你的建议做的重构

| 项 | 说明 |
|---|---|
| **皮肤配置移出 Core** | `AppSkin` 与"当前皮肤"不再属于 `AppSettings`，改到 Desktop：`Services/UiPreferences.cs`（`AppSkin` + `DesktopUiPreferences` + `JsonUiPreferencesService`），持久化到数据目录的 `desktop-ui.json`。移动端不会被动到任何 Win7/XP 概念；旧 `settings.json` 里的 `Skin` 字段会被自动忽略 |
| **托盘注入 MainViewModel** | `MainViewModel` 构造函数新增可选 `ITrayService`；`ActivateMainWindow()` 优先走 `TrayService.ShowMainWindow()`（隐藏到托盘时也能正确恢复）；`App` 中把托盘创建提前到 ViewModel 之前 |
| **本地 IP 选择** | 保留你加的"有网关优先"排序；额外叠加网段白/黑名单过滤（配置了白名单时，只从允许的网卡里选本机 IP） |

### 11.2 本次补完的功能

| 功能 | 说明 |
|---|---|
| **网段白名单 / 黑名单** | `Core/Network/NetworkFilter.cs`：支持 `192.168.1.0/24`、单 IP、`192.168.1.` 前缀写法；广播只向允许网段的定向广播地址发送（配置白名单时不再发 255.255.255.255），组播只在允许网卡上 `JoinMulticastGroup`，本机 IP 选择同样遵守规则；设置页「允许网段 / 排除网段」两个输入框 |
| **发送队列可重排** | 左侧选择文件/文件夹或拖拽后**进入队列**（文件夹按文件展开并保留相对路径）；队列面板显示名称+大小，支持 ↑ ↓ 调整顺序、✕ 移除、清空；发送按"当前队列顺序"逐项进行 —— 暂停后调整顺序**立即对剩余项目生效**；取消后剩余项目保留在队列中 |
| **队列与并发的关系** | 队列逐项发送（保证顺序与重排语义），并发数设置对"一次多选但未入队"的场景仍有效；`SendSingleAsync` 对外暴露，便于以后做更复杂的调度 |

### 11.3 验证（本次）

| 项 | 结果 |
|---|---|
| Core / Desktop / Core.Tests / Desktop.Tests 构建 | ✅ 0 警告 0 错误 |
| Core 测试 | ✅ 125 通过 / 0 失败 / 3 跳过 |
| Desktop 测试 | ✅ 16 通过 |
| UI 冒烟（三皮肤） | ✅ 通过 `desktop-ui.json` 切换皮肤，三页渲染正常、无 ERR |

## 12. 第五轮（本次）：i18n + 断点续传

### 12.1 i18n（中 / 英）

采用「**以中文原文为 key**」的方案（比自造 key + resx 少一半工作量，且永不出现"界面显示 key"）：

| 文件 | 作用 |
|---|---|
| `FileShare.Desktop/Localization/Loc.cs` | `AppLanguage`、`Loc.Get/ F`、`TrExtension` 标记扩展；找不到译文自动回退中文 |
| `FileShare.Desktop/Localization/EnglishTranslations.cs` | 237 条「中 → 英」映射 |
| `Services/UiPreferences.cs` | 语言与皮肤一样，属于**桌面端私有配置**，存在 `desktop-ui.json` |
| `SettingsView` | 「界面风格」里新增「界面语言」下拉（简体中文 / English），保存后**重启生效** |

落地范围：6 个视图的静态文案（105 处 `{loc:Tr '原文'}`）+ 3 个 ViewModel 的状态/对话框文案（169 处 `Loc.Get/Loc.F`，含插值模板）。日志文案保持中文（不影响界面）。

### 12.2 断点续传（协议扩展，向后兼容）

| 环节 | 实现 |
|---|---|
| 协议 | `TransferRequest.StartOffset`（发送端声明起点）、`TransferResponse.ResumeOffset`（接收端已有字节数）；旧端忽略新字段，退化为从头传 |
| 接收端 | `HandleSendFileRequest` 阶段就解析落盘路径（`ResolveTargetPaths`，与数据阶段共用同一份计划），发现未完成的 `.part` 且 `0 < 长度 < 文件大小` 时返回 `ResumeOffset`；`HandleFileData` 以 **Append** 打开 `.part`，先对已有前缀重算 SHA-256，再继续接收 |
| 发送端 | 收到 `ResumeOffset > 0` 后 seek 到该偏移；生产者先只读前缀做哈希（校验和仍覆盖整个文件），再从偏移发送 |
| 中断保留 | 传输中断/取消时**不再删除** `.part`，这样"重试"可直接续传；校验失败或超时才清理 |
| 重试入口 | 失败任务卡片新增「重试」按钮（`FileTransferViewModel.CanRetry` + `RetryTransferCommand`），点击后用同一目标设备重发，接收端自动续传 |

测试（`Core.Tests/Network/ResumeTransferTests`）：
- 预置 128KB `.part` → 发送 256KB → 文件内容一致、`.part` 被清理、接收端总接收量 < 文件大小（证明没有重传已收部分）
- 无 `.part` 时行为与之前一致

### 12.3 验证

| 项 | 结果 |
|---|---|
| Core / Desktop / 两个测试项目构建 | ✅ 0 警告 0 错误 |
| Core 测试 | ✅ **127 通过**（含 2 个续传用例）/ 0 失败 / 3 跳过 |
| Desktop 测试 | ✅ 16 通过 |
| UI 冒烟（中文+Win7 / 英文+XP 蓝 / 中文+XP 橄榄） | ✅ 三套组合均正常渲染、无 ERR |

## 13. 仍未完成

| 项 | 现状与下一步 |
|---|---|
| **与官方 LocalSend 互通** | 未实现。已核实可直接复用 `LocalSend.Core`：①`LocalSendServer(port:53317, tlsConfig:null, info:ClientInfo{Alias,Version,DeviceType,Token}, v2Config:new ServerConfigV2())` + `StartAsync()` 作为接收端；②订阅 `server.State.V2.EventTx`，处理 `ServerEventV2.PrepareUpload`（`DecisionTx.TrySetResult(new PrepareUploadDecisionV2.Accept(ids))`）与 `ServerEventV2.FileUpload`（给 `FileUploadTarget.PathTarget(path, tcs)`）；③`MulticastDiscoveryService(localInfo, httpPort, ProtocolTypeV2.Http, group, port, serverRunning:true, httpClient)` + `StartListeningAsync/SendAnnouncementAsync` 让官方客户端能发现本机；④发送侧用 `LsHttpClientV3/V2` 的 `PrepareUploadAsync/UploadAsync`。需要真实官方客户端联调（接收确认、PIN、mTLS 指纹）。 |
| **移动端保存目录 / 发送文件夹** | 未实现，需要 MAUI `FolderPicker` + Android SAF 授权，只能真机验证 |

> 以上两项在代码结构上已留好位置（桌面端私有 UI 配置、Core 的 Web/网络过滤分层、队列逐项发送），接入时不会牵动现有逻辑。