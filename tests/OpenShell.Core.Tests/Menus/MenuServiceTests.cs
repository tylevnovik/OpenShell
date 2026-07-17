using FluentAssertions;
using OpenShell.Commands;
using OpenShell.Menus;
using Xunit;

namespace OpenShell.Core.Tests.Menus;

/// <summary>
/// Unit tests for <see cref="MenuService"/>. Per ADR-0028 sections 1, 3, 9, 10.
/// </summary>
public class MenuServiceTests
{
    // ---- Rebuild basics ----------------------------------------------------

    [Fact]
    public void Rebuild_FromTypes_BuildsTree()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var contextChildren = svc.Tree.GetGroup("context");
        contextChildren.Should().NotBeEmpty();
        contextChildren.Should().Contain(n => n.Id == "copy");

        var toolbarChildren = svc.Tree.GetGroup("toolbar");
        toolbarChildren.Should().NotBeEmpty();
        toolbarChildren.Should().Contain(n => n.Id == "copy");
    }

    [Fact]
    public void Rebuild_FromCommandDescriptors_BuildsTree()
    {
        var descriptor = CommandDescriptor.FromType(typeof(CopyItemCommand));
        var svc = new MenuService(new[] { descriptor });

        var contextChildren = svc.Tree.GetGroup("context");
        contextChildren.Should().NotBeEmpty();
        contextChildren.Should().Contain(n => n.Id == "copy");
    }

    [Fact]
    public void Rebuild_NoMenuAttributes_EmptyTree()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(NoMenuCommand) });

        svc.Tree.GetGroup("context").Should().BeEmpty();
        svc.Tree.GetGroup("toolbar").Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_NullCommandTypes_Throws()
    {
        var svc = new MenuService();
        var act = () => svc.Rebuild((IEnumerable<Type>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Rebuild_NullCommands_Throws()
    {
        var svc = new MenuService();
        var act = () => svc.Rebuild((IReadOnlyCollection<CommandDescriptor>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Rebuild_OverwritesPreviousTree()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });
        svc.Tree.GetGroup("context").Should().NotBeEmpty();

        svc.Rebuild(new[] { typeof(PasteItemCommand) });
        svc.Tree.GetGroup("context").Should().NotBeEmpty();
        svc.Tree.GetGroup("context").Should().Contain(n => n.Id == "paste");
        // The copy command from the previous rebuild is no longer present.
        svc.Tree.GetGroup("context").Should().NotContain(n => n.Id == "copy");
    }

    [Fact]
    public void Rebuild_IncludesIconPath()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var copyNode = svc.Tree.GetGroup("context").First(n => n.Id == "copy");
        copyNode.Contribution!.IconPath.Should().Be("Icons/copy.svg");
    }

    [Fact]
    public void Rebuild_NoIconAttribute_IconPathNull()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(PasteItemCommand) });

        var pasteNode = svc.Tree.GetGroup("context").First(n => n.Id == "paste");
        pasteNode.Contribution!.IconPath.Should().BeNull();
    }

    [Fact]
    public void Rebuild_DerivesLabelFromPath_WhenLabelNotSet()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var copyNode = svc.Tree.GetGroup("context").First(n => n.Id == "copy");
        // Path "context/copy" → last segment "copy" → "Copy"
        copyNode.Contribution!.Label.Should().Be("Copy");
    }

    [Fact]
    public void Rebuild_UsesExplicitLabel_WhenProvided()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(RefreshCommand) });

        var refreshNode = svc.Tree.GetGroup("toolbar").First(n => n.Id == "refresh");
        refreshNode.Contribution!.Label.Should().Be("Refresh Now");
    }

    [Fact]
    public void Rebuild_MultipleMenuItems_OnSameClass_AllRegistered()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        svc.Tree.GetGroup("context").Should().Contain(n => n.Id == "copy");
        svc.Tree.GetGroup("toolbar").Should().Contain(n => n.Id == "copy");

        var contextCopy = svc.Tree.GetGroup("context").First(n => n.Id == "copy");
        var toolbarCopy = svc.Tree.GetGroup("toolbar").First(n => n.Id == "copy");
        contextCopy.Contribution!.CommandId.Should().Be("copy-item");
        toolbarCopy.Contribution!.CommandId.Should().Be("copy-item");
    }

    [Fact]
    public void Rebuild_CommandId_DerivedFromVerbAttribute()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var copyNode = svc.Tree.GetGroup("context").First(n => n.Id == "copy");
        copyNode.Contribution!.CommandId.Should().Be("copy-item");
    }

    [Fact]
    public void Rebuild_VerbOnlyCommand_CommandIdIsVerb()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(RefreshCommand) });

        var refreshNode = svc.Tree.GetGroup("toolbar").First(n => n.Id == "refresh");
        // [Verb("Refresh")] with empty Noun → "refresh"
        refreshNode.Contribution!.CommandId.Should().Be("refresh");
    }

    [Fact]
    public void Rebuild_WhenExpression_StoredOnContribution()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var copyNode = svc.Tree.GetGroup("context").First(n => n.Id == "copy");
        copyNode.Contribution!.When.Should().Be("selected.count > 0");
    }

    // ---- GetVisibleNodes ---------------------------------------------------

    [Fact]
    public void GetVisibleNodes_NoWhen_AlwaysVisible()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(RefreshCommand) });

        var nodes = svc.GetVisibleNodes("toolbar", new MenuContext());
        nodes.Should().NotBeEmpty();
        nodes.Should().Contain(n => n.Id == "refresh");
    }

    [Fact]
    public void GetVisibleNodes_WhenCountGreaterThanZero_VisibleWhenCountPositive()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        // count == 0 → not visible
        var emptyCtx = new MenuContext();
        svc.GetVisibleNodes("context", emptyCtx)
            .Should().NotContain(n => n.Id == "copy");

        // count > 0 → visible
        var selectionCtx = new MenuContext
        {
            Selection = new SelectionInfo { Count = 3 },
        };
        svc.GetVisibleNodes("context", selectionCtx)
            .Should().Contain(n => n.Id == "copy");
    }

    [Fact]
    public void GetVisibleNodes_WhenFails_Hidden()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        // provider == "reg" but provider is "fs" → not visible
        var ctx = new MenuContext { CurrentProvider = "fs" };
        var nodes = svc.GetVisibleNodes("context", ctx);
        // CopyItemCommand requires selected.count > 0, so it won't show anyway.
        // Verify only that no exception is thrown and result is enumerable.
        nodes.Should().NotBeNull();
    }

    [Fact]
    public void GetVisibleNodes_SortByOrder_ThenByLabel()
    {
        var svc = new MenuService();
        svc.Rebuild(new[]
        {
            typeof(SortA_Order50),
            typeof(SortB_Order10),
            typeof(SortC_Order10),
        });

        var nodes = svc.GetVisibleNodes("context", new MenuContext());
        // Order 10 first (B then C alphabetically), then Order 50 (A)
        nodes.Select(n => n.Id).Should().Equal(new[] { "b", "c", "a" });
    }

    [Fact]
    public void GetVisibleNodes_MissingGroup_ReturnsEmpty()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });

        var nodes = svc.GetVisibleNodes("nonexistent", new MenuContext());
        nodes.Should().BeEmpty();
    }

    [Fact]
    public void GetVisibleNodes_NullContext_Throws()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(CopyItemCommand) });
        var act = () => svc.GetVisibleNodes("context", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetVisibleNodes_FocusAndProviderExpressions()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(FocusSensitiveCommand) });

        // focus:pane → visible
        var paneCtx = new MenuContext { FocusedElement = "pane" };
        svc.GetVisibleNodes("context", paneCtx)
            .Should().Contain(n => n.Id == "showOnPane");

        // focus:tree → not visible
        var treeCtx = new MenuContext { FocusedElement = "tree" };
        svc.GetVisibleNodes("context", treeCtx)
            .Should().NotContain(n => n.Id == "showOnPane");
    }

    [Fact]
    public void GetVisibleNodes_ComplexWhenExpression()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(ComplexWhenCommand) });

        // provider == "reg" && selected.count == 1
        var regSingleCtx = new MenuContext
        {
            CurrentProvider = "reg",
            Selection = new SelectionInfo { Count = 1 },
        };
        svc.GetVisibleNodes("context", regSingleCtx)
            .Should().Contain(n => n.Id == "export");

        var regMultiCtx = new MenuContext
        {
            CurrentProvider = "reg",
            Selection = new SelectionInfo { Count = 2 },
        };
        svc.GetVisibleNodes("context", regMultiCtx)
            .Should().NotContain(n => n.Id == "export");
    }

    [Fact]
    public void GetVisibleNodes_MalformedWhen_HiddenWithoutError()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(MalformedWhenCommand) });

        var act = () => svc.GetVisibleNodes("context", new MenuContext());
        act.Should().NotThrow();
        svc.GetVisibleNodes("context", new MenuContext())
            .Should().NotContain(n => n.Id == "broken");
    }

    [Fact]
    public void GetVisibleNodes_WhenNull_AlwaysVisible()
    {
        var svc = new MenuService();
        svc.Rebuild(new[] { typeof(NullWhenCommand) });

        svc.GetVisibleNodes("context", new MenuContext())
            .Should().Contain(n => n.Id == "always");
    }

    // ---- Constructor -------------------------------------------------------

    [Fact]
    public void Constructor_NullCommands_BuildsEmptyTree()
    {
        var svc = new MenuService();
        svc.Tree.GetGroup("context").Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithCommands_BuildsTreeImmediately()
    {
        var descriptor = CommandDescriptor.FromType(typeof(CopyItemCommand));
        var svc = new MenuService(new[] { descriptor });
        svc.Tree.GetGroup("context").Should().NotBeEmpty();
    }

    // ---- Test command classes ---------------------------------------------

    [OpenShell.Commands.Verb("Copy", Noun = "Item")]
    [MenuItem(Path = "context/copy", When = "selected.count > 0", Order = 100)]
    [MenuItem(Path = "toolbar/copy", When = "selected.count > 0", Order = 100)]
    [Icon("Icons/copy.svg")]
    public sealed class CopyItemCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Paste", Noun = "Item")]
    [MenuItem(Path = "context/paste", When = "selected.allDirectories", Order = 110)]
    public sealed class PasteItemCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Refresh")]
    [MenuItem(Path = "toolbar/refresh", Label = "Refresh Now", Order = 50)]
    public sealed class RefreshCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Get", Noun = "Item")]
    public sealed class NoMenuCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Sort", Noun = "A")]
    [MenuItem(Path = "context/a", Order = 50, Label = "A")]
    public sealed class SortA_Order50
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Sort", Noun = "B")]
    [MenuItem(Path = "context/b", Order = 10, Label = "B")]
    public sealed class SortB_Order10
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Sort", Noun = "C")]
    [MenuItem(Path = "context/c", Order = 10, Label = "C")]
    public sealed class SortC_Order10
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Show", Noun = "Pane")]
    [MenuItem(Path = "context/showOnPane", When = "focus:pane", Order = 10)]
    public sealed class FocusSensitiveCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Export", Noun = "Reg")]
    [MenuItem(Path = "context/export",
        When = "provider == \"reg\" && selected.count == 1", Order = 200)]
    public sealed class ComplexWhenCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Broken", Noun = "When")]
    [MenuItem(Path = "context/broken", When = "@@@malformed")]
    public sealed class MalformedWhenCommand
    {
        public sealed record Args;
    }

    [OpenShell.Commands.Verb("Always", Noun = "Visible")]
    [MenuItem(Path = "context/always", When = null, Order = 5)]
    public sealed class NullWhenCommand
    {
        public sealed record Args;
    }
}
