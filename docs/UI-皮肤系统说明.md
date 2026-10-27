# UI 皮肤系统说明（Win7 / XP 蓝 / XP 橄榄 一键切换）

## 1. 设计目标

1. **统一风格**：所有外观（颜色、渐变、字体、圆角、边框、hover/pressed）集中到皮肤文件；视图里不再出现硬编码色值，也不再各页面自带 `<UserControl.Styles>`。
2. **可切换**：运行期在 **Windows 7（Aero）**、**Windows XP 经典蓝（Luna Blue）**、**Windows XP 橄榄绿（Luna Olive）** 之间切换，立即生效并随设置持久化。
3. **可扩展**：新增皮肤 = 复制一个 `.axaml` + 在枚举/映射表各加一项，视图零改动。

## 2. 为什么把基础主题从 Fluent 换成 Simple

| 维度 | FluentTheme | SimpleTheme（现用） |
|---|---|---|
| 控件模板 | 大量内置圆角、阴影、Reveal 高亮、动画 | 极简模板，几乎只有边框与纯色 |
| 主题资源 | 几十个 `ButtonBackground*` / `ControlCornerRadius` 等键需要在皮肤里逐个覆盖 | 需要覆盖的键很少，直接给控件设属性即可 |
| 与经典皮肤的冲突 | 「胶囊按钮 + 圆角卡片」很难彻底压平，常出现半 Fluent 半 XP 的割裂 | 只要覆盖 `Button/TextBox/ListBox/ProgressBar` 几个样式就干净了 |
| 工作量 | 需要写大量 `ControlTheme` 覆盖 | 本次就是 300 行左右的样式 |

结论：**做经典皮肤，Simple 明显更友好**。代价是「系统原生感」稍弱（例如 ComboBox 下拉、MenuFlyout 仍偏扁平），但和 XP/Win7 的契合度反而更高。

## 3. 分层约定

```
Views/*.axaml            ← 只写布局 + 语义化 Classes（panel / card / toolbar / h1 / muted ...）
Themes/Win7.axaml        ← Win7 Aero：渐变按钮、1px 描边、Segoe UI、方角
Themes/WinXP.axaml       ← XP Luna Blue：蓝色渐变工具栏 + 白字、米色窗体、深蓝默认按钮、绿色进度条
Themes/WinXPOlive.axaml  ← XP Luna Olive：橄榄绿渐变、米色
Services/ThemeService    ← 运行期替换 Application.Styles 里的皮肤 StyleInclude
Core/Services/AppSettings.Skin ← 持久化用户选择
```

## 4. 运行机制

1. `App.axaml` 先加载 `SimpleTheme` + 默认皮肤 `Themes/Win7.axaml`。
2. `App.Initialize()`（窗口创建前）按设置调用 `IThemeService.Apply(settings.Skin)`。
3. `ThemeService.Apply`：从 `Application.Styles` 移除旧皮肤的 `StyleInclude`，再添加新皮肤的；样式挂载即生效，**无需重启**。
4. 设置页三个 `RadioButton` 双向绑定 `IsWin7Skin` / `IsWinXPSkin` / `IsWinXPOliveSkin`，点选即应用并写入设置文件。
5. 托盘开关在保存设置后立即生效（`App.TrayService.SetVisible(...)`）。

## 5. Classes 契约（三套皮肤都必须实现）

