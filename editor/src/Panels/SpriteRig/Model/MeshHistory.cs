using System;
using System.Collections.Generic;
using SEEDEditor.Panels.SpriteRig.Mesh;

namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグ 1 タブぶんのローカル Undo/Redo スタック（スナップショット方式）。
///
/// 差分コマンドではなくメッシュ全体のスナップショットを積む。
/// 1 枚のスプライトメッシュは頂点数百・三角形数百の規模で、
/// 深さ <see cref="MaxDepth"/> ぶん保持しても数 MB に収まるうえ、
/// 「どの操作を足しても Undo が壊れない」ことが構造的に保証されるため。
///
/// エディタ全体の Undo（シーン編集）とは独立していて、
/// パネルにフォーカスがある間だけ Ctrl+Z / Ctrl+Y がこちらへ流れる。
/// </summary>
public sealed class MeshHistory
{
    /// <summary>保持するスナップショットの最大数（これを超えると古いものから捨てる）。</summary>
    public const int MaxDepth = 64;

    /// <summary>Undo で戻れる過去のスナップショット（末尾が直近）。</summary>
    private readonly List<Snapshot> _undoStack = new();

    /// <summary>Redo で進めるスナップショット（末尾が直近）。</summary>
    private readonly List<Snapshot> _redoStack = new();

    /// <summary>Undo できるか。</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Redo できるか。</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>直近の Undo 操作名（無ければ空文字列）。</summary>
    public string UndoLabel => CanUndo ? _undoStack[^1].Label : string.Empty;

    /// <summary>直近の Redo 操作名（無ければ空文字列）。</summary>
    public string RedoLabel => CanRedo ? _redoStack[^1].Label : string.Empty;

    /// <summary>1 件ぶんのスナップショット。</summary>
    /// <param name="Label">操作名（UI のツールチップ表示用）。</param>
    /// <param name="Mesh">操作<b>前</b>のメッシュの深いコピー。</param>
    private sealed record Snapshot(string Label, SpriteRigMesh Mesh);

    /// <summary>
    /// これから行う操作の直前状態を記録する。操作を実行する<b>前</b>に呼ぶ。
    /// </summary>
    /// <param name="label">操作名（「自動メッシュ生成」「頂点移動」など）。</param>
    /// <param name="current">現在のメッシュ（深いコピーが取られる）。</param>
    public void Push(string label, SpriteRigMesh current)
    {
        _undoStack.Add(new Snapshot(label, current.Clone()));
        // 新しい操作を積んだ時点で Redo 系列は無効になる
        _redoStack.Clear();

        if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
    }

    /// <summary>
    /// 直前の状態へ戻す。
    /// </summary>
    /// <param name="current">現在のメッシュ（Redo 用に退避される）。</param>
    /// <returns>復元されたメッシュ。戻せない場合は null。</returns>
    public SpriteRigMesh? Undo(SpriteRigMesh current)
    {
        if (!CanUndo) return null;
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(new Snapshot(snapshot.Label, current.Clone()));
        return snapshot.Mesh;
    }

    /// <summary>
    /// Undo で戻した操作をやり直す。
    /// </summary>
    /// <param name="current">現在のメッシュ（Undo 用に退避される）。</param>
    /// <returns>復元されたメッシュ。やり直せない場合は null。</returns>
    public SpriteRigMesh? Redo(SpriteRigMesh current)
    {
        if (!CanRedo) return null;
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(new Snapshot(snapshot.Label, current.Clone()));
        return snapshot.Mesh;
    }

    /// <summary>履歴を全消去する（新しい画像を読み込んだときなど）。</summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
