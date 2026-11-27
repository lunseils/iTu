namespace iTu.Core;

/// <summary>撤销 / 重做栈：保存标注快照，交互简单可靠。</summary>
public sealed class AnnotationHistory
{
    private readonly List<List<Annotation>> _undo = new();
    private readonly List<List<Annotation>> _redo = new();

    public IReadOnlyList<Annotation> Current { get; private set; } = new List<Annotation>();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>释放一组标注的懒加载 GDI 缓存（Clone 不共享这些缓存，可安全释放）。</summary>
    private static void Release(IEnumerable<Annotation> items)
    {
        foreach (var a in items) a.ReleaseCachedResources();
    }

    /// <summary>提交一次修改（调用前先 Undo 前状态已入栈）。</summary>
    public void Push(IReadOnlyList<Annotation> next)
    {
        _undo.Add(Current.Select(a => a.Clone()).ToList());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        Release(Current);   // 旧 Current 已 Clone 入栈，其 GDI 缓存不再需要
        Current = next.Select(a => a.Clone()).ToList();
    }

    public bool Undo()
    {
        if (!CanUndo) return false;
        _redo.Add(Current.Select(a => a.Clone()).ToList());
        Release(Current);
        Current = _undo[^1].Select(a => a.Clone()).ToList();
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo) return false;
        _undo.Add(Current.Select(a => a.Clone()).ToList());
        Release(Current);
        Current = _redo[^1].Select(a => a.Clone()).ToList();
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }
}
