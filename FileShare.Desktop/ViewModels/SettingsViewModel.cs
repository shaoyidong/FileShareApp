using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FileShare.Desktop.ViewModels;

/// <summary>设备类型下拉项。</summary>
public sealed record DeviceTypeOption(DeviceType Value, string Text);

/// <summary>
/// 设置页视图模型：设备名、设备类型、端口、加密开关、保存目录、自动接收、界面皮肤。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly Action _backAction;

    [ObservableProperty]
    private string _deviceName = string.Empty;

    [ObservableProperty]
    private string _portText = "5237";

    [ObservableProperty]
    private bool _tlsEnabled;

    [ObservableProperty]
    private bool _autoAcceptFromFavorites;

    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    [ObservableProperty]
    private DeviceTypeOption? _selectedDeviceType;

    [ObservableProperty]
    private string _statusMessage = "修改后点击「保存」；设备名称 / 端口 / 加密开关需要重启应用生效。";

    /// <summary>可选设备类型。</summary>
    public IReadOnlyList<DeviceTypeOption> DeviceTypeOptions { get; } = new[]
    {
        new DeviceTypeOption(DeviceType.Desktop, "桌面电脑 / 笔记本"),
        new DeviceTypeOption(DeviceType.Mobile, "手机"),
        new DeviceTypeOption(DeviceType.Tablet, "平板")
    };

    /// <summary>Windows 7 风格。</summary>
    public bool IsWin7Skin
    {
        get => _themeService.Current == AppSkin.Win7;
        set
        {
            if (value)
            {
                ApplySkin(AppSkin.Win7);
            }
        }
    }

    /// <summary>Windows XP 风格。</summary>
    public bool IsWinXPSkin
    {
        get => _themeService.Current == AppSkin.WinXP;
        set
        {
            if (value)
            {
                ApplySkin(AppSkin.WinXP);
            }
        }
    }

    /// <summary>设置文件路径（只读展示，便于用户备份）。</summary>
    public string SettingsFilePath => string.IsNullOrEmpty(_settingsService.SettingsFilePath)
        ? "（内存模式，未持久化）"
        : _settingsService.SettingsFilePath;

    public ICommand BackCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BrowseSaveDirectoryCommand { get; }
    public ICommand ClearSaveDirectoryCommand { get; }

    public SettingsViewModel(
        IAppSettingsService settingsService,
        IThemeService themeService,
        IDialogService dialogService,
        Action backAction)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _backAction = backAction ?? throw new ArgumentNullException(nameof(backAction));

        BackCommand = new RelayCommand(Back);
        SaveCommand = new RelayCommand(Save);
        BrowseSaveDirectoryCommand = new RelayCommand(async () => await BrowseSaveDirectoryAsync());
        ClearSaveDirectoryCommand = new RelayCommand(() => SaveDirectory = string.Empty);

        Load();
    }

    private void Load()
    {
        var settings = _settingsService.Current;
        DeviceName = settings.DeviceName;
        PortText = settings.TransferPort.ToString();
        TlsEnabled = settings.TlsEnabled;
        AutoAcceptFromFavorites = settings.AutoAcceptFromFavorites;
        SaveDirectory = settings.SaveDirectory ?? string.Empty;
        SelectedDeviceType = FindOption(settings.DeviceType);
    }

    private DeviceTypeOption FindOption(DeviceType deviceType)
    {
        foreach (var option in DeviceTypeOptions)
        {
            if (option.Value == deviceType)
            {
                return option;
            }
        }

        return DeviceTypeOptions[0];
    }

    private void ApplySkin(AppSkin skin)
    {
        if (_themeService.Current == skin)
        {
            return;
        }

        _themeService.Apply(skin);
        _settingsService.Update(settings => settings.Skin = skin);

        OnPropertyChanged(nameof(IsWin7Skin));
        OnPropertyChanged(nameof(IsWinXPSkin));
        StatusMessage = skin == AppSkin.WinXP ? "已切换为 Windows XP 风格" : "已切换为 Windows 7 风格";
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            StatusMessage = "设备名称不能为空";
            return;
        }

        if (!int.TryParse(PortText?.Trim(), out var port) || port is < 1024 or > 65535)
        {
            StatusMessage = "端口必须是 1024~65535 之间的整数";
            return;
        }

        _settingsService.Update(settings =>
        {
            settings.DeviceName = DeviceName.Trim();
            settings.DeviceType = SelectedDeviceType?.Value ?? settings.DeviceType;
            settings.TransferPort = port;
            settings.TlsEnabled = TlsEnabled;
            settings.AutoAcceptFromFavorites = AutoAcceptFromFavorites;
            settings.SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory) ? null : SaveDirectory.Trim();
            settings.Skin = _themeService.Current;
        });

        StatusMessage = "设置已保存；设备名称 / 端口 / 加密开关需要重启应用后生效。";
    }

    private async Task BrowseSaveDirectoryAsync()
    {
        var folders = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择接收文件的保存目录",
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return;
        }

        var uri = folders[0].Path;
        SaveDirectory = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeFile ? uri.LocalPath : uri.OriginalString;
    }

    private void Back()
    {
        _backAction();
    }
}
