namespace OpenShell.Clipboard;

/// <summary>
/// 拖拽效果位标志。Per ADR-0029 §5.
/// </summary>
[Flags]
public enum DragDropEffects
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
    Delete = 8,
}
