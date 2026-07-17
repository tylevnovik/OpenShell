using FluentAssertions;
using NSubstitute;
using OpenShell.Errors;
using OpenShell.Gui.Abstractions;
using OpenShell.Paths;
using Xunit;

namespace OpenShell.Core.Tests.Gui;

/// <summary>
/// <see cref="DialogErrorExtensions"/> 单元测试。Per ADR-0043 §7, ADR-0033.
/// 验证 <see cref="DialogErrorExtensions.ToMessageBoxOptions"/> 纯函数映射 + ShowErrorAsync / ShowErrorsAsync 调用流。
/// </summary>
public class DialogErrorExtensionsTests
{
    private static ErrorRecord MakeError(
        ErrorCategory category = ErrorCategory.Unknown,
        string operation = "test-op",
        string message = "boom",
        string? detail = "stack trace",
        ItemPath? targetPath = null)
    {
        return new ErrorRecord
        {
            Category = category,
            Message = message,
            Detail = detail,
            Operation = operation,
            TargetPath = targetPath,
            Phase = ErrorPhase.Operation,
        };
    }

    /// <summary>
    /// 验证 PermissionDenied 映射: Error + YesNoCancel (允许重试)。
    /// </summary>
    [Fact]
    public void ToMessageBoxOptions_PermissionDenied_MapsToErrorYesNoCancel()
    {
        var err = MakeError(ErrorCategory.PermissionDenied, operation: "copy-item");

        var opts = DialogErrorExtensions.ToMessageBoxOptions(err);

        opts.Kind.Should().Be(MessageBoxKind.Error);
        opts.Buttons.Should().Be(MessageBoxButtons.YesNoCancel);
        opts.Title.Should().Contain("copy-item");
        opts.Title.Should().Contain("PermissionDenied");
        opts.Message.Should().Be("boom");
        opts.Detail.Should().Be("stack trace");
        opts.RelatedPath.Should().BeNull();
    }

    /// <summary>
    /// 验证 IOError 映射: Error + YesNoCancel (允许重试)。
    /// </summary>
    [Fact]
    public void ToMessageBoxOptions_IOError_MapsToErrorYesNoCancel()
    {
        var err = MakeError(ErrorCategory.IOError);

        var opts = DialogErrorExtensions.ToMessageBoxOptions(err);

        opts.Kind.Should().Be(MessageBoxKind.Error);
        opts.Buttons.Should().Be(MessageBoxButtons.YesNoCancel);
    }

    /// <summary>
    /// 验证 OperationTimeout 映射: Warning + OKCancel (允许跳过)。
    /// </summary>
    [Fact]
    public void ToMessageBoxOptions_OperationTimeout_MapsToWarningOKCancel()
    {
        var err = MakeError(ErrorCategory.OperationTimeout);

        var opts = DialogErrorExtensions.ToMessageBoxOptions(err);

        opts.Kind.Should().Be(MessageBoxKind.Warning);
        opts.Buttons.Should().Be(MessageBoxButtons.OKCancel);
    }

    /// <summary>
    /// 验证普通错误 (ItemNotFound / ParseError / Unknown 等) 映射: Warning + OK。
    /// </summary>
    [Theory]
    [InlineData(ErrorCategory.ItemNotFound)]
    [InlineData(ErrorCategory.ParseError)]
    [InlineData(ErrorCategory.InvalidArgument)]
    [InlineData(ErrorCategory.Unknown)]
    [InlineData(ErrorCategory.ProviderNotFound)]
    [InlineData(ErrorCategory.NotImplemented)]
    public void ToMessageBoxOptions_OtherErrors_MapsToWarningOK(ErrorCategory category)
    {
        var err = MakeError(category, targetPath: ItemPath.Parse("fs::C:/test"));

        var opts = DialogErrorExtensions.ToMessageBoxOptions(err);

        opts.Kind.Should().Be(MessageBoxKind.Warning);
        opts.Buttons.Should().Be(MessageBoxButtons.OK);
        opts.RelatedPath.Should().NotBeNull();
        opts.RelatedPath!.Value.Display.Should().Be("fs::C:/test");
    }

