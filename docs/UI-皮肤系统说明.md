# UI 皮肤系统说明（Windows 7 / Windows XP 一键切换）

## 1. 设计目标

1. **统一风格**：同一个界面上不再混用「Fluent 圆角卡片 + 硬编码 `#0078D4` + 各页面自带 `<UserControl.Styles>`」。所有外观集中到皮肤文件。
2. **可切换**：运行期在 Windows 7（Aero）与 Windows XP（Luna）之间切换，立即生效并持久化。
3. **可扩展**：新增第三套皮肤（例如 Win10 经典、macOS 银灰）只需新增一个 `.axaml` + 枚举值，视图零改动。

## 2. 分层约定

```
Views/*.axaml          ← 只写布局 + 语义化 Classes（panel / card / toolbar / h1 / muted ...）
Themes/Win7.axaml      ← 只写外观（颜色、渐变、字体、圆角、边框、hover/pressed）
Themes/WinXP.axaml     ← 与 Win7 的 Classes 集合必须完全一致
Services/ThemeService  ← 运行期替换 Application.Styles 中的皮肤 StyleInclude
Core/Services/AppSettings.Skin ← 持久化用户选择
```

**为什么这样分**：两套皮肤提供相同的 `Classes` 契约，因此布局在切换时不会位移；样式优先级冲突也被限制在皮肤文件内。

## 3. 运行机制

1. `App.axaml` 里默认引入 `Themes/Win7.axaml`：

```xml
<Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://FileShare.Desktop/Themes/Win7.axaml" />
</Application.Styles>
```

2. `App.Initialize()`（创建窗口之前）读取设置并调用 `IThemeService.Apply(settings.Skin)`：

```csharp
var settingsService = ServiceProvider?.GetService<IAppSettingsService>();
var themeService    = ServiceProvider?.GetService<IThemeService>();
themeService?.Apply(settingsService?.Current.Skin ?? AppSkin.Win7);
```

3. `ThemeService.Apply` 的做法：从 `Application.Styles` 中移除旧皮肤的 `StyleInclude`，再 `Add(new StyleInclude(new Uri(...)))`。样式会在挂载时加载并立即重新求值，所以**无需重启**。

4. 设置页用两个 `RadioButton` 双向绑定 `IsWin7Skin` / `IsWinXPSkin`，点选即调用 `Apply` 并写入设置文件。

## 4. Classes 契约（两套皮肤都必须实现）

| Class | 用于 | Win7（Aero） | WinXP（Luna） |
|---|---|---|---|
| `Border.panel` | 内容分区容器 | 白底、`#C6C6C6` 1px 边、圆角 2 | 白底、`#ACA899` 1px 边、圆角 3 |
| `Border.card` | 列表项卡片（设备/任务/历史） | 白底、`#DCDCDC` 边 | 白底、`#D4D0C8` 边 |
| `Border.toolbar` | 顶部工具条 | 蓝白渐变 `#FDFEFF→#E6EEF9`，仅下边框 | 米色渐变 `#FAF9F5→#E3E0D3` |
| `Border.statusbar` | 底部状态栏 | 同工具栏渐变，仅上边框 | 同上 |
| `Border.separator` | 竖直分隔线 | `#DCDCDC` | `#ACA899` |
| `TextBlock.h1` | 分区标题 | Segoe UI 12 Bold | Tahoma 11 Bold |
| `TextBlock.muted` | 次要文字（11px 灰） | `#6D6D6D` | `#4A4A4A`（选中行内提亮） |
| `TextBlock.accent` | 强调文字（速率等） | `#2E7BD6` | `#316AC5` |
| `TextBlock.danger` | 危险文字 | `#C42B1C` | `#B00000` |
| `TextBlock.label` | 表单左侧标签 | 垂直居中 + 右边距 | 同 |
| `TextBlock.iconGlyph` | Fluent 图标字体 | 16px | 15px |
| `TextBlock.star` / `.starOff` | 收藏星标 | 金色 / 灰 | 琥珀色 / 灰 |
| `Button`（默认） | 所有按钮 | 渐变面 `#FCFCFD→#E4E4E4`、1px 边、圆角 2、最小高 23 | `#FFFFFF→#E3DFD2`、圆角 3、最小高 22 |
| `Button.accent` | 主操作（发送/接受/保存） | 蓝色渐变 + 白字 | 深蓝渐变 + 2px `#003C74` 边（XP 默认按钮观感） |
| `Button.icon` | 图标按钮 | 透明底、hover 浅蓝 | 透明底、hover 浅蓝 |
| `Button.danger` | 移除任务 `✕` | 透明底红字、hover 淡红 | 同 |
| `TextBox` | 所有输入框 | 白底 `#9AA5B1` 边、圆角 2 | 白底 `#7F9DB9` 边、直角 |
| `ListBox` / `ListBoxItem` | 设备列表 | hover `#EAF2FB`、选中 `#CFE4F8` | hover `#E8F0FE`、选中深蓝 `#316AC5` + 白字 |
| `ProgressBar` | 传输/扫描进度 | 高 10、`#E1E1E1` 槽、蓝条 | 高 12、白槽、绿条（XP 风格） |
| `ToolTip` | 悬停提示 | 淡黄底 `#FFFFE1` + 灰边 | 同 |

