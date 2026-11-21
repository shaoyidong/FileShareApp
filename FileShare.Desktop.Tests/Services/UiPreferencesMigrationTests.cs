using FileShare.Desktop.Services;
using Shyd.Prism.Localization;

namespace FileShare.Desktop.Tests.Services;

/// <summary>旧版 desktop-ui.json 的 Skin 枚举 / Language 枚举应一次性迁移成字符串 id。</summary>
public class UiPreferencesMigrationTests
{
    [Theory]
    [InlineData(0, "win7")]
    [InlineData(1, "winxp")]
    [InlineData(2, "winxp-olive")]
    public void LegacySkinEnum_IsMigratedToThemeId(int legacySkin, string expectedId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-uipref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "desktop-ui.json"),
                $"{{\"Skin\":{legacySkin},\"Language\":1,\"MinimizeToTray\":true}}");

            // 语言迁移会对照 Loc.Packs 校验代码合法性：框架不内置翻译内容，测试环境自行注册 en-US
            Loc.Register(new JsonLanguagePack("en-US", "English",
                new Dictionary<string, string>()));

            var service = new JsonUiPreferencesService(dir);
            Assert.Equal(expectedId, service.Current.SkinId);
            Assert.Equal("en-US", service.Current.LanguageCode);
            Assert.True(service.Current.MinimizeToTray);

            // 迁移结果应写回新格式（旧 Skin 字段消失）
            var rewritten = File.ReadAllText(Path.Combine(dir, "desktop-ui.json"));
            Assert.Contains("SkinId", rewritten);
            Assert.DoesNotContain("\"Skin\"", rewritten);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetSkinId_PersistsCustomTheme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fs-uipref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var service = new JsonUiPreferencesService(dir);
            service.SetSkinId("my-theme");

            var reloaded = new JsonUiPreferencesService(dir);
            Assert.Equal("my-theme", reloaded.Current.SkinId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
