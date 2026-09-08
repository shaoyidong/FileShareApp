#if SIPSORCERY
using System.Threading.Channels;
using SIPSorcery.Net;

namespace LocalSend.Core.WebRtc;

/// <summary>
/// 基于 SIPSorcery 的 <see cref="IRtcDataChannel"/> 实现。
/// 封装 SIPSorcery.Net.<see cref="DataChannel"/>。
/// </summary>
/// <remarks>
/// 需要在 csproj 中添加 SIPSorcery 包引用并定义 SIPSORCERY 编译符号才能使用。
/// <code>
/// <ItemGroup>
///   <PackageReference Include="SIPSorcery" Version="10.0.12" />
/// </ItemGroup>
/// <PropertyGroup>
///   <DefineConstants>$(DefineConstants);SIPSORCERY</DefineConstants>
/// </PropertyGroup>
/// </code>
/// </remarks>
public sealed class SipsorceryDataChannel : IRtcDataChannel
{
    private readonly DataChannel _inner;
    private readonly Channel<RtcDataChannelMessage> _messages;
    private readonly TaskCompletionSource _openedTcs;

    /// <inheritdoc />
    public string Label => _inner.Label;

    /// <inheritdoc />
    public Task Opened => _openedTcs.Task;

    /// <inheritdoc />
    public ChannelReader<RtcDataChannelMessage> Messages => _messages.Reader;

    /// <summary>
    /// 构造并绑定 SIPSorcery DataChannel 事件。
    /// </summary>
    internal SipsorceryDataChannel(DataChannel inner)
    {
        _inner = inner;
        _messages = Channel.CreateUnbounded<RtcDataChannelMessage>();
        _openedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        inner.onopen += () => _openedTcs.TrySetResult();

        inner.onmessage += message =>
        {
            var isString = message.IsString;
            var data = message.Data;
            _messages.Writer.TryWrite(new RtcDataChannelMessage(isString, data));
        };

        inner.onclose += () =>
        {
            _messages.Writer.TryComplete();
            _openedTcs.TrySetResult();
        };
    }

    /// <inheritdoc />
    public Task SendTextAsync(string text, CancellationToken ct = default)
        => _inner.sendTextAsync(text);

    /// <inheritdoc />
    public Task SendBinaryAsync(byte[] data, CancellationToken ct = default)
        => _inner.sendAsync(data);

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken ct = default)
    {
        _inner.close();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WaitBufferEmptyAsync(CancellationToken ct = default)
    {
        while (_inner.BufferedAmount > 0)
        {
            await Task.Delay(10, ct);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _messages.Writer.TryComplete();
        return default;
    }
}
#endif