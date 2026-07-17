using FluentAssertions;
using OpenShell.When;
using Xunit;

namespace OpenShell.Core.Tests.When;

public class WhenExpressionTests
{
    private static IReadOnlyDictionary<string, object?> Ctx(params (string key, object? value)[] pairs)
        => pairs.ToDictionary(p => p.key, p => p.value);

    // ---- Empty / null expressions ----------------------------------------

    [Fact]
    public void NullExpression_AlwaysTrue()
    {
        var expr = WhenExpression.Parse(null);
        expr.IsEmpty.Should().BeTrue();
        expr.Evaluate(Ctx()).Should().BeTrue();
    }

    [Fact]
    public void EmptyExpression_AlwaysTrue()
    {
        var expr = WhenExpression.Parse("");
        expr.IsEmpty.Should().BeTrue();
        expr.Evaluate(Ctx()).Should().BeTrue();
    }

    [Fact]
    public void WhitespaceExpression_AlwaysTrue()
    {
        var expr = WhenExpression.Parse("   ");
        expr.IsEmpty.Should().BeTrue();
        expr.Evaluate(Ctx()).Should().BeTrue();
    }

    // ---- Equality (== and :) ---------------------------------------------

    [Fact]
    public void ColonOperator_StringMatch_CaseInsensitive()
    {
        // ADR-0027 style: focus:pane
        var expr = WhenExpression.Parse("focus:pane");
        expr.Evaluate(Ctx(("focus", "pane"))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "Pane"))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "tree"))).Should().BeFalse();
    }

    [Fact]
    public void DoubleEquals_StringMatch()
    {
        var expr = WhenExpression.Parse("provider == \"reg\"");
        expr.Evaluate(Ctx(("provider", "reg"))).Should().BeTrue();
        expr.Evaluate(Ctx(("provider", "fs"))).Should().BeFalse();
    }

    [Fact]
    public void NotEquals_StringMismatch()
    {
        var expr = WhenExpression.Parse("provider != \"reg\"");
        expr.Evaluate(Ctx(("provider", "fs"))).Should().BeTrue();
        expr.Evaluate(Ctx(("provider", "reg"))).Should().BeFalse();
    }

    [Fact]
    public void Equality_MissingKey_False()
    {
        var expr = WhenExpression.Parse("focus:pane");
        expr.Evaluate(Ctx()).Should().BeFalse();
    }

    [Fact]
    public void Equality_BoolValue()
    {
        var expr = WhenExpression.Parse("modal:true");
        expr.Evaluate(Ctx(("modal", true))).Should().BeTrue();
        expr.Evaluate(Ctx(("modal", false))).Should().BeFalse();
    }

    // ---- Numeric comparisons ---------------------------------------------

    [Fact]
    public void GreaterThan_Number()
    {
        var expr = WhenExpression.Parse("selected.count > 0");
        expr.Evaluate(Ctx(("selected.count", 3))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 0))).Should().BeFalse();
        expr.Evaluate(Ctx(("selected.count", -1))).Should().BeFalse();
    }

    [Fact]
    public void LessThan_Number()
    {
        var expr = WhenExpression.Parse("selected.count < 2");
        expr.Evaluate(Ctx(("selected.count", 1))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 2))).Should().BeFalse();
    }

    [Fact]
    public void GreaterThanOrEqual_Number()
    {
        var expr = WhenExpression.Parse("selected.count >= 1");
        expr.Evaluate(Ctx(("selected.count", 1))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 0))).Should().BeFalse();
    }

    [Fact]
    public void LessThanOrEqual_Number()
    {
        var expr = WhenExpression.Parse("selected.count <= 1");
        expr.Evaluate(Ctx(("selected.count", 1))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 2))).Should().BeFalse();
    }

    [Fact]
    public void NumericComparison_StringNumberCoercion()
    {
        // Value stored as string, compared as number.
        var expr = WhenExpression.Parse("count > 5");
        expr.Evaluate(Ctx(("count", "10"))).Should().BeTrue();
        expr.Evaluate(Ctx(("count", "3"))).Should().BeFalse();
    }

    // ---- Truthy -----------------------------------------------------------

    [Fact]
    public void Truthy_BoolTrue()
    {
        var expr = WhenExpression.Parse("selected.containsArchive");
        expr.Evaluate(Ctx(("selected.containsArchive", true))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.containsArchive", false))).Should().BeFalse();
    }

    [Fact]
    public void Truthy_MissingKey_False()
    {
        var expr = WhenExpression.Parse("missing");
        expr.Evaluate(Ctx()).Should().BeFalse();
    }

    [Fact]
    public void Truthy_IntZero_False()
    {
        var expr = WhenExpression.Parse("count");
        expr.Evaluate(Ctx(("count", 0))).Should().BeFalse();
        expr.Evaluate(Ctx(("count", 5))).Should().BeTrue();
    }

    // ---- Logical operators ------------------------------------------------

    [Fact]
    public void AndOperator_BothTrue()
    {
        var expr = WhenExpression.Parse("selected.count == 1 && selected.allFiles");
        expr.Evaluate(Ctx(("selected.count", 1), ("selected.allFiles", true))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 1), ("selected.allFiles", false))).Should().BeFalse();
        expr.Evaluate(Ctx(("selected.count", 2), ("selected.allFiles", true))).Should().BeFalse();
    }

    [Fact]
    public void OrOperator_EitherTrue()
    {
        var expr = WhenExpression.Parse("focus:pane || focus:tree");
        expr.Evaluate(Ctx(("focus", "pane"))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "tree"))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "console"))).Should().BeFalse();
    }

    [Fact]
    public void NotOperator()
    {
        var expr = WhenExpression.Parse("!modal");
        expr.Evaluate(Ctx(("modal", false))).Should().BeTrue();
        expr.Evaluate(Ctx(("modal", true))).Should().BeFalse();
        expr.Evaluate(Ctx()).Should().BeTrue(); // missing key → false → !false → true
    }

    [Fact]
    public void Parentheses()
    {
        var expr = WhenExpression.Parse("(focus:pane || focus:tree) && !modal");
        expr.Evaluate(Ctx(("focus", "pane"), ("modal", false))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "tree"), ("modal", false))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "pane"), ("modal", true))).Should().BeFalse();
        expr.Evaluate(Ctx(("focus", "console"), ("modal", false))).Should().BeFalse();
    }

    [Fact]
    public void ComplexExpression()
    {
        // selected.count == 1 && selected.allFiles
        var expr = WhenExpression.Parse("selected.count == 1 && selected.allFiles");
        expr.Evaluate(Ctx(("selected.count", 1), ("selected.allFiles", true))).Should().BeTrue();
    }

    [Fact]
    public void Precedence_AndBeforeOr()
    {
        // a || b && c  →  a || (b && c)
        var expr = WhenExpression.Parse("a || b && c");
        expr.Evaluate(Ctx(("a", true), ("b", false), ("c", false))).Should().BeTrue();
        expr.Evaluate(Ctx(("a", false), ("b", true), ("c", true))).Should().BeTrue();
        expr.Evaluate(Ctx(("a", false), ("b", true), ("c", false))).Should().BeFalse();
    }

    // ---- Boolean literals -------------------------------------------------

    [Fact]
    public void TrueLiteral_AlwaysTrue()
    {
        var expr = WhenExpression.Parse("true");
        expr.Evaluate(Ctx()).Should().BeTrue();
    }

    [Fact]
    public void FalseLiteral_AlwaysFalse()
    {
        var expr = WhenExpression.Parse("false");
        expr.Evaluate(Ctx()).Should().BeFalse();
    }

    // ---- Source round-trip ------------------------------------------------

    [Fact]
    public void Source_PreservesOriginalText()
    {
        var expr = WhenExpression.Parse("focus:pane && !modal");
        expr.Source.Should().Be("focus:pane && !modal");
        expr.IsEmpty.Should().BeFalse();
    }

    // ---- Parse errors -----------------------------------------------------

    [Fact]
    public void ParseError_UnterminatedString()
    {
        var act = () => WhenExpression.Parse("provider == \"reg");
        act.Should().Throw<WhenParseException>().WithMessage("Unterminated string literal.");
    }

    [Fact]
    public void ParseError_UnexpectedCharacter()
    {
        var act = () => WhenExpression.Parse("@invalid");
        act.Should().Throw<WhenParseException>();
    }

    [Fact]
    public void ParseError_MissingValueAfterOp()
    {
        var act = () => WhenExpression.Parse("focus:");
        act.Should().Throw<WhenParseException>();
    }

    [Fact]
    public void ParseError_TrailingToken()
    {
        var act = () => WhenExpression.Parse("focus:pane )");
        act.Should().Throw<WhenParseException>();
    }

    [Fact]
    public void ParseError_MissingCloseParen()
    {
        var act = () => WhenExpression.Parse("(focus:pane");
        act.Should().Throw<WhenParseException>();
    }

    // ---- Realistic scenarios ----------------------------------------------

    [Fact]
    public void MenuContext_SelectedCountGreaterThanZero()
    {
        // ADR-0028 §2: selected.count > 0
        var expr = WhenExpression.Parse("selected.count > 0");
        expr.Evaluate(Ctx(("selected.count", 3))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 0))).Should().BeFalse();
    }

    [Fact]
    public void MenuContext_SingleFileSelected()
    {
        // ADR-0028 §2: selected.count == 1 && selected.allFiles
        var expr = WhenExpression.Parse("selected.count == 1 && selected.allFiles");
        expr.Evaluate(Ctx(("selected.count", 1), ("selected.allFiles", true))).Should().BeTrue();
        expr.Evaluate(Ctx(("selected.count", 2), ("selected.allFiles", true))).Should().BeFalse();
        expr.Evaluate(Ctx(("selected.count", 1), ("selected.allFiles", false))).Should().BeFalse();
    }

    [Fact]
    public void MenuContext_RegistryProviderSingleSelection()
    {
        // ADR-0028 §4: provider == "reg" && selected.count == 1
        var expr = WhenExpression.Parse("provider == \"reg\" && selected.count == 1");
        expr.Evaluate(Ctx(("provider", "reg"), ("selected.count", 1))).Should().BeTrue();
        expr.Evaluate(Ctx(("provider", "fs"), ("selected.count", 1))).Should().BeFalse();
    }

    [Fact]
    public void KeyBindingContext_FocusAndProvider()
    {
        // ADR-0027 §2: focus:pane && provider:fs
        var expr = WhenExpression.Parse("focus:pane && provider:fs");
        expr.Evaluate(Ctx(("focus", "pane"), ("provider", "fs"))).Should().BeTrue();
        expr.Evaluate(Ctx(("focus", "pane"), ("provider", "reg"))).Should().BeFalse();
        expr.Evaluate(Ctx(("focus", "tree"), ("provider", "fs"))).Should().BeFalse();
    }
}
