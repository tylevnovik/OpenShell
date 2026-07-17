using FluentAssertions;
using OpenShell.I18n;
using OpenShell.TestUtils;
using Xunit;

namespace OpenShell.Core.Tests.I18n;

/// <summary>
/// Unit tests for <see cref="ResourceI18nService"/>. Per ADR-0035.
/// Uses a temp directory for user locale JSON files to avoid polluting the real user home.
/// </summary>
public sealed class ResourceI18nServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    /// <summary>启动 locale 为 zh-CN (Per i18n 改造 T-301)。</summary>
    [Fact]
    public void Startup_Locale_Is_ZhCN()
    {
        var svc = CreateService();

        svc.CurrentLocale.Should().Be("zh-CN");
    }

    /// <summary>en-US 下翻译已知 key 返回内置英文模板。</summary>
    [Fact]
    public void Translate_KnownKey_EnUs_ReturnsBuiltInTemplate()
    {
        var svc = CreateService();
        svc.SetLocale("en-US");

        svc.Translate("shell.banner").Should().Be("OpenShell CLI");
        svc.Translate("error.commandNotFound").Should().Be("command not found: {0}");
    }

    /// <summary>未知 key 返回 key 本身。</summary>
    [Fact]
    public void Translate_UnknownKey_ReturnsKeyItself()
    {
        var svc = CreateService();

        svc.Translate("nonexistent.key.does.not.exist").Should().Be("nonexistent.key.does.not.exist");
    }

    /// <summary>SetLocale 切换 CurrentLocale。</summary>
    [Fact]
    public void SetLocale_SwitchesCurrentLocale()
    {
        var svc = CreateService();

        svc.SetLocale("zh-CN");

        svc.CurrentLocale.Should().Be("zh-CN");
    }

    /// <summary>SetLocale 加载用户文件: 与内置表合并, 用户条目覆盖内置同名条目。</summary>
    [Fact]
    public void SetLocale_LoadsUserFile_MergesWithBuiltIn_UserOverrides()
    {
        _tempDir.CreateFile("zh-CN.json",
            """{ "shell.banner": "用户自定义横幅", "custom.user.key": "用户自定义值" }""");

        var svc = CreateService();

        svc.SetLocale("zh-CN");

        // 用户覆盖的 key。
        svc.Translate("shell.banner").Should().Be("用户自定义横幅");
        // 用户新增的 key。
        svc.Translate("custom.user.key").Should().Be("用户自定义值");
        // 未覆盖的内置 key 仍可用。
        svc.Translate("shell.prompt").Should().Be("{0}> ");
        svc.Translate("error.commandNotFound").Should().Be("未找到命令: {0}");
    }

    /// <summary>SetLocale 时用户文件不存在: 不抛异常, 保留内置表。</summary>
    [Fact]
    public void SetLocale_NonExistentFile_KeepsBuiltIn_NoThrow()
    {
        var svc = CreateService();

        var act = () => svc.SetLocale("zh-CN");

        act.Should().NotThrow();
        svc.Translate("shell.banner").Should().Be("OpenShell 命令行");
    }

    /// <summary>SetLocale 时用户文件 JSON 非法: 不抛异常, 保留内置表 (graceful degradation)。</summary>
    [Fact]
    public void SetLocale_InvalidJson_KeepsBuiltIn_NoThrow()
    {
        _tempDir.CreateFile("zh-CN.json", "{ this is not valid json,,, ");

        var svc = CreateService();

        var act = () => svc.SetLocale("zh-CN");

        act.Should().NotThrow();
        // 内置 zh-CN 表应保留 (用户文件被忽略)。
        svc.Translate("shell.banner").Should().Be("OpenShell 命令行");
        svc.Translate("error.commandNotFound").Should().Be("未找到命令: {0}");
    }

    /// <summary>fallback 链: ja-JP 缺失的 key 回退到 en-US。</summary>
    [Fact]
    public void Translate_MissingKeyInJaJp_FallsBackToEnUs()
    {
        var svc = CreateService();

        svc.SetLocale("ja-JP");

        // common.no 在 ja-JP 内置表中故意缺失, 应回退到 en-US 的 "No"。
        svc.Translate("common.no").Should().Be("No");
        // ja-JP 自身的 key 仍正常返回。
        svc.Translate("shell.banner").Should().Be("OpenShell CLI");
        svc.Translate("commands.get-childitem.description").Should().Be("コンテナ内の項目を一覧表示");
    }

    /// <summary>带参数的 Translate 按 string.Format 插值。</summary>
    [Fact]
    public void Translate_WithFormatArgs_FormatsTemplate()
    {
        var svc = CreateService();

        // 启动 locale 为 zh-CN, 用 zh-CN 模板插值。
        svc.Translate("shell.prompt", "cwd").Should().Be("cwd> ");
        svc.Translate("error.commandNotFound", "foo").Should().Be("未找到命令: foo");
        svc.Translate("ui.tasks.active", 3).Should().Be("活动任务: 3");

        // 切到 en-US 后, 用 en-US 模板插值。
        svc.SetLocale("en-US");
        svc.Translate("error.commandNotFound", "foo").Should().Be("command not found: foo");
    }

    /// <summary>SetLocale 触发 LocaleChanged 事件, 参数为新 locale。</summary>
    [Fact]
    public void SetLocale_FiresLocaleChangedEvent()
    {
        var svc = CreateService();
        var fired = new List<string>();
        svc.LocaleChanged += (_, locale) => fired.Add(locale);

        svc.SetLocale("zh-CN");

        fired.Should().ContainSingle();
        fired[0].Should().Be("zh-CN");
    }

    /// <summary>AvailableLocales 包含内置 locale 与用户文件中的 locale。</summary>
    [Fact]
    public void AvailableLocales_IncludesBuiltinsAndUserFiles()
    {
        _tempDir.CreateFile("fr-FR.json", """{ "shell.banner": "OpenShell CLI FR" }""");
        _tempDir.CreateFile("de-DE.json", """{ "shell.banner": "OpenShell CLI DE" }""");

        var svc = CreateService();

        svc.AvailableLocales.Should().Contain(new[] { "zh-CN", "en-US", "ja-JP", "fr-FR", "de-DE" });
        // 内置 locale 排在前面 (zh-CN 首位)。
        svc.AvailableLocales.Take(3).Should().ContainInOrder("zh-CN", "en-US", "ja-JP");
    }

    /// <summary>构造时传入自定义 localesDir, 服务从该目录读取用户文件。</summary>
    [Fact]
    public void Constructor_CustomLocalesDir_ReadsUserFilesFromThatDir()
    {
        // 在临时目录下放一个 es-ES.json, 验证服务从该目录发现并加载。
        _tempDir.CreateFile("es-ES.json",
            """{ "shell.banner": "OpenShell CLI ES", "error.commandNotFound": "comando no encontrado: {0}" }""");

        var svc = CreateService();

        // 自定义 locale 出现在 AvailableLocales 中 (证明扫描了自定义目录)。
        svc.AvailableLocales.Should().Contain("es-ES");

        svc.SetLocale("es-ES");

        svc.CurrentLocale.Should().Be("es-ES");
        svc.Translate("shell.banner").Should().Be("OpenShell CLI ES");
        svc.Translate("error.commandNotFound", "foo").Should().Be("comando no encontrado: foo");
    }

    /// <summary>LoadLocaleAsync 显式加载用户文件并合并到内置表。</summary>
    [Fact]
    public async Task LoadLocaleAsync_LoadsUserFile_MergesWithBuiltIn()
    {
        _tempDir.CreateFile("zh-CN.json",
            """{ "shell.banner": "异步加载横幅", "ui.tasks.active": "异步任务: {0}" }""");

        var svc = CreateService();

        await svc.LoadLocaleAsync("zh-CN");

        // 加载后但未切换 locale 时, 启动 locale (zh-CN) 仍为当前 locale。
        // 已预加载的用户覆盖已合并到 zh-CN 表中。
        svc.CurrentLocale.Should().Be("zh-CN");
        svc.Translate("shell.banner").Should().Be("异步加载横幅");

        // 切换到 en-US 后, en-US 不受 zh-CN 用户文件影响。
        svc.SetLocale("en-US");
        svc.Translate("shell.banner").Should().Be("OpenShell CLI");
    }

    private ResourceI18nService CreateService()
        => new(_tempDir.FullPath);

    public void Dispose() => _tempDir.Dispose();
}
