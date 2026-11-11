using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace FileShare.Desktop.Themes;

/// <summary>
/// Windows 7 / Aero 基础皮肤。带 <c>x:Class</c> 的编译期 XAML：构造即加载编译产物，
/// 不经过运行时 XAML URI 加载（AvaloniaXamlLoader 的 URI 路径在 NativeAOT 下不可用），
/// 因此 NativeAOT 发布下也能正常换肤。调色板可被主题包 JSON 覆盖（见 Theming/ThemePackLoader）。
/// </summary>
public partial class Win7Skin : Styles
{
    public Win7Skin()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
