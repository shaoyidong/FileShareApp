using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace FileShare.Desktop.Themes;

/// <summary>
/// Windows XP / Luna Olive 基础皮肤。编译期 XAML（AOT 安全），说明见 <see cref="Win7Skin"/>。
/// </summary>
public partial class WinXPOliveSkin : Styles
{
    public WinXPOliveSkin()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
