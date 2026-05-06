using Rawr.Core.Models;

namespace Rawr.App.Services;

/// <summary>
/// One reversible metadata edit (rating, flag, color label, or tag toggle).
/// Apply runs the forward edit; Revert undoes it. Photo is the item the edit
/// targets, so the host can re-select it on undo/redo to make the change visible.
/// </summary>
public sealed record EditOp(
    string Description,
    PhotoItem Photo,
    Action Apply,
    Action Revert);

/// <summary>
/// Bounded undo/redo stack for per-photo metadata edits. Records each edit
/// after the VM has already applied the new value; Undo/Redo replay the
/// stored delegates and bubble a Changed event so commands can re-evaluate
/// CanExecute. Recording a new edit drops the redo branch.
/// </summary>
public sealed class EditHistory
{
    private const int MaxDepth = 100;

    private readonly Stack<EditOp> _undo = new();
    private readonly Stack<EditOp> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo.Peek().Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public event EventHandler? Changed;

    public void Record(EditOp op)
    {
        _undo.Push(op);
        _redo.Clear();
        TrimToDepth(_undo);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public EditOp? Undo()
    {
        if (_undo.Count == 0) return null;
        var op = _undo.Pop();
        op.Revert();
        _redo.Push(op);
        Changed?.Invoke(this, EventArgs.Empty);
        return op;
    }

    public EditOp? Redo()
    {
        if (_redo.Count == 0) return null;
        var op = _redo.Pop();
        op.Apply();
        _undo.Push(op);
        Changed?.Invoke(this, EventArgs.Empty);
        return op;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void TrimToDepth(Stack<EditOp> stack)
    {
        if (stack.Count <= MaxDepth) return;
        // Stack.ToArray returns top-first; keep the most recent MaxDepth entries.
        var newest = stack.ToArray();
        stack.Clear();
        for (int i = MaxDepth - 1; i >= 0; i--) stack.Push(newest[i]);
    }
}
