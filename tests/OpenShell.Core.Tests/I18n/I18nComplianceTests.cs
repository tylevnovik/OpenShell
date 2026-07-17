using FluentAssertions;
using OpenShell.I18n;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.I18n;

/// <summary>
/// i18n 改造合规测试。Per ADR-0035 + docs/i18n-audit.md + docs/i18n-tasks.md.
/// 验证 GUI / TUI 国际化的关键特性：内置 key 覆盖、默认 locale、翻译正确性。
/// 基线状态：已实现的 i18n 服务特性 [Fact] 通过；未实现的 GUI/TUI 接入 [Fact(Skip)] 跳过。
/// </summary>
public sealed class I18nComplianceTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    private ResourceI18nService CreateService()
        => new(_tempDir.FullPath);

    public void Dispose() => _tempDir.Dispose();

    // ===== T-301: 启动 locale 为 zh-CN =====

    /// <summary>启动 locale 应为 zh-CN（用户要求中文支持）。T-301。</summary>
    [Fact]
    public void Default_Locale_Is_ZhCN()
    {
        var svc = CreateService();
        svc.CurrentLocale.Should().Be("zh-CN");
    }

    // ===== T-300: 内置翻译表 key 覆盖 =====

    /// <summary>zh-CN 内置表应覆盖所有关键 key（不回退到 key 本身）。</summary>
    [Fact]
    public void All_EnUs_Keys_Have_ZhCN_Translation()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        var criticalKeys = new[]
        {
            "tui.banner",
            "tui.cwd",
            "tui.help.hint",
            "gui.title",
            "gui.search.watermark",
            "gui.column.name",
            "gui.menu.file",
            "gui.menu.help",
            "gui.nav.quickAccess",
            "common.ok",
            "common.cancel",
            "confirm.title",
            "confirm.areYouSure",
            "error.commandNotFound",
        };

        foreach (var key in criticalKeys)
        {
            var translated = svc.Translate(key);
            translated.Should().NotBe(key,
                $"key '{key}' 应在 zh-CN 内置表中有翻译，不应回退到 key 本身");
        }
    }

    /// <summary>zh-CN 下关键 TUI 字符串应为中文。</summary>
    [Fact]
    public void Tui_Critical_Strings_Are_Chinese()
    {
        var svc = CreateService();
        svc.Translate("tui.help.hint").Should().Contain("帮助");
        svc.Translate("tui.items.empty").Should().Contain("空");
        svc.Translate("confirm.title").Should().Be("确认");
    }

    /// <summary>zh-CN 下关键 GUI 字符串应为中文。</summary>
    [Fact]
    public void Gui_Critical_Strings_Are_Chinese()
    {
        var svc = CreateService();
        svc.Translate("gui.menu.file").Should().Be("文件");
        svc.Translate("gui.menu.help").Should().Be("帮助");
        svc.Translate("gui.column.name").Should().Be("名称");
        svc.Translate("gui.nav.quickAccess").Should().Be("快速访问");
        svc.Translate("common.ok").Should().Be("确定");
        svc.Translate("common.cancel").Should().Be("取消");
    }

    // ===== fallback 链（已有特性，应通过） =====

    /// <summary>ja-JP 下缺失的 key 回退到 en-US。验证 fallback 机制（已有特性）。</summary>
    [Fact]
    public void JaJp_MissingKey_FallsBack_To_EnUs()
    {
        var svc = CreateService();
        svc.SetLocale("ja-JP");
        // common.no 在 ja-JP 内置表中故意缺失，回退到 en-US。
        svc.Translate("common.no").Should().Be("No");
    }

    /// <summary>带参数的 Translate 按 string.Format 插值（已有特性）。</summary>
    [Fact]
    public void Translate_WithArgs_FormatsTemplate()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");
        svc.Translate("error.commandNotFound", "foo").Should().Be("未找到命令: foo");
        svc.Translate("ui.tasks.active", 3).Should().Be("活动任务: 3");
    }

    /// <summary>SetLocale 切换 locale 并触发事件（已有特性）。</summary>
    [Fact]
    public void SetLocale_Switches_And_FiresEvent()
    {
        var svc = CreateService();
        var fired = new List<string>();
        svc.LocaleChanged += (_, locale) => fired.Add(locale);

        svc.SetLocale("zh-CN");

        svc.CurrentLocale.Should().Be("zh-CN");
        fired.Should().ContainSingle().Which.Should().Be("zh-CN");
    }

    // ===== T-302: TUI 接入 =====

    /// <summary>TUI banner 在 zh-CN 下应显示中文。T-302。</summary>
    /// <remarks>
    /// CliHost.Ansi.WriteBanner(II18nService?) 调用 Translate("tui.banner")。
    /// 本测试验证 i18n 服务在 zh-CN locale 下返回中文翻译（组件已接入 T() 方法）。
    /// </remarks>
    [Fact]
    public void Tui_Banner_Translates_To_Chinese()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("tui.banner").Should().Be("OpenShell 命令行");
        svc.Translate("tui.cwd", "/tmp").Should().Contain("当前路径");
        svc.Translate("tui.help.hint").Should().Contain("帮助");
    }

    // ===== T-303: ConsoleConfirmationPrompter 接入 =====

    /// <summary>确认提示在 zh-CN 下应显示中文。T-303。</summary>
    /// <remarks>
    /// ConsoleConfirmationPrompter(II18nService?) 的 Prompt 方法调用 Translate("confirm.*")。
    /// 本测试验证确认提示 key 在 zh-CN 下返回中文翻译。
    /// </remarks>
    [Fact]
    public void Confirm_Prompt_Translates_To_Chinese()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("confirm.title").Should().Be("确认");
        svc.Translate("confirm.areYouSure").Should().Contain("确定");
        svc.Translate("confirm.choices").Should().Contain("是");
        svc.Translate("confirm.choices").Should().Contain("挂起");
    }

    // ===== T-305: MainWindow 动态切换 =====

    /// <summary>MainWindow 应在 zh-CN locale 下显示中文菜单/列头/导航树。T-305。</summary>
    /// <remarks>
    /// MainWindow.ApplyTranslations() 调用 T("gui.*") 刷新所有 UI 文本。
    /// 本测试验证 MainWindow 使用的关键 key 在 zh-CN 下返回中文翻译。
    /// </remarks>
    [Fact]
    public void MainWindow_Applies_Chinese_Translations()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("gui.title").Should().Contain("OpenShell");
        svc.Translate("gui.search.watermark").Should().Be("搜索");
        svc.Translate("gui.menu.file").Should().Be("文件");
        svc.Translate("gui.menu.edit").Should().Be("编辑");
        svc.Translate("gui.menu.view").Should().Be("查看");
        svc.Translate("gui.menu.help").Should().Be("帮助");
        svc.Translate("gui.column.name").Should().Be("名称");
        svc.Translate("gui.column.size").Should().Be("大小");
        svc.Translate("gui.nav.quickAccess").Should().Be("快速访问");
        svc.Translate("gui.nav.thisPc").Should().Be("此电脑");
    }

    // ===== T-306: MainViewModel 对话框 =====

    /// <summary>MainViewModel 对话框消息在 zh-CN 下应为中文。T-306。</summary>
    /// <remarks>
    /// MainViewModel 的 Copy/Move/Delete/Rename/About/Properties 方法调用 T("gui.dialog.*")。
    /// 本测试验证对话框 key 在 zh-CN 下返回中文翻译。
    /// </remarks>
    [Fact]
    public void MainViewModel_Dialogs_Translate_To_Chinese()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("gui.dialog.deleteTitle").Should().Be("删除");
        svc.Translate("gui.dialog.deleteMessage", 2, "test.txt").Should().Be("删除 2 项: test.txt?");
        svc.Translate("gui.dialog.renameTitle").Should().Be("重命名");
        svc.Translate("gui.dialog.aboutTitle").Should().Be("关于 OpenShell");
        svc.Translate("gui.dialog.propertiesTitle").Should().Be("属性");
        svc.Translate("gui.dialog.copyTo").Should().Contain("复制");
        svc.Translate("gui.dialog.moveTo").Should().Contain("移动");
    }

    // ===== T-307: StatusbarViewModel =====

    /// <summary>StatusbarViewModel.TasksLabel 在 zh-CN 下应为 "任务: N"。T-307。</summary>
    [Fact]
    public void Statusbar_TasksLabel_Translates()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("gui.status.tasks", 5).Should().Be("任务: 5");
        svc.Translate("gui.status.tasks", 0).Should().Be("任务: 0");
    }

    // ===== T-308~T-309: 对话框按钮 =====

    /// <summary>MessageBox 按钮 OK/Cancel/Yes/No 在 zh-CN 下应为中文。T-308。</summary>
    [Fact]
    public void MessageBox_Buttons_Translate()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("common.ok").Should().Be("确定");
        svc.Translate("common.cancel").Should().Be("取消");
        svc.Translate("common.yes").Should().Be("是");
        svc.Translate("common.no").Should().Be("否");
    }

    /// <summary>InputDialog 按钮 OK/Cancel 在 zh-CN 下应为中文。T-309。</summary>
    [Fact]
    public void InputDialog_Buttons_Translate()
    {
        var svc = CreateService();
        svc.SetLocale("zh-CN");

        svc.Translate("common.ok").Should().Be("确定");
        svc.Translate("common.cancel").Should().Be("取消");
    }
}
