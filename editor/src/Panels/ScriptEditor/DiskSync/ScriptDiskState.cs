using System;

namespace SEEDEditor.Panels.ScriptEditor.DiskSync;

/// <summary>
/// 開いているタブが、いまディスクに対してどういう状態として表示されているか。
///
/// 「次に何をすべきか」を決めるための入力であると同時に、
/// 同じ判定が何度走っても同じ表示に落ち着く（＝冪等になる）ための現在値でもある。
/// バージョン管理でブランチを切り替えると監視イベントが大量に飛んでくるため、
/// 「もう出している帯をもう一度出さない」ことが実用上とても重要になる。
/// </summary>
public enum ScriptDiskStatus
{
    /// <summary>通常表示。ディスクと食い違っていない（または食い違いを利用者が了承済み）。</summary>
    Normal,

    /// <summary>「ディスク上で変更されました」の帯を出している（未保存の編集があるので上書きしない）。</summary>
    ChangedOnDisk,

    /// <summary>「ディスク上から削除されました」の帯を出している（未保存の編集を抱えたまま）。</summary>
    DeletedOnDisk,

    /// <summary>エディタ領域を「ファイルが見つかりません」表示へ差し替えている。</summary>
    Missing,
}

/// <summary>
/// ディスクの状態を点検した結果、タブに対して行うべき操作。
/// 表示の切り替えと本文の読み直しを呼び出し側（パネル）が行う。
/// </summary>
public enum ScriptDiskAction
{
    /// <summary>何もしない（自分の保存直後や、すでに正しい表示になっている場合）。</summary>
    None,

    /// <summary>黙って再読み込みする（未保存の編集が無いので失うものが無い）。</summary>
    Reload,

    /// <summary>消えていたファイルが戻ってきたので、通常表示へ戻して読み直す。</summary>
    Restore,

    /// <summary>「ディスク上で変更されました」の帯を出す（本文は上書きしない）。</summary>
    ShowChangedNotice,

    /// <summary>「ディスク上から削除されました」の帯を出す（本文はそのまま保持する）。</summary>
    ShowDeletedNotice,

    /// <summary>エディタ領域を「ファイルが見つかりません」表示へ差し替える。</summary>
    ShowMissing,

    /// <summary>帯を出す理由が消えたので、帯を畳んで通常表示へ戻す（本文は触らない）。</summary>
    ClearNotice,
}

/// <summary>
/// ディスク点検 1 回分の入力。
/// </summary>
/// <param name="ExistsOnDisk">ファイルがディスク上に在るか。</param>
/// <param name="DiskMatchesBaseline">
/// ディスクの内容が、このタブが「最後に読み書きした内容」と同じか。
/// <see cref="ExistsOnDisk"/> が false のときは意味を持たない（false を渡す）。
/// 自分の保存で再読み込みしないための鍵になる値で、保存時にも基準値を更新する。
/// </param>
/// <param name="HasUnsavedEdits">タブに未保存の編集があるか。</param>
/// <param name="Current">いまタブが取っている表示状態。</param>
public readonly record struct ScriptDiskInputs(
    bool             ExistsOnDisk,
    bool             DiskMatchesBaseline,
    bool             HasUnsavedEdits,
    ScriptDiskStatus Current);

/// <summary>
/// 「開いているタブをディスクの状態へ追従させる」判定表。
///
/// WPF にも AvalonEdit にも依存しない純粋関数だけを置く（単体テストの対象）。
/// 判定の正典は docs/editor_script_panel.md の「ディスク追従」節。
///
/// 判定の骨子（［有無］×［内容が基準と同じか］×［未保存の編集］）:
/// <list type="bullet">
///   <item>在る・同じ　　　　　　→ 何もしない（＝自分の保存で再読み込みしない）</item>
///   <item>在る・違う・未保存なし→ 黙って再読み込み</item>
///   <item>在る・違う・未保存あり→ 上書きせず帯で知らせる</item>
///   <item>無い・未保存なし　　　→「ファイルが見つかりません」表示へ差し替える</item>
///   <item>無い・未保存あり　　　→ 本文は保持し、帯で削除を知らせる</item>
///   <item>消えていたものが戻った→ 通常表示へ戻して読み直す</item>
/// </list>
/// </summary>
public static class ScriptDiskState
{
    /// <summary>
    /// 現在の状態とディスクの状態から、タブに対して行うべき操作を 1 つ決める。
    /// 同じ入力で何度呼んでも同じ結果になり、適用後にもう一度呼べば
    /// <see cref="ScriptDiskAction.None"/> へ落ち着く（冪等）。
    /// </summary>
    /// <param name="inputs">点検 1 回分の入力。</param>
    /// <returns>行うべき操作。</returns>
    public static ScriptDiskAction Decide(in ScriptDiskInputs inputs)
    {
        // ── ファイルがディスク上に無い ──────────────────────────
        if (!inputs.ExistsOnDisk)
        {
            // 未保存の編集は絶対に失わせない。本文はそのまま置いて帯だけ出す。
            if (inputs.HasUnsavedEdits)
                return inputs.Current == ScriptDiskStatus.DeletedOnDisk
                    ? ScriptDiskAction.None
                    : ScriptDiskAction.ShowDeletedNotice;

            // 失うものが無いので「ファイルが見つかりません」表示へ差し替える。
            return inputs.Current == ScriptDiskStatus.Missing
                ? ScriptDiskAction.None
                : ScriptDiskAction.ShowMissing;
        }

        // ── 消えていたファイルが戻ってきた ──────────────────────
        // 内容が基準と同じでも表示は戻す必要があるため、内容比較より先に判定する。
        //
        // 「見つかりません」表示中は編集できないので、通常は未保存の編集を持たない。
        // ただしクラッシュ復元は「消えたファイルのタブへ退避内容を流し込む」ため、
        // 一瞬だけこの組み合わせが起きる。そのときは読み直さず下の判定へ落として
        // 帯で知らせる（復元した編集を握り潰さないため）。
        if (inputs.Current == ScriptDiskStatus.Missing && !inputs.HasUnsavedEdits)
            return ScriptDiskAction.Restore;

        // ── ディスクの内容が「最後に読み書きした内容」と同じ ────
        if (inputs.DiskMatchesBaseline)
            // 自分の保存やタイムスタンプだけの更新。通常表示なら本当に何もしない。
            // 帯を出していたなら、その前提（差分・削除）が消えたので畳む。
            return inputs.Current == ScriptDiskStatus.Normal
                ? ScriptDiskAction.None
                : ScriptDiskAction.ClearNotice;

        // ── ディスクの内容が違う ────────────────────────────────
        // 未保存の編集があるなら上書きしない。判断は利用者に委ねる。
        if (inputs.HasUnsavedEdits)
            return inputs.Current == ScriptDiskStatus.ChangedOnDisk
                ? ScriptDiskAction.None
                : ScriptDiskAction.ShowChangedNotice;

        // 失うものが無いので黙って読み直す。
        return ScriptDiskAction.Reload;
    }
}
