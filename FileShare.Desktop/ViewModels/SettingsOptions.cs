using FileShare.Core.Models;

namespace FileShare.Desktop.ViewModels;


/// <summary>语言下拉项。</summary>
public sealed record LanguageOption(string Code, string Text);

/// <summary>主题下拉项（id 来自 themes/*.json）。</summary>
public sealed record ThemeOption(string Id, string Name);

/// <summary>设备类型下拉项。</summary>
public sealed record DeviceTypeOption(DeviceType Value, string Text);
