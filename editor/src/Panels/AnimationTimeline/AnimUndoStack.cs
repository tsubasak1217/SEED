// ============================================================
//  AnimUndoStack.cs — クリップ編集の Undo / Redo（純ロジック）
//
//  タイムライン内の編集（キー追加・移動・削除・値変更・fps/duration 変更）を
//  「クリップ全体の JSON スナップショット」単位で巻き戻す簡易スタック。
//
//  【なぜ差分ではなくスナップショットか】
//   .anim は数 KB〜数十 KB 程度の小さなアセットで、編集操作の種類は多い。
//   操作ごとに逆操作を実装するより、AnimClipIO.Serialize / Parse の
//   ラウンドトリップ（.anim の保存経路そのもの）を再利用するほうが
//   実装量・破綻リスクとも小さい。
//
//  【エディタ全体の Undo との関係】
//   本スタックはパネル内で閉じている（シーンの Undo とは独立）。
//   .anim はシーンとは別アセットであり、保存も明示操作のため干渉しない。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>クリップ JSON のスナップショットによる Undo / Redo スタック。</summary>
internal sealed class AnimUndoStack
{
    /// <summary>保持するスナップショットの最大数（古いものから捨てる）。</summary>
    public const int DefaultCapacity = 64;

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private readonly int          _capacity;

    /// <summary>直近に確定したスナップショット（= 現在の状態）。未初期化なら null。</summary>
    private string? _current;

    /// <param name="capacity">履歴の最大段数（0 以下は既定値）。</param>
    public AnimUndoStack(int capacity = DefaultCapacity)
        => _capacity = capacity > 0 ? capacity : DefaultCapacity;

    /// <summary>Undo できるか。</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Redo できるか。</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// クリップを差し替えた（別ファイルを開いた・新規作成した）ときに履歴を捨てて基準を張り直す。
    /// </summary>
    public void Reset(string snapshot)
    {
        _undo.Clear();
        _redo.Clear();
        _current = snapshot;
    }

    /// <summary>
    /// 編集**後**のスナップショットを積む。
    ///
    /// 呼び出し規約: 編集前の状態は _current が保持しているため、呼び出し側は
    /// 「モデルを書き換えた直後に 1 回呼ぶ」だけでよい。編集前に積む方式より
    /// 呼び忘れの穴が小さいのでこちらを採用する。
    /// 内容が変わっていなければ履歴を汚さない。
    /// </summary>
    public void Push(string newSnapshot)
    {
        if (_current is null) { _current = newSnapshot; return; }
        if (_current == newSnapshot) return;

        _undo.Add(_current);
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _redo.Clear();                          // 新しい編集で Redo 系列は無効になる
        _current = newSnapshot;
    }

    /// <summary>1 つ戻す。戻せない場合は null。</summary>
    public string? Undo()
    {
        if (_undo.Count == 0 || _current is null) return null;
        var prev = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(_current);
        _current = prev;
        return prev;
    }

    /// <summary>1 つ進める。進められない場合は null。</summary>
    public string? Redo()
    {
        if (_redo.Count == 0 || _current is null) return null;
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(_current);
        _current = next;
        return next;
    }
}