## 5. 资源键（皮肤内部使用）

每套皮肤在 `<Styles.Resources>` 内定义同名键，值随皮肤变化：

`FsWindowBrush` `FsSurfaceBrush` `FsCardBrush` `FsBorderBrush` `FsSoftBorderBrush`
`FsTextBrush` `FsMutedBrush` `FsAccentBrush` `FsAccentTextBrush` `FsDangerBrush`
`FsRowHoverBrush` `FsRowSelectedBrush` `FsButtonBorderBrush` `FsButtonBorderHoverBrush` `FsInputBorderBrush`
`FsToolbarBrush` `FsButtonFaceBrush` `FsButtonHoverBrush` `FsButtonPressedBrush`
`FsAccentButtonBrush` `FsAccentButtonHoverBrush` `FsFontFamily`

> 注意：**不要**给 `TextBlock` 设全局 `Foreground`。样式优先级高于继承，会盖掉 `accent` 按钮里的白字。
> 普通正文的颜色通过 `Window` / `UserControl` 的 `Foreground` 继承下去。

## 6. 新增一套皮肤（3 步）

1. 复制 `Themes/Win7.axaml` → `Themes/Win10.axaml`，改调色板与字体（**Classes 一个都不能少**）。
2. `FileShare.Core/Services/AppSettings.cs` 的 `AppSkin` 加枚举值（注意保持向后兼容，新值加在末尾）。
3. `FileShare.Desktop/Services/ThemeService.cs` 的 `SkinRelativePaths` 加 `"Themes/Win10.axaml"`，并在 `Apply` 的路径选择处加分支；`SettingsView.axaml` 加一个 `RadioButton` 绑到新的 `IsWin10Skin` 属性（`SettingsViewModel` 里照抄 `IsWin7Skin` 的实现）。

## 7. 已知限制

1. **弹出层仍用 Fluent 模板**：`MenuFlyout`（历史页"更多"菜单）、`ComboBox` 下拉、`ToolTip` 圆角等由 FluentTheme 提供，经典皮肤下会有轻微「不搭」。若要彻底统一，需要为这些控件写 `ControlTheme`（工作量大，本次未做）。
2. **固定浅色**：`App.axaml` 设置了 `RequestedThemeVariant="Light"`，避免系统暗色模式与经典皮肤混搭出错。如需暗色，应作为第三套皮肤实现，而不是跟随系统。
3. **字体依赖系统**：Win7 皮肤用 `Segoe UI`（Win7+ 自带），WinXP 皮肤用 `Tahoma`（XP+ 自带），并各自回退到 `Microsoft YaHei`；Linux/macOS 上会自动回退到系统默认字体，观感会有差异。
4. **无窗口边框/标题栏定制**：`ExtendClientAreaToDecorationsHint` 未启用，所以标题栏仍是系统原生样式（这也保证了经典皮肤不会出现「圆角标题栏配方角内容」的割裂感）。
