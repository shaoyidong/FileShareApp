using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.ViewModels;

/// <summary>设备类型下拉项。</summary>

/// <summary>语言下拉项。</summary>
public sealed record LanguageOption(string Code, string Text);
public sealed record DeviceTypeOption(DeviceType Value, string Text);

/// <summary>
/// 设置页视图模型：设备名、设备类型、端口、加密、保存目录、同名策略、托盘、发现方式、并发、皮肤、信任设备。
/// <para>标注「重启生效」的项在启动时被读入服务，因此修改后需要重启应用。</para>
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
    private bool _overwriteExisting;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _closeToTray;

    [ObservableProperty]
    private bool _enableMdns;

    [ObservableProperty]
    private bool _enableMulticast;

    [ObservableProperty]
    private bool _enableBroadcast;

    [ObservableProperty]
    private string _multicastAddress = "224.0.0.167";

    [ObservableProperty]
    private string _multicastPortText = "53317";

    [ObservableProperty]
    private string _broadcastPortText = "5236";


    [ObservableProperty]
    private string _networkWhitelistText = string.Empty;

    [ObservableProperty]
    private string _networkBlacklistText = string.Empty;
    [ObservableProperty]
    private string _parallelTransfersText = "1";

    [ObservableProperty]
    private string _uploadLimitText = "0";

    [ObservableProperty]
    private string _selectedLanguageCode = "zh-CN";

    /// <summary>可选语言。</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } = new[]
    {
        new LanguageOption("zh-CN", "简体中文"),
        new LanguageOption("en-US", "English"),
    };

    [ObservableProperty]
    private bool _webShareEnabled;

    [ObservableProperty]
    private string _webSharePortText = "8088";

    [ObservableProperty]
    private string _webShareUrlText = Loc.Get("（未启用）");

    [ObservableProperty]
    private string _webSharePinText = "-";

    [ObservableProperty]
    private string _trustedDeviceCountText = Loc.Get("未启用加密");

    [ObservableProperty]
    private string _statusMessage = Loc.Get("修改后点击「保存」；标注「重启生效」的项需要重启应用。");

    /// <summary>可选设备类型。</summary>
    public IReadOnlyList<DeviceTypeOption> DeviceTypeOptions { get; } = new[]
    {
        new DeviceTypeOption(DeviceType.Desktop, Loc.Get("桌面电脑 / 笔记本")),
        new DeviceTypeOption(DeviceType.Mobile, Loc.Get("手机")),
        new DeviceTypeOption(DeviceType.Tablet, Loc.Get("平板"))
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

    /// <summary>Windows XP 经典蓝（Luna Blue）。</summary>
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

    /// <summary>Windows XP 橄榄绿（Luna Olive）。</summary>
    public bool IsWinXPOliveSkin
    {
        get => _themeService.Current == AppSkin.WinXPOlive;
        set
        {
            if (value)
            {
                ApplySkin(AppSkin.WinXPOlive);
            }
        }
    }

    /// <summary>设置文件路径（只读展示，便于用户备份）。</summary>
    public string SettingsFilePath => string.IsNullOrEmpty(_settingsService.SettingsFilePath)
        ? Loc.Get("（内存模式，未持久化）")
        : _settingsService.SettingsFilePath;

    public ICommand BackCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BrowseSaveDirectoryCommand { get; }
    public ICommand ClearSaveDirectoryCommand { get; }
    public ICommand ClearTrustedDevicesCommand { get; }
    public ICommand RefreshTrustedDevicesCommand { get; }
    public ICommand RegenerateWebSharePinCommand { get; }
    public ICommand OpenWebShareCommand { get; }
    public ICommand RefreshWebShareCommand { get; }

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
        ClearTrustedDevicesCommand = new RelayCommand(ClearTrustedDevices);
        RefreshTrustedDevicesCommand = new RelayCommand(RefreshTrustedDevices);
        RegenerateWebSharePinCommand = new RelayCommand(RegenerateWebSharePin);
        OpenWebShareCommand = new RelayCommand(OpenWebShare);
        RefreshWebShareCommand = new RelayCommand(RefreshWebShareStatus);

        Load();
        RefreshWebShareStatus();
        RefreshTrustedDevices();
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
        OverwriteExisting = settings.ConflictPolicy == FileNameConflictPolicy.Overwrite;
        MinimizeToTray = settings.MinimizeToTray;
        CloseToTray = settings.CloseToTray;
        EnableMdns = settings.EnableMdns;
        EnableMulticast = settings.EnableMulticast;
        EnableBroadcast = settings.EnableBroadcast;
        MulticastAddress = settings.MulticastAddress;
        MulticastPortText = settings.MulticastPort.ToString();
        BroadcastPortText = settings.BroadcastPort.ToString();
        NetworkWhitelistText = settings.NetworkWhitelist;
        SelectedLanguageCode = ResolveUiPreferences()?.Current.Language switch
        {
            AppLanguage.EnUS => "en-US",
            _ => "zh-CN",
        };
        NetworkBlacklistText = settings.NetworkBlacklist;
        ParallelTransfersText = settings.ParallelTransfers.ToString();
        UploadLimitText = settings.UploadLimitKBps.ToString();
        WebShareEnabled = settings.WebShareEnabled;
        WebSharePortText = settings.WebSharePort.ToString();
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

        // ThemeService 内部会持久化到桌面端私有配置文件（desktop-ui.json）
        _themeService.Apply(skin);

        OnPropertyChanged(nameof(IsWin7Skin));
        OnPropertyChanged(nameof(IsWinXPSkin));
        OnPropertyChanged(nameof(IsWinXPOliveSkin));

        StatusMessage = skin switch
        {
            AppSkin.WinXP => Loc.Get("已切换为 Windows XP 经典蓝（Luna Blue）"),
            AppSkin.WinXPOlive => Loc.Get("已切换为 Windows XP 橄榄绿（Luna Olive）"),
            _ => Loc.Get("已切换为 Windows 7 风格（Aero）")
        };
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            StatusMessage = Loc.Get("设备名称不能为空");
            return;
        }

        if (!int.TryParse(PortText?.Trim(), out var port) || port is < 1024 or > 65535)
        {
            StatusMessage = Loc.Get("传输端口必须是 1024~65535 之间的整数");
            return;
        }

        if (!int.TryParse(MulticastPortText?.Trim(), out var multicastPort) || multicastPort is < 1024 or > 65535)
        {
            StatusMessage = Loc.Get("组播端口必须是 1024~65535 之间的整数");
            return;
        }

        if (!int.TryParse(BroadcastPortText?.Trim(), out var broadcastPort) || broadcastPort is < 1024 or > 65535)
        {
            StatusMessage = Loc.Get("广播端口必须是 1024~65535 之间的整数");
            return;
        }

        if (!int.TryParse(ParallelTransfersText?.Trim(), out var parallelism) || parallelism is < 1 or > 4)
        {
            StatusMessage = Loc.Get("并发发送数必须是 1~4 之间的整数");
            return;
        }

        if (!int.TryParse(WebSharePortText?.Trim(), out var webSharePort) || webSharePort is < 1024 or > 65535)
        {
            StatusMessage = Loc.Get("网页分享端口必须是 1024~65535 之间的整数");
            return;
        }

        if (!int.TryParse(UploadLimitText?.Trim(), out var uploadLimit) || uploadLimit is < 0 or > 1000000)
        {
            StatusMessage = Loc.Get("上传限速必须是 0~1000000 之间的整数（KB/s，0 表示不限速）");
            return;
        }

        if (!System.Net.IPAddress.TryParse(MulticastAddress?.Trim(), out var multicast) ||
            multicast.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            StatusMessage = Loc.Get("组播地址必须是合法的 IPv4 地址，例如 224.0.0.167");
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

            settings.ConflictPolicy = OverwriteExisting ? FileNameConflictPolicy.Overwrite : FileNameConflictPolicy.Rename;
            settings.MinimizeToTray = MinimizeToTray;
            settings.CloseToTray = CloseToTray;
            settings.EnableMdns = EnableMdns;
            settings.EnableMulticast = EnableMulticast;
            settings.EnableBroadcast = EnableBroadcast;
            settings.MulticastAddress = MulticastAddress.Trim();
            settings.MulticastPort = multicastPort;
            settings.BroadcastPort = broadcastPort;
            settings.NetworkWhitelist = NetworkWhitelistText?.Trim() ?? string.Empty;
            settings.NetworkBlacklist = NetworkBlacklistText?.Trim() ?? string.Empty;
            settings.ParallelTransfers = parallelism;
            settings.UploadLimitKBps = uploadLimit;
            settings.WebShareEnabled = WebShareEnabled;
            settings.WebSharePort = webSharePort;
        });

        // 托盘开关即时生效，其余（设备名/端口/加密/发现方式/同名策略）在重启后生效
        App.TrayService?.SetVisible(MinimizeToTray || CloseToTray);
        var uiPreferences = ResolveUiPreferences();
        if (uiPreferences is not null)
        {
            var language = SelectedLanguageCode == "en-US" ? AppLanguage.EnUS : AppLanguage.ZhCN;
            uiPreferences.SetLanguage(language);
        }

        ResolveWebShareHost()?.Apply();
        RefreshWebShareStatus();

        StatusMessage = Loc.Get(SelectedLanguageCode == "en-US"
            ? "设置已保存。语言切换将在重启后生效；设备名称 / 端口 / 加密 / 发现方式 / 同名策略同样需要重启。"
            : "设置已保存。设备名称 / 端口 / 加密 / 发现方式 / 同名策略需要重启生效。");
    }

    private async Task BrowseSaveDirectoryAsync()
    {
        var folders = await _dialogService.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = Loc.Get("选择接收文件的保存目录"),
            AllowMultiple = false
        });

        if (folders.Count == 0)
        {
            return;
        }

        var uri = folders[0].Path;
        SaveDirectory = uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeFile ? uri.LocalPath : uri.OriginalString;
    }

        /// <summary>桌面端 UI 偏好服务（皮肤 / 语言）。</summary>
        private static IUiPreferencesService? ResolveUiPreferences()
        {
            return App.ServiceProvider?.GetService(typeof(IUiPreferencesService)) as IUiPreferencesService;
        }
        /// <summary>获取网页分享宿主（未注册时返回 null，设计器/测试环境安全）。</summary>
        private static IWebShareHost? ResolveWebShareHost()
        {
            return App.ServiceProvider?.GetService(typeof(IWebShareHost)) as IWebShareHost;
        }

        private void RefreshWebShareStatus()
        {
            var host = ResolveWebShareHost();
            if (host is null || string.IsNullOrEmpty(host.Url))
            {
                WebShareUrlText = Loc.Get("（未启用）");
                WebSharePinText = "-";
                return;
            }

            WebShareUrlText = host.Url;
            WebSharePinText = host.Pin;
        }

        private void RegenerateWebSharePin()
        {
            var host = ResolveWebShareHost();
            if (host is null)
            {
                return;
            }

            var pin = host.RegeneratePin();
            RefreshWebShareStatus();
            StatusMessage = WebShareEnabled
                ? Loc.F("已生成新 PIN：{0}（旧链接立即失效）", pin)
                : Loc.Get("已生成新 PIN；启用网页分享后生效");
        }

        private void OpenWebShare()
        {
            var host = ResolveWebShareHost();
            var url = host?.Url;
            if (string.IsNullOrEmpty(url))
            {
                StatusMessage = Loc.Get("网页分享未启用，请先勾选「启用网页分享」并保存");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.F("打开浏览器失败: {0}", ex.Message);
            }
        }

    private void RefreshTrustedDevices()
    {
        try
        {
            var trusted = App.ServiceProvider is null
                ? null
                : (App.ServiceProvider.GetService(typeof(FileShare.Core.Services.FileShareServiceManager))
                    as FileShare.Core.Services.FileShareServiceManager);

            if (trusted is null)
            {
                TrustedDeviceCountText = Loc.Get("未启用加密");
                return;
            }

            var count = trusted.GetTrustedDevices().Count;
            TrustedDeviceCountText = count == 0 ? Loc.Get("暂无信任记录") : Loc.F("已信任 {0} 台设备", count);
        }
        catch (Exception)
        {
            TrustedDeviceCountText = Loc.Get("读取失败");
        }
    }

    private async void ClearTrustedDevices()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Loc.Get("清除信任记录"),
            Loc.Get("清除后，所有设备下次连接都会重新记录证书指纹（相当于重新 TOFU）。确定继续吗？"));

        if (!confirmed)
        {
            return;
        }

        if (App.ServiceProvider?.GetService(typeof(FileShare.Core.Services.FileShareServiceManager))
            is FileShare.Core.Services.FileShareServiceManager manager)
        {
            manager.ClearTrustedDevices();
        }

        RefreshTrustedDevices();
        StatusMessage = Loc.Get("已清除全部信任记录。");
    }

    private void Back()
    {
        _backAction();
    }
}