| Class | 用于 | Win7（Aero） | XP Blue（Luna） | XP Olive（Luna） |
|---|---|---|---|---|
| `Border.panel` | 内容分区 | 白底 / `#C6C6C6` 边 / 圆角 2 | 白底 / `#7F9DB9` 边 | 白底 / `#ACA899` 边 |
| `Border.card` | 列表卡片 | 白底 / `#DCDCDC` 边 | 白底 / `#C8D4E2` 边 | 白底 / `#D4D0C8` 边 |
| `Border.toolbar` | 顶部工具条 | 蓝白渐变 + 深色文字 | **Luna 蓝渐变（#4E86C8→#1F4A8C）+ 白字** | 米色渐变 + 深色文字 |
| `Border.statusbar` | 底部状态栏 | 浅蓝渐变 + 上边框 | 米色 + `#ACA899` 上边框 | 米色 + 上边框 |
| `Border.separator` | 竖分隔线 | `#DCDCDC` | `#9DB8D8` | `#ACA899` |
| `TextBlock.h1` | 分区标题 | Segoe UI 12 Bold | Tahoma 11 Bold | Tahoma 11 Bold |
| `TextBlock.muted` | 次要文字 | `#6D6D6D` | `#4A4A4A`（选中行内提亮） | `#4A4A4A` |
| `TextBlock.accent` | 强调（速率等） | `#2E7BD6` | `#1C4E9C` | `#55701F` |
| `TextBlock.danger` | 危险文字 | `#C42B1C` | `#B00000` | `#B00000` |
| `TextBlock.label` | 表单标签 | 垂直居中 + 右边距 | 同 | 同 |
| `TextBlock.iconGlyph` | Fluent 图标字体 | 16px | 15px（工具栏内自动转白） | 15px |
| `TextBlock.star` / `.starOff` | 收藏星标 | 金 / 灰 | 琥珀 / 灰 | 琥珀 / 灰 |
| `Button` | 全部按钮 | 渐变面 + 1px 边 + 圆角 2 | Luna 面 + 圆角 3 + 1px 边 | 米色面 + 圆角 3 |
| `Button.accent` | 主操作 | 蓝渐变 | **深蓝渐变 + 2px `#003C74` 边（XP 默认按钮）** | 橄榄渐变 + 深橄榄边 |
| `Button.icon` | 图标按钮 | 透明 + hover 浅蓝 | 透明 + hover 浅蓝；工具栏内 hover 半透明白 | 透明 + hover 浅绿 |
| `Button.danger` | 删除 `✕` | 透明底红字 | 同 | 同 |
| `TextBox` | 输入框 | 白底 / 圆角 2 | 白底 / 直角 / `#7F9DB9` 边 | 白底 / 直角 |
| `ListBoxItem` | 设备列表项 | hover `#EAF2FB` / 选中 `#CFE4F8` | hover `#E3EEFC` / 选中 **`#316AC5` + 白字** | hover `#EFF3DE` / 选中 `#7C9B48` |
| `ProgressBar` | 传输/扫描进度 | 高 10 / 蓝条 | 高 12 / **XP 绿条** | 高 12 / 绿条 |
| `ToolTip` | 悬停提示 | 淡黄底 | 淡黄底 | 淡黄底 |

## 6. 资源键（皮肤内部使用）

`FsWindowBrush` `FsSurfaceBrush` `FsCardBrush` `FsBorderBrush` `FsSoftBorderBrush`
`FsTextBrush` `FsMutedBrush` `FsAccentBrush` `FsAccentTextBrush` `FsDangerBrush`
`FsRowHoverBrush` `FsRowSelectedBrush` `FsButtonBorderBrush` `FsButtonBorderHoverBrush` `FsInputBorderBrush`
`FsToolbarBrush` `FsButtonFaceBrush` `FsButtonHoverBrush` `FsButtonPressedBrush`
`FsAccentButtonBrush` `FsAccentButtonHoverBrush` `FsFontFamily`

> ⚠️ 不要给 `TextBlock` 设全局 `Foreground`：样式优先级高于继承，会盖掉 accent 按钮里的白字。
> 正文颜色通过 `Window` / `UserControl` 的 `Foreground` 继承；工具栏里的白字由 `Border.toolbar TextBlock` 规则统一处理。

## 7. 新增一套皮肤（3 步）

1. 复制 `Themes/WinXP.axaml` → `Themes/Win10.axaml`，改调色板与字体（**Classes 一个都不能少**）。
2. `FileShare.Core/Services/AppSettings.cs` 的 `AppSkin` 末尾加枚举值（保持向后兼容）。
3. `FileShare.Desktop/Services/ThemeService.cs` 的 `SkinFiles` 字典加一行；`SettingsViewModel` 里照抄 `IsWinXPSkin` 加一个 `IsWin10Skin` 并在 `SettingsView.axaml` 加一个 `RadioButton`。

## 8. 验证方式

```powershell
# 依次以三套皮肤启动，自动翻三页后退出（退出码 0 = 渲染正常）
# 皮肤由 settings.json 的 Skin 字段决定：0=Win7, 1=XP Blue, 2=XP Olive
$env:FILESHARE_DATA_DIR = "$PWD\.smoke\skin1"   # 目录内放好 settings.json
.\FileShare.Desktop\bin\Debug\net10.0\FileShare.Desktop.exe --smoke
```

## 9. 已知限制

1. **弹出层仍用 Simple 模板**：`MenuFlyout`（历史页「更多」）、`ComboBox` 下拉、部分滚动条是扁平风格，与 XP 皮肤会有轻微不搭；要彻底统一需为这些控件写 `ControlTheme`。
2. **固定浅色**：`App.axaml` 设 `RequestedThemeVariant="Light"`，避免系统暗色模式与经典皮肤混搭；暗色应作为独立皮肤实现。
3. **字体依赖系统**：Win7 用 `Segoe UI`、XP 用 `Tahoma`，回退 `Microsoft YaHei`；Linux/macOS 上会回退到系统字体，观感不同。
4. **不自定义窗口边框**：标题栏保持系统原生，避免「圆角标题栏 + 方角内容」的割裂感。
5. **托盘图标**：`Assets/logo.ico` 的第一帧是 PNG，`TrayService` 按 ICO 目录取出该帧再交给 SkiaSharp（SkiaSharp 不解码 ICO 容器）。若更换图标，请保留「首帧为 PNG」这一约定，或自行调整 `AppIconLoader`。
