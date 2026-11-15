using FileShare.Desktop.Services.Dialogs;
using System;
using System.Collections.Generic;
using Xunit;

namespace FileShare.Desktop.Tests.Services.Dialogs;

/// <summary>
/// 弹窗参数集合：读写、缺失与类型不匹配的语义。
/// </summary>
public class DialogParametersTests
{
    [Fact]
    public void AddGetValue_RoundTrips()
    {
        var parameters = new DialogParameters();
        parameters.Add("name", "设备A");
        parameters.Add("size", 1024L);

        Assert.Equal("设备A", parameters.GetValue<string>("name"));
        Assert.Equal(1024L, parameters.GetValue<long>("size"));
        Assert.True(parameters.ContainsKey("name"));
        Assert.False(parameters.ContainsKey("missing"));
    }

    [Fact]
    public void Indexer_SupportsInitializerStyle()
    {
        var parameters = new DialogParameters
        {
            ["text"] = "hello",
            ["flag"] = true
        };

        Assert.Equal("hello", parameters.GetValue<string>("text"));
        Assert.True(parameters.GetValue<bool>("flag"));
    }

    [Fact]
    public void GetValue_MissingKey_Throws()
    {
        var parameters = new DialogParameters();

        Assert.Throws<KeyNotFoundException>(() => parameters.GetValue<string>("missing"));
    }

    [Fact]
    public void GetValue_WrongType_Throws()
    {
        var parameters = new DialogParameters();
        parameters.Add("count", 42);

        Assert.Throws<InvalidOperationException>(() => parameters.GetValue<string>("count"));
    }

    [Fact]
    public void TryGetValue_MissingKey_ReturnsFalse()
    {
        var parameters = new DialogParameters();

        Assert.False(parameters.TryGetValue<string>("missing", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryGetValue_NullStoredValue_ReturnsTrueWithNull()
    {
        var parameters = new DialogParameters();
        parameters.Add<string?>("note", null);

        Assert.True(parameters.TryGetValue<string?>("note", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void DialogResult_DefaultParameters_NotNull()
    {
        var result = new DialogResult(ButtonResult.Ok);

        Assert.Equal(ButtonResult.Ok, result.Result);
        Assert.NotNull(result.Parameters);
        Assert.Null(result.Exception);
    }
}
