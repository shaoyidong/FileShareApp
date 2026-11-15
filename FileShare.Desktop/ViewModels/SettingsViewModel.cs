using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileShare.Core.Models;
using FileShare.Core.Services;
using FileShare.Desktop.Services;
using FileShare.Desktop.Services.Dialogs;
using FileShare.LocalSend;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using FileShare.Desktop.Localization;

namespace FileShare.Desktop.ViewModels;

/// <summary>
/// 设置页视图模型：设备名、设备类型、端口、加密、保存目录、同名策略、托盘、发现方式、并发、皮肤、信任设备。
/// <para>标注「重启生效」的项在启动时被读入服务，因此修改后需要重启应用。</para>
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IAppSettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IPlatformDialogService _dialogService;
    private readonly IDialogService _dialogs;
    private readonly Action _backAction;

    [ObservableProperty]
    public partial string DeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PortText { get; set; } = "5237";

    [ObservableProperty]
    public partial bool TlsEnabled { get; set; }

    [ObservableProperty]
    public partial bool AutoAcceptFromFavorites { get; set; }

    [ObservableProperty]
    public partial string SaveDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DeviceTypeOption? SelectedDeviceType { get; set; }

    [ObservableProperty]
    public partial bool OverwriteExisting { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool EnableMdns { get; set; }

    [ObservableProperty]
    public partial bool EnableMulticast { get; set; }

    [ObservableProperty]
    public partial bool EnableBroadcast { get; set; }

    [ObservableProperty]
    public partial string MulticastAddress { get; set; } = "224.0.0.167";

    [ObservableProperty]
    public partial string MulticastPortText { get; set; } = "53317";

    [ObservableProperty]
    public partial string BroadcastPortText { get; set; } = "5236";


    [ObservableProperty]
    public partial string NetworkWhitelistText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkBlacklistText { get; set; } = string.Empty;
    [ObservableProperty]
    public partial string ParallelTransfersText { get; set; } = "1";

    [ObservableProperty]
    public partial string UploadLimitText { get; set; } = "0";

    [ObservableProperty]
    public partial bool LocalSendEnabled { get; set; }

    [ObservableProperty]
    public partial string LocalSendPortText { get; set; } = "53317";

    [ObservableProperty]
    public partial string LocalSendPinText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalSendEndpointText { get; set; } = "（未启用）";

    [ObservableProperty]
    public partial string SelectedLanguageCode { get; set; } = "zh-CN";

    /// <summary>可选语言：从已注册的语言包动态生成（重载语言包后可刷新）。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LanguageOption> LanguageOptions { get; set; } = Array.Empty<LanguageOption>();

    /// <summary>当前主题 id。</summary>
    [ObservableProperty]
    public partial string SelectedThemeId { get; set; } = "win7";

    /// <summary>可选主题：从已注册的主题包动态生成（重载主题后可刷新）。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ThemeOption> ThemeOptions { get; set; } = Array.Empty<ThemeOption>();

    /// <summary>下拉框选中项变化时立即应用主题（保存按钮不需要管主题）。</summary>
    partial void OnSelectedThemeIdChanged(string value) => ApplyTheme(value);

    [ObservableProperty]
    public partial bool WebShareEnabled { get; set; }

    [ObservableProperty]
    public partial string WebSharePortText { get; set; } = "8088";

    [ObservableProperty]
    public partial string WebShareUrlText { get; set; } = Loc.Get("（未启用）");

    [ObservableProperty]
    public partial string WebSharePinText { get; set; } = "-";

    [ObservableProperty]
    public partial string TrustedDeviceCountText { get; set; } = Loc.Get("未启用加密");

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Loc.Get("修改后点击「保存」；标注「重启生效」的项需要重启应用。");

    /// <summary>可选设备类型。</summary>
    public IReadOnlyList<DeviceTypeOption> DeviceTypeOptions { get; } = new[]
    {
        new DeviceTypeOption(DeviceType.Desktop, Loc.Get("桌面电脑 / 笔记本")),
        new DeviceTypeOption(DeviceType.Mobile, Loc.Get("手机")),
        new DeviceTypeOption(DeviceType.Tablet, Loc.Get("平板"))
    };

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
    public ICommand ReloadLanguagesCommand { get; }
    public ICommand ReloadThemesCommand { get; }
    public ICommand ShowAboutCommand { get; }

    public SettingsViewModel(
        IAppSettingsService settingsService,
        IThemeService themeService,
        IPlatformDialogService dialogService,
        IDialogService dialogs,
        Action backAction)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
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
        ReloadLanguagesCommand = new RelayCommand(ReloadLanguages);
        ReloadThemesCommand = new RelayCommand(ReloadThemes);
        ShowAboutCommand = new RelayCommand(() => _dialogs.Show<AboutViewModel>());

        RefreshLanguageOptions();
        RefreshThemeOptions();
        Load();
        RefreshWebShareStatus();
        RefreshTrustedDevices();
    }

    /// <summary>刷新语言下拉框（语言包文件可在应用发布后修改 / 新增）。</summary>
    private void RefreshLanguageOptions() =>
        LanguageOptions = Loc.Packs
            .Select(pack => new LanguageOption(pack.Code, pack.DisplayName))
            .ToArray();

    /// <summary>重新读取 i18n 目录下的语言包文件，立即应用改过的译文或新增加的语言。</summary>
    private void ReloadLanguages()
    {
        Loc.ReloadPacks();
        RefreshLanguageOptions();
        StatusMessage = Loc.Get("语言包已重载");
    }

    /// <summary>刷新主题下拉框（themes 目录下的 JSON 可在应用发布后修改 / 新增）。</summary>
    private void RefreshThemeOptions() =>
        ThemeOptions = _themeService.AvailableThemes
            .Select(pack => new ThemeOption(pack.Id, pack.Name))
            .ToArray();

    /// <summary>重新读取 themes 目录，立即应用改过的配色或新增加的主题。</summary>
    private void ReloadThemes()
    {
        var loaded = _themeService.ReloadThemes();
        RefreshThemeOptions();
        SelectedThemeId = _themeService.Current;
        StatusMessage = Loc.F("主题包已重载（共 {0} 个）", loaded);
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
        // 托盘行为属于桌面端 UI 偏好，不再从 Core 的 AppSettings 读取
        var uiPreferences = ResolveUiPreferences()?.Current;
        MinimizeToTray = uiPreferences?.MinimizeToTray ?? false;
        CloseToTray = uiPreferences?.CloseToTray ?? false;
        EnableMdns = settings.EnableMdns;
        EnableMulticast = settings.EnableMulticast;
        EnableBroadcast = settings.EnableBroadcast;
        MulticastAddress = settings.MulticastAddress;
        MulticastPortText = settings.MulticastPort.ToString();
        BroadcastPortText = settings.BroadcastPort.ToString();
        NetworkWhitelistText = settings.NetworkWhitelist;
        LocalSendEnabled = settings.LocalSendEnabled;
        LocalSendPortText = settings.LocalSendPort.ToString();
        LocalSendPinText = settings.LocalSendPin;
        SelectedLanguageCode = uiPreferences?.LanguageCode ?? "zh-CN";
        SelectedThemeId = uiPreferences?.SkinId ?? "win7";
        NetworkBlacklistText = settings.NetworkBlacklist;
        ParallelTransfersText = settings.ParallelTransfers.ToString();
        UploadLimitText = settings.UploadLimitKBps.ToString();
        WebShareEnabled = settings.WebShareEnabled;
        WebSharePortText = settings.WebSharePort.ToString();
        RefreshLocalSendStatus();
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

    private void ApplyTheme(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId) ||
            string.Equals(_themeService.Current, themeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ThemeService 内部会持久化到桌面端私有配置文件（desktop-ui.json）
        _themeService.Apply(themeId);
        SelectedThemeId = _themeService.Current;

        var name = _themeService.AvailableThemes
            .FirstOrDefault(pack => string.Equals(pack.Id, SelectedThemeId, StringComparison.OrdinalIgnoreCase))
            ?.Name ?? SelectedThemeId;
        StatusMessage = Loc.F("已切换主题：{0}", name);
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

        if (!int.TryParse(LocalSendPortText?.Trim(), out var localSendPort) || localSendPort is < 1024 or > 65535)
        {
            StatusMessage = Loc.Get("LocalSend 端口必须是 1024~65535 之间的整数");
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
            settings.LocalSendEnabled = LocalSendEnabled;
            settings.LocalSendPort = localSendPort;
            settings.LocalSendPin = LocalSendPinText?.Trim() ?? string.Empty;
        });


        // LocalSend 互通：与网页分享同一时机立即应用
        ResolveLocalSendHost()?.Apply();
        RefreshLocalSendStatus();
        // 托盘开关即时生效，其余（设备名/端口/加密/发现方式/同名策略）在重启后生效
        App.TrayService?.SetVisible(MinimizeToTray || CloseToTray);
        var uiPreferences = ResolveUiPreferences();
        if (uiPreferences is not null)
        {
            uiPreferences.SetTrayPreferences(MinimizeToTray, CloseToTray);
            uiPreferences.SetLanguage(SelectedLanguageCode);

            // 立即生效（会触发 Loc.LanguageChanged → 主界面重建）
            Loc.SetLanguage(SelectedLanguageCode);
        }

        ResolveWebShareHost()?.Apply();
        RefreshWebShareStatus();

        StatusMessage = Loc.Get("设置已保存。设备名称 / 端口 / 加密 / 发现方式 / 同名策略需要重启生效。");
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

        /// <summary>LocalSend 互通宿主。</summary>
        private static ILocalSendInteropHost? ResolveLocalSendHost()
        {
            return App.ServiceProvider?.GetService(typeof(ILocalSendInteropHost)) as ILocalSendInteropHost;
        }

        private void RefreshLocalSendStatus()
        {
            var host = ResolveLocalSendHost();
            LocalSendEndpointText = host is { IsRunning: true, Endpoint: { } endpoint }
                ? endpoint
                : Loc.Get("（未启用）");
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
