namespace FileShare.Desktop.Services.Dialogs;

/// <summary>
/// 弹窗参数键值集合（仿 Prism 的 IDialogParameters），实现逻辑在 <see cref="ParametersBase"/>。
/// </summary>
public interface IDialogParameters
{
    /// <summary>添加或覆盖一个参数。</summary>
    void Add<T>(string key, T? value);

    /// <summary>读取参数；键不存在或类型不匹配时抛出异常。</summary>
    T GetValue<T>(string key);

    /// <summary>尝试读取参数。</summary>
    bool TryGetValue<T>(string key, out T value);

    /// <summary>是否包含指定键。</summary>
    bool ContainsKey(string key);
}

/// <inheritdoc />
public sealed class DialogParameters : ParametersBase, IDialogParameters
{
}
