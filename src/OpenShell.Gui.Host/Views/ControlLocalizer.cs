using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using OpenShell.I18n;

namespace OpenShell.Gui.Host.Views;

/// <summary>递归翻译 XAML 中以资源键形式声明的可见字符串，并保留 key 以支持动态切换语言。</summary>
internal static class ControlLocalizer
{
    private static readonly ConditionalWeakTable<AvaloniaObject, LocalizedKeys> s_keys = new();

    public static void Apply(ILogical root, II18nService i18n)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(i18n);

        var visited = new HashSet<ILogical>(ReferenceEqualityComparer.Instance);
        ApplyCore(root, i18n, visited);
    }

    private static void ApplyCore(
        ILogical node,
        II18nService i18n,
        HashSet<ILogical> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is AvaloniaObject avaloniaObject)
        {
            var keys = s_keys.GetOrCreateValue(avaloniaObject);

            if (node is Window window)
            {
                keys.Title ??= AsResourceKey(window.Title);
                if (keys.Title is not null)
                    window.Title = i18n.Translate(keys.Title);
            }

            if (node is TextBlock textBlock)
            {
                keys.Text ??= AsResourceKey(textBlock.Text);
                if (keys.Text is not null)
                    textBlock.Text = i18n.Translate(keys.Text);
            }

            if (node is TextBox textBox)
            {
                keys.Watermark ??= AsResourceKey(textBox.Watermark as string);
                if (keys.Watermark is not null)
                    textBox.Watermark = i18n.Translate(keys.Watermark);
            }

            if (node is ContentControl contentControl)
            {
                keys.Content ??= AsResourceKey(contentControl.Content as string);
                if (keys.Content is not null)
                    contentControl.Content = i18n.Translate(keys.Content);
            }

            if (node is MenuItem menuItem)
            {
                keys.Header ??= AsResourceKey(menuItem.Header as string);
                if (keys.Header is not null)
                    menuItem.Header = i18n.Translate(keys.Header);
            }

            if (node is TreeViewItem treeViewItem)
            {
                keys.Header ??= AsResourceKey(treeViewItem.Header as string);
                if (keys.Header is not null)
                    treeViewItem.Header = i18n.Translate(keys.Header);
            }

            if (node is Control control)
            {
                keys.ToolTip ??= AsResourceKey(ToolTip.GetTip(control) as string);
                if (keys.ToolTip is not null)
                    ToolTip.SetTip(control, i18n.Translate(keys.ToolTip));
            }
        }

        foreach (var child in node.LogicalChildren)
            ApplyCore(child, i18n, visited);
    }

    private static string? AsResourceKey(string? value)
        => value is not null
           && (value.StartsWith("gui.", StringComparison.Ordinal)
               || value.StartsWith("common.", StringComparison.Ordinal))
            ? value
            : null;

    private sealed class LocalizedKeys
    {
        public string? Title { get; set; }
        public string? Text { get; set; }
        public string? Watermark { get; set; }
        public string? Content { get; set; }
        public string? Header { get; set; }
        public string? ToolTip { get; set; }
    }
}
