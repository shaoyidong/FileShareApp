using FileShare.Desktop.Localization;

namespace FileShare.Desktop.Tests.Localization;

/// <summary>
/// i18n 机制测试：中英切换、缺失回退、模板格式化、XAML 标记扩展。
/// </summary>
public class LocTests
{
    [Fact]
    public void English_TranslatesKnownKeys()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.EnUS);

            Assert.Equal("Send", Loc.Get("发送"));
            Assert.Equal("Cancel", Loc.Get("取消"));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void Chinese_ReturnsOriginalText()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.ZhCN);

            Assert.Equal("发送", Loc.Get("发送"));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void UnknownKey_FallsBackToChinese()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.EnUS);

            Assert.Equal("这条文案没有英文", Loc.Get("这条文案没有英文"));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void Format_FillsTemplatePlaceholders()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.ZhCN);
            Assert.Equal("正在发送 (1/3): a.txt", Loc.F("正在发送 ({0}/{1}): {2}", 1, 3, "a.txt"));

            Loc.SetLanguage(AppLanguage.EnUS);
            Assert.Equal("Sending (1/3): a.txt", Loc.F("正在发送 ({0}/{1}): {2}", 1, 3, "a.txt"));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void Format_WithFormatSpecifier_KeepsPrecision()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.EnUS);

            var text = Loc.F("正在传输: {0} ({1:F1}%)", "a.bin", 42.34);
            Assert.Equal("Transferring: a.bin (42.3%)", text);
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void LanguageChanged_FiresOnlyWhenChanged()
    {
        var previous = Loc.Language;
        var count = 0;
        void Handler() => count++;

        Loc.LanguageChanged += Handler;
        try
        {
            Loc.SetLanguage(previous);
            Assert.Equal(0, count);

            Loc.SetLanguage(previous == AppLanguage.ZhCN ? AppLanguage.EnUS : AppLanguage.ZhCN);
            Assert.Equal(1, count);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void TrExtension_ReturnsLocalizedText()
    {
        var previous = Loc.Language;
        try
        {
            Loc.SetLanguage(AppLanguage.EnUS);
            var extension = new TrExtension("发送文件（可多选）");

            Assert.Equal("Send files (multi-select)", extension.ProvideValue(null!));
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }
}
