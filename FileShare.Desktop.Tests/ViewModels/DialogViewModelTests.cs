using FileShare.Core.Models;
using Shyd.Prism.Dialogs;
using FileShare.Desktop.ViewModels;
using System.Collections.Generic;
using Xunit;

namespace FileShare.Desktop.Tests.ViewModels;

/// <summary>
/// 弹窗 ViewModel（IDialogAware）：参数注入、RequestClose 结果语义。
/// </summary>
public class DialogViewModelTests
{
    /// <summary>收集 RequestClose 触发过的结果。</summary>
    private static List<IDialogResult> Capture(IDialogAware viewModel)
    {
        var results = new List<IDialogResult>();
        viewModel.RequestClose += result => results.Add(result);
        return results;
    }

    [Fact]
    public void SendTextViewModel_Open_SetsTargetText()
    {
        var viewModel = new SendTextViewModel();

        viewModel.OnDialogOpened(new DialogParameters
        {
            [SendTextViewModel.DialogKeys.TargetDeviceName] = "手机"
        });

        Assert.Contains("手机", viewModel.TargetText);
    }

    [Fact]
    public void SendTextViewModel_Send_RaisesOkWithText()
    {
        var viewModel = new SendTextViewModel();
        var results = Capture(viewModel);
        viewModel.Text = "你好";

        viewModel.SendCommand.Execute(null);

        var result = Assert.Single(results);
        Assert.Equal(ButtonResult.Ok, result.Result);
        Assert.Equal("你好", result.Parameters.GetValue<string>(SendTextViewModel.DialogKeys.Text));
    }

    [Fact]
    public void SendTextViewModel_SendEmptyText_DoesNotClose()
    {
        var viewModel = new SendTextViewModel();
        var results = Capture(viewModel);

        viewModel.SendCommand.Execute(null);

        Assert.Empty(results);
    }

    [Fact]
    public void SendTextViewModel_Cancel_RaisesCancelWithoutText()
    {
        var viewModel = new SendTextViewModel();
        var results = Capture(viewModel);
        viewModel.Text = "你好";

        viewModel.CancelCommand.Execute(null);

        var result = Assert.Single(results);
        Assert.Equal(ButtonResult.Cancel, result.Result);
        Assert.False(result.Parameters.ContainsKey(SendTextViewModel.DialogKeys.Text));
    }

    [Fact]
    public void ReceiveRequestViewModel_Open_PopulatesDisplayTexts()
    {
        var viewModel = new ReceiveRequestViewModel();
        var transfer = new FileTransferInfo
        {
            TransferId = "t1",
            FileName = "photo.jpg",
            FileSize = 2048,
            SenderId = "sender-1",
            ReceiverId = "receiver-1"
        };

        viewModel.OnDialogOpened(new DialogParameters
        {
            [ReceiveRequestViewModel.DialogKeys.Transfer] = transfer,
            [ReceiveRequestViewModel.DialogKeys.SaveDirectory] = @"D:\Downloads",
            [ReceiveRequestViewModel.DialogKeys.SenderName] = "手机"
        });

        Assert.Contains("手机", viewModel.SenderText);
        Assert.Equal("photo.jpg", viewModel.FileText);
        Assert.Contains("2.0", viewModel.SizeText);
        Assert.Equal(@"D:\Downloads", viewModel.DirectoryText);
    }

    [Fact]
    public void ReceiveRequestViewModel_Accept_RaisesOkWithResult()
    {
        var viewModel = new ReceiveRequestViewModel();
        var results = Capture(viewModel);
        viewModel.AlwaysAccept = true;

        viewModel.AcceptCommand.Execute(null);

        var result = Assert.Single(results);
        Assert.Equal(ButtonResult.Ok, result.Result);
        var choice = result.Parameters.GetValue<ReceiveRequestResult>(ReceiveRequestViewModel.DialogKeys.Result);
        Assert.True(choice.Accept);
        Assert.True(choice.AlwaysAccept);
    }

    [Fact]
    public void ReceiveRequestViewModel_Decline_RaisesNoWithResult()
    {
        var viewModel = new ReceiveRequestViewModel();
        var results = Capture(viewModel);

        viewModel.DeclineCommand.Execute(null);

        var result = Assert.Single(results);
        Assert.Equal(ButtonResult.No, result.Result);
        var choice = result.Parameters.GetValue<ReceiveRequestResult>(ReceiveRequestViewModel.DialogKeys.Result);
        Assert.False(choice.Accept);
        Assert.False(choice.AlwaysAccept);
    }

    [Fact]
    public void MessageReceivedViewModel_Open_PopulatesMessage()
    {
        var viewModel = new MessageReceivedViewModel();

        viewModel.OnDialogOpened(new DialogParameters
        {
            [MessageReceivedViewModel.DialogKeys.SenderName] = "手机",
            [MessageReceivedViewModel.DialogKeys.Message] = "全文内容"
        });

        Assert.Contains("手机", viewModel.SenderText);
        Assert.Equal("全文内容", viewModel.Message);
    }

    [Fact]
    public void MessageReceivedViewModel_Close_RaisesCancel()
    {
        var viewModel = new MessageReceivedViewModel();
        var results = Capture(viewModel);

        viewModel.CloseCommand.Execute(null);

        Assert.Equal(ButtonResult.Cancel, Assert.Single(results).Result);
    }

    [Fact]
    public void MessageReceivedViewModel_ConfiguresHost_NotActivated()
    {
        // 消息窗不抢焦点：宿主窗口必须以非激活方式显示
        var viewModel = new MessageReceivedViewModel();
        var options = new DialogWindowOptions();

        viewModel.ConfigureWindowOptions(options);

        Assert.False(options.ShowActivated);
        Assert.Null(options.Width);
        Assert.Null(options.Height);
    }

    [Fact]
    public void AllDialogViewModels_AllowCloseAndExposeTitle()
    {
        IDialogAware[] viewModels =
        {
            new SendTextViewModel(),
            new ReceiveRequestViewModel(),
            new MessageReceivedViewModel(),
            new AboutViewModel()
        };

        foreach (var viewModel in viewModels)
        {
            Assert.False(string.IsNullOrWhiteSpace(viewModel.Title));
            Assert.True(viewModel.CanCloseDialog());
        }
    }
}
