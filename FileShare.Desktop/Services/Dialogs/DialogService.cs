using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// <see cref="IDialogService"/> 的实现：统一管理弹窗窗口的创建、显示、结果回传与超时。
/// <para>所有弹窗创建都发生在 UI 线程；主窗口隐藏到托盘时不能作为 owner（Avalonia 要求 owner 已显示），
/// 非模态退化为独立显示，模态退化为无 owner 模态。</para>
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly DialogRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly Func<Window?> _ownerProvider;

    /// <summary>已打开的非模态弹窗（按 ViewModel 类型跟踪，供 activateExisting 复用激活）。</summary>
    private readonly Dictionary<Type, Window> _openDialogs = new();

    public DialogService(DialogRegistry registry, IServiceProvider serviceProvider, Func<Window?> ownerProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
    }

    /// <inheritdoc />
    public void ShowDialog(string name, IDialogParameters? parameters, Action<IDialogResult> callback) =>
        RunOnUi(() => ShowCore(Resolve(name), parameters, options: null, modal: true, callback));

    /// <inheritdoc />
    public void ShowDialog<TViewModel>(IDialogParameters? parameters, Action<IDialogResult> callback)
        where TViewModel : class, IDialogAware =>
        RunOnUi(() => ShowCore(Resolve<TViewModel>(), parameters, options: null, modal: true, callback));

    /// <inheritdoc />
    public async Task<IDialogResult> ShowDialogAsync(
        string name, IDialogParameters? parameters = null, DialogOptions? options = null)
    {
        var completion = StartOnUi(name, parameters, options);
        return await completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IDialogResult> ShowDialogAsync<TViewModel>(
        IDialogParameters? parameters = null, DialogOptions? options = null)
        where TViewModel : class, IDialogAware
    {
        var completion = StartOnUi(null, parameters, options, typeof(TViewModel));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Window? Show<TViewModel>(IDialogParameters? parameters = null, Action<IDialogResult>? closedCallback = null)
        where TViewModel : class, IDialogAware
    {
        if (Dispatcher.UIThread.CheckAccess() is false)
        {
            throw new InvalidOperationException($"{nameof(Show)} 必须在 UI 线程调用（可用 Dispatcher.UIThread.Post 包裹）。");
        }

        return ShowCore(Resolve<TViewModel>(), parameters, options: null, modal: false, closedCallback);
    }

    /// <inheritdoc />
    public void Show(string name, IDialogParameters? parameters = null, Action<IDialogResult>? closedCallback = null) =>
        RunOnUi(() => ShowCore(Resolve(name), parameters, options: null, modal: false, closedCallback));

    /// <summary>异步打开的核心：创建并显示弹窗，结果通过完成源回传。</summary>
    private TaskCompletionSource<IDialogResult> StartOnUi(
        string? name, IDialogParameters? parameters, DialogOptions? options, Type? viewModelType = null)
    {
        var completion = new TaskCompletionSource<IDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Open()
        {
            try
            {
                var registration = viewModelType is not null ? Resolve(viewModelType) : Resolve(name!);
                ShowCore(registration, parameters, options, modal: true,
                    callback: result => completion.TrySetResult(result));
            }
            catch (Exception ex)
            {
                completion.TrySetResult(new DialogResult(ButtonResult.None, exception: ex));
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Open();
        }
        else
        {
            Dispatcher.UIThread.Post(Open);
        }

        return completion;
    }

    /// <summary>同步打开的核心：解析 VM 与窗口、接线生命周期、显示（模态 / 非模态）。UI 线程专用。</summary>
    private Window? ShowCore(
        DialogRegistration registration,
        IDialogParameters? parameters,
        DialogOptions? options,
        bool modal,
        Action<IDialogResult>? callback)
    {
        // activateExisting：同类型非模态弹窗已打开时只激活，不重复创建（也不触发本次回调）
        if (!modal &&
            registration.ActivateExisting &&
            _openDialogs.TryGetValue(registration.ViewModelType, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var vm = registration.ViewModelFactory(_serviceProvider);
        var view = registration.ViewFactory(_serviceProvider);
        Window window;
        if (view is Window viewWindow)
        {
            // 窗口弹窗：View 自带 chrome（行为同 Prism.Avalonia 的 ShowDialogWindow / ShowWindow）
            window = viewWindow;
        }
        else
        {
            // 内容弹窗（仿 Prism ShowDialog）：UserControl 放入宿主窗口，Title 取自 ViewModel
            window = CreateHostWindow(options?.HostWindow);
            window.Content = view;
            if (!string.IsNullOrEmpty(vm.Title))
            {
                window.Title = vm.Title;
            }
        }

        window.DataContext = vm;

        IDialogResult? delivered = null;
        var closedHandled = false;

        void HandleClosed()
        {
            if (closedHandled)
            {
                return;
            }

            closedHandled = true;
            _openDialogs.Remove(registration.ViewModelType);
            vm.OnDialogClosed();
            callback?.Invoke(delivered ?? new DialogResult(
                modal ? ButtonResult.Cancel : ButtonResult.None));
        }

        window.Closed += (_, _) => HandleClosed();

        // 标题栏 / 系统关闭：给 VM 一次拒绝的机会（仿 Prism CanCloseDialog）
        window.Closing += (_, e) =>
        {
            if (!vm.CanCloseDialog())
            {
                e.Cancel = true;
            }
        };

        // ViewModel 主动关闭：把结果带给 ShowDialog / 回调
        vm.RequestClose += result =>
        {
            delivered = result;
            window.Close(result);
        };

        vm.OnDialogOpened(parameters ?? new DialogParameters());

        // 可选的宿主窗口配置（仿 Prism 的 Dialog.WindowStyle，由 VM 以类型化对象提供）：
        // 内容弹窗作用于宿主窗口，窗口弹窗作用于自身；只应用显式赋值项
        if (vm is IDialogAwareWindowOptions configurable)
        {
            var windowOptions = new DialogWindowOptions();
            configurable.ConfigureWindowOptions(windowOptions);
            windowOptions.ApplyTo(window);
        }

        var owner = options?.Owner ?? _ownerProvider();

        try
        {
            if (modal)
            {
                // Avalonia 的 ShowDialog 要求 owner 非空且已显示；
                // 应用约定是「主窗口不可见时先 Toast，点击后再激活并弹窗」（见 MainViewModel / LocalSendUiBridge）
                if (owner is not { IsVisible: true })
                {
                    throw new InvalidOperationException(
                        "模态弹窗要求 owner 已显示：主窗口隐藏到托盘时应先激活窗口（Toast 点击）再调用。");
                }

                _ = window.ShowDialog<IDialogResult>(owner);
            }
            else
            {
                // 主窗口隐藏到托盘时不能作为 owner（Avalonia 要求 owner 已显示），改为独立窗口
                if (owner is { IsVisible: true })
                {
                    window.Show(owner);
                }
                else
                {
                    window.Show();
                }

                _openDialogs[registration.ViewModelType] = window;
            }
        }
        catch (Exception ex)
        {
            closedHandled = true;
            vm.OnDialogClosed();
            callback?.Invoke(new DialogResult(ButtonResult.None, exception: ex));
            return null;
        }

        // 超时：到点自动关闭，结果走 Cancel（Close(null) → ShowDialog 返回 null → delivered 为空）
        if (options?.Timeout is { } timeout)
        {
            _ = Task.Delay(timeout).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (window.IsVisible)
                    {
                        window.Close(null);
                    }
                });
            });
        }

        return window;
    }

    /// <summary>解析内容弹窗的宿主窗口：优先 DialogOptions.HostWindow 指定的命名宿主，
    /// 再退回默认宿主（AddDialogWindow 注册），最后退回 DI 中的内置 <see cref="DialogWindow"/>。</summary>
    private Window CreateHostWindow(string? hostName)
    {
        if (_registry.TryGetHostWindow(hostName, out var factory))
        {
            return factory(_serviceProvider);
        }

        return _serviceProvider.GetService<DialogWindow>() ?? new DialogWindow();
    }

    private DialogRegistration Resolve(string name)
    {
        if (_registry.TryGet(name, out var registration))
        {
            return registration;
        }

        throw new InvalidOperationException($"No dialog registered with name '{name}'.");
    }

    private DialogRegistration Resolve(Type viewModelType)
    {
        if (_registry.TryGet(viewModelType, out var registration))
        {
            return registration;
        }

        throw new InvalidOperationException(
            $"No dialog registered for view model '{viewModelType.Name}'.");
    }

    private DialogRegistration Resolve<TViewModel>() where TViewModel : class, IDialogAware =>
        Resolve(typeof(TViewModel));

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