    /// <summary>
    /// 验证 operation 为 null 时 title 仍能正确生成 (前缀 "Error:" 而非 "<op>:")。
    /// </summary>
    [Fact]
    public void ToMessageBoxOptions_NullOperation_TitleContainsCategoryOnly()
    {
        var err = MakeError(ErrorCategory.IOError, operation: null!);

        var opts = DialogErrorExtensions.ToMessageBoxOptions(err);

        opts.Title.Should().StartWith("Error:");
        opts.Title.Should().Contain("IOError");
        opts.Title.Should().NotContain("copy-item");
        opts.Title.Should().NotContain("test-op");
    }

    /// <summary>
    /// 验证 ShowErrorAsync 调用 IDialogService.ShowMessageBoxAsync。
    /// </summary>
    [Fact]
    public async Task ShowErrorAsync_CallsShowMessageBoxAsync()
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowMessageBoxAsync(Arg.Any<MessageBoxOptions>(), Arg.Any<CancellationToken>())
            .Returns(DialogResult.Yes);

        var err = MakeError(ErrorCategory.PermissionDenied, operation: "copy-item");

        var result = await DialogErrorExtensions.ShowErrorAsync(dialogs, err);

        result.Should().Be(DialogResult.Yes);
        await dialogs.Received(1).ShowMessageBoxAsync(
            Arg.Is<MessageBoxOptions>(o =>
                o.Buttons == MessageBoxButtons.YesNoCancel &&
                o.Kind == MessageBoxKind.Error &&
                o.Message == "boom"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 验证 ShowErrorsAsync 多错误聚合: 第一个错误 Message + 全部错误在 Detail。
    /// </summary>
    [Fact]
    public async Task ShowErrorsAsync_MultipleErrors_AggregatesInDetail()
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowMessageBoxAsync(Arg.Any<MessageBoxOptions>(), Arg.Any<CancellationToken>())
            .Returns(DialogResult.OK);

        var errors = new[]
        {
            MakeError(ErrorCategory.IOError, operation: "copy", message: "first error"),
            MakeError(ErrorCategory.PermissionDenied, operation: "move", message: "second error"),
            MakeError(ErrorCategory.ItemNotFound, operation: "delete", message: "third error"),
        };

        await DialogErrorExtensions.ShowErrorsAsync(dialogs, errors);

        await dialogs.Received(1).ShowMessageBoxAsync(
            Arg.Is<MessageBoxOptions>(o =>
                o.Message == "first error" &&
                o.Detail != null &&
                o.Detail.Contains("first error") &&
                o.Detail.Contains("second error") &&
                o.Detail.Contains("third error")),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 验证 ShowErrorsAsync 空列表返回 Cancel, 不调用对话框。
    /// </summary>
    [Fact]
    public async Task ShowErrorsAsync_EmptyList_ReturnsCancelWithoutCallingDialog()
    {
        var dialogs = Substitute.For<IDialogService>();

        var result = await DialogErrorExtensions.ShowErrorsAsync(dialogs, Array.Empty<ErrorRecord>());

        result.Should().Be(DialogResult.Cancel);
        await dialogs.DidNotReceive().ShowMessageBoxAsync(
            Arg.Any<MessageBoxOptions>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 验证 ShowErrorAsync 传入 null 抛 ArgumentNullException。
    /// </summary>
    [Fact]
    public async Task ShowErrorAsync_NullArguments_Throws()
    {
        var dialogs = Substitute.For<IDialogService>();
        await Assert.ThrowsAsync<ArgumentNullException>(() => DialogErrorExtensions.ShowErrorAsync(null!, new ErrorRecord { Message = "x" }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DialogErrorExtensions.ShowErrorAsync(dialogs, null!));
    }

    /// <summary>
    /// 验证 ToMessageBoxOptions 传入 null 抛 ArgumentNullException。
    /// </summary>
    [Fact]
    public void ToMessageBoxOptions_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DialogErrorExtensions.ToMessageBoxOptions(null!));
    }
}
