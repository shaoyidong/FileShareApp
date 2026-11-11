using Avalonia.Media;
using FileShare.Desktop.Theming;
using System.Text.Json;

namespace FileShare.Desktop.Tests.Theming;

/// <summary>验证主题配色包：内置种子落盘、解析、坏文件容错、改色与新增包重载生效。</summary>
[Collection("Theming")]
public class ThemePackLoaderTests
{
    [Fact]
    public void Initialize_SeedsBuiltInPacks()
    {
        RunInIsolatedDirectory(packDir =>
        {
            Assert.True(File.Exists(Path.Combine(packDir, "win7.json")), "内置 win7 主题应被复制到数据目录");
            Assert.True(File.Exists(Path.Combine(packDir, "winxp.json")));
            Assert.True(File.Exists(Path.Combine(packDir, "winxp-olive.json")));

            Assert.Contains(ThemePackLoader.Packs, p => p.Id == "win7");
            Assert.Contains(ThemePackLoader.Packs, p => p.Id == "winxp");
            Assert.Contains(ThemePackLoader.Packs, p => p.Id == "winxp-olive");

            var win7 = ThemePackLoader.Find("win7");
            Assert.NotNull(win7);
            Assert.Equal("win7", win7.Base);
            Assert.Equal("Windows 7（Aero）", win7.Name);
            Assert.IsType<SolidColorBrush>(win7.Colors["FsWindowBrush"]);
        });
    }

    [Fact]
    public void TryParse_SolidGradientAndFont()
    {
        const string json = """
        {
          "id": "parse-test", "name": "解析测试", "base": "winxp",
          "colors": {
            "FsWindowBrush": "#123456",
            "FsToolbarBrush": { "start": "#010203", "end": "#040506" },
            "FsButtonFaceBrush": { "stops": [
                { "offset": 0, "color": "#FFFFFF" },
                { "offset": 1, "color": "#000000" } ] },
            "FsFontFamily": "Consolas",
            "FsBrokenBrush": "not-a-color"
          }
        }
        """;

        Assert.True(ThemePack.TryParse(json, null, out var pack));
        Assert.NotNull(pack);
        Assert.Equal("parse-test", pack.Id);
        Assert.Equal("winxp", pack.Base);

        Assert.Equal(Color.Parse("#123456"), ((ISolidColorBrush)pack.Colors["FsWindowBrush"]!).Color);
        Assert.IsType<LinearGradientBrush>(pack.Colors["FsToolbarBrush"]);
        Assert.Equal(2, ((LinearGradientBrush)pack.Colors["FsToolbarBrush"]!).GradientStops.Count);
        Assert.Equal(2, ((LinearGradientBrush)pack.Colors["FsButtonFaceBrush"]!).GradientStops.Count);
        Assert.IsType<FontFamily>(pack.Colors["FsFontFamily"]);
        Assert.False(pack.Colors.ContainsKey("FsBrokenBrush"), "非法颜色应被跳过而不影响整个主题");
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsFalse()
    {
        Assert.False(ThemePack.TryParse("{ not json", "fallback", out _));
        Assert.False(ThemePack.TryParse("[]", "fallback", out _));
        Assert.False(ThemePack.TryParse("{}", null, out _), "既无 id 也无文件名时无法注册");
        Assert.True(ThemePack.TryParse("{}", "from-file", out var pack));
        Assert.Equal("from-file", pack!.Id);
        Assert.Equal("win7", pack.Base); // 未知 base 应回退到 win7
    }

    [Fact]
    public void Reload_ModifiedColorAndBadFiles()
    {
        RunInIsolatedDirectory(packDir =>
        {
            var win7Path = Path.Combine(packDir, "win7.json");

            // 1) 直接改 JSON 里的颜色 → 重载后生效
            using (var document = JsonDocument.Parse(File.ReadAllText(win7Path)))
            {
                var colors = new Dictionary<string, object?>();
                foreach (var property in document.RootElement.GetProperty("colors").EnumerateObject())
                {
                    colors[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : property.Value.Clone();
                }
                colors["FsWindowBrush"] = "#ABCDEF";

                File.WriteAllText(win7Path, JsonSerializer.Serialize(new
                {
                    id = "win7",
                    name = "Windows 7（Aero）",
                    @base = "win7",
                    colors
                }));
            }

            // 2) 损坏文件不应影响其它主题
            File.WriteAllText(Path.Combine(packDir, "broken.json"), "{ this is not json");
            File.WriteAllText(Path.Combine(packDir, "empty.json"), "{}");

            ThemePackLoader.Reload();

            var win7 = ThemePackLoader.Find("win7");
            Assert.NotNull(win7);
            Assert.Equal(Color.Parse("#ABCDEF"), ((ISolidColorBrush)win7.Colors["FsWindowBrush"]!).Color);
            Assert.Null(ThemePackLoader.Find("broken"));
        });
    }

    [Fact]
    public void Reload_NewPack_IsRegistered()
    {
        RunInIsolatedDirectory(packDir =>
        {
            File.WriteAllText(Path.Combine(packDir, "my-dark.json"), """
            {
              "id": "my-dark",
              "name": "我的深色主题",
              "base": "winxp",
              "colors": { "FsWindowBrush": "#202020", "FsTextBrush": "#EEEEEE" }
            }
            """);

            var loaded = ThemePackLoader.Reload();
            Assert.True(loaded >= 4, "内置三个主题 + 新主题都应成功加载");

            var added = ThemePackLoader.Find("my-dark");
            Assert.NotNull(added);
            Assert.Equal("我的深色主题", added.Name);
            Assert.Equal("winxp", added.Base);
            Assert.Equal(Color.Parse("#202020"), ((ISolidColorBrush)added.Colors["FsWindowBrush"]!).Color);
            Assert.Contains(ThemePackLoader.Packs, p => p.Id == "my-dark");
        });
    }

    private static void RunInIsolatedDirectory(Action<string> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-themes-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var packDir = Path.Combine(dir, ThemePackLoader.PackFolderName);

        try
        {
            ThemePackLoader.Initialize(dir);
            test(packDir);
        }
        finally
        {
            ThemePackLoader.Initialize(FileShare.Core.Services.AppPaths.GetDataDirectory());
            Directory.Delete(dir, recursive: true);
        }
    }
}
