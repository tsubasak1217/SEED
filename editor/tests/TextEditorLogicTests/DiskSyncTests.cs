using SEEDEditor.Panels.ScriptEditor.DiskSync;
using SpriteRigTests;                // TestHarness / Check（テストランナーは共用）

namespace TextEditorLogicTests;

/// <summary>
/// 「開いているタブをディスクの状態へ追従させる」判定表の単体テスト
/// （docs/editor_script_panel.md の「ディスク追従」節）。
///
/// バージョン管理でブランチを切り替えると、開いているスクリプトがディスク上で
/// 書き換わったり消えたりする。ここで守りたい性質は 3 つ:
///   1. 未保存の編集を、どの経路でも勝手に上書きしない
///   2. 自分の保存で自分を再読み込みしない
///   3. 同じ判定を何度走らせても同じ表示に落ち着く（＝冪等）
/// 監視イベントはブランチ切り替えで何百件も飛ぶため、3 は実用上とても重要。
/// </summary>
public static class DiskSyncTests
{
    /// <summary>このファイルのテストをランナーへ登録する。</summary>
    /// <param name="harness">登録先のテストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 在る × 内容が同じ ──────────────────────────────
        harness.Add("在る・同じ・未保存なし → 何もしない（自分の保存で再読込しない）", ExistsSameClean);
        harness.Add("在る・同じ・未保存あり → 何もしない（通常の編集中）",             ExistsSameDirty);
        harness.Add("在る・同じ → 帯を出していたら畳む",                               ExistsSameClearsNotice);

        // ── 在る × 内容が違う ──────────────────────────────
        harness.Add("在る・違う・未保存なし → 黙って再読み込み",                       ExistsChangedClean);
        harness.Add("在る・違う・未保存あり → 上書きせず帯を出す",                     ExistsChangedDirty);
        harness.Add("帯を出したあとは同じ判定を繰り返しても何もしない",                 ChangedNoticeIsIdempotent);

        // ── 無い ────────────────────────────────────────────
        harness.Add("無い・未保存なし → 「ファイルが見つかりません」へ差し替える",     MissingClean);
        harness.Add("無い・未保存あり → 本文を残して削除の帯を出す",                   MissingDirty);
        harness.Add("「見つかりません」表示は繰り返しても何もしない",                   MissingIsIdempotent);
        harness.Add("削除の帯は繰り返しても何もしない",                                 DeletedNoticeIsIdempotent);

        // ── 戻ってきた ──────────────────────────────────────
        harness.Add("消えていたファイルが戻ったら通常表示へ戻して読み直す",             RestoreOnReturn);
        harness.Add("戻ってきた内容が基準と同じでも表示は戻す",                         RestoreEvenIfSameContent);
        harness.Add("削除の帯を出したまま中身違いで戻ったら、上書きせず変更の帯へ",     DeletedThenReturnedWithEdits);
        harness.Add("クラッシュ復元で未保存になった「見つかりません」タブは読み直さない", MissingWithRecoveredEdits);

        // ── 全体の性質 ──────────────────────────────────────
        harness.Add("未保存の編集がある限り、どの状態からでも本文を上書きしない",       NeverOverwriteUnsavedEdits);
    }

    /// <summary>判定を 1 回実行する小さなヘルパー（呼び出しの意図を読みやすくする）。</summary>
    /// <param name="exists">ディスクにファイルが在るか。</param>
    /// <param name="matches">ディスクの内容が基準と同じか。</param>
    /// <param name="dirty">未保存の編集があるか。</param>
    /// <param name="current">いまの表示状態。</param>
    /// <returns>判定された操作。</returns>
    private static ScriptDiskAction Decide(
        bool exists, bool matches, bool dirty, ScriptDiskStatus current)
        => ScriptDiskState.Decide(new ScriptDiskInputs(exists, matches, dirty, current));

    // ── 在る × 内容が同じ ───────────────────────────────────

    /// <summary>
    /// 自分で保存した直後は「在る・基準と同じ」になる。ここで再読み込みしてしまうと
    /// 保存のたびにキャレットが飛び、Undo 履歴も消える。
    /// </summary>
    private static void ExistsSameClean()
        => Check.Equal(ScriptDiskAction.None,
            Decide(exists: true, matches: true, dirty: false, ScriptDiskStatus.Normal),
            "在る・同じ・未保存なし");

    /// <summary>編集中（ディスクは触られていない）。ごく普通の状態なので何も起きてはいけない。</summary>
    private static void ExistsSameDirty()
        => Check.Equal(ScriptDiskAction.None,
            Decide(exists: true, matches: true, dirty: true, ScriptDiskStatus.Normal),
            "在る・同じ・未保存あり");

    /// <summary>
    /// 帯を出したあとにディスクが元の内容へ戻された（ブランチを戻した等）。
    /// 帯の前提が消えたので畳む。本文は触らない。
    /// </summary>
    private static void ExistsSameClearsNotice()
    {
        Check.Equal(ScriptDiskAction.ClearNotice,
            Decide(exists: true, matches: true, dirty: true, ScriptDiskStatus.ChangedOnDisk),
            "変更の帯 → 内容が戻った");
        Check.Equal(ScriptDiskAction.ClearNotice,
            Decide(exists: true, matches: true, dirty: true, ScriptDiskStatus.DeletedOnDisk),
            "削除の帯 → ファイルが戻った");
    }

    // ── 在る × 内容が違う ───────────────────────────────────

    /// <summary>失うものが無いので黙って読み直す（マージ後にそのまま最新が見える）。</summary>
    private static void ExistsChangedClean()
        => Check.Equal(ScriptDiskAction.Reload,
            Decide(exists: true, matches: false, dirty: false, ScriptDiskStatus.Normal),
            "在る・違う・未保存なし");

    /// <summary>未保存の編集がある。上書きは絶対にしない。</summary>
    private static void ExistsChangedDirty()
        => Check.Equal(ScriptDiskAction.ShowChangedNotice,
            Decide(exists: true, matches: false, dirty: true, ScriptDiskStatus.Normal),
            "在る・違う・未保存あり");

    /// <summary>
    /// 監視イベントはブランチ切り替えで大量に飛ぶ。帯を出したあとに同じ判定が
    /// 何度走っても、帯を作り直さない（＝画面がちらつかない）こと。
    /// </summary>
    private static void ChangedNoticeIsIdempotent()
        => Check.Equal(ScriptDiskAction.None,
            Decide(exists: true, matches: false, dirty: true, ScriptDiskStatus.ChangedOnDisk),
            "変更の帯を出したあとの再判定");

    // ── 無い ─────────────────────────────────────────────────

    /// <summary>古い中身を編集できる状態で残さない（別ブランチのファイルを復活させないため）。</summary>
    private static void MissingClean()
        => Check.Equal(ScriptDiskAction.ShowMissing,
            Decide(exists: false, matches: false, dirty: false, ScriptDiskStatus.Normal),
            "無い・未保存なし");

    /// <summary>未保存の編集は失わせない。本文を残したまま削除だけ知らせる。</summary>
    private static void MissingDirty()
        => Check.Equal(ScriptDiskAction.ShowDeletedNotice,
            Decide(exists: false, matches: false, dirty: true, ScriptDiskStatus.Normal),
            "無い・未保存あり");

    /// <summary>「見つかりません」表示中に再判定しても作り直さない。</summary>
    private static void MissingIsIdempotent()
        => Check.Equal(ScriptDiskAction.None,
            Decide(exists: false, matches: false, dirty: false, ScriptDiskStatus.Missing),
            "見つかりません表示中の再判定");

    /// <summary>削除の帯を出したまま再判定しても作り直さない。</summary>
    private static void DeletedNoticeIsIdempotent()
        => Check.Equal(ScriptDiskAction.None,
            Decide(exists: false, matches: false, dirty: true, ScriptDiskStatus.DeletedOnDisk),
            "削除の帯を出したあとの再判定");

    // ── 戻ってきた ───────────────────────────────────────────

    /// <summary>ブランチを戻した等でファイルが復活したら、自動で通常表示へ戻す。</summary>
    private static void RestoreOnReturn()
        => Check.Equal(ScriptDiskAction.Restore,
            Decide(exists: true, matches: false, dirty: false, ScriptDiskStatus.Missing),
            "見つかりません → ファイルが戻った");

    /// <summary>
    /// 内容が「消える前と同じ」でも、エディタ領域は差し替わったままなので表示を戻す必要がある。
    /// 内容比較だけで判断すると、ここで何も起きず「見つかりません」が残り続ける。
    /// </summary>
    private static void RestoreEvenIfSameContent()
        => Check.Equal(ScriptDiskAction.Restore,
            Decide(exists: true, matches: true, dirty: false, ScriptDiskStatus.Missing),
            "見つかりません → 同じ内容で戻った");

    /// <summary>
    /// 未保存の編集を抱えたまま削除 → 別の内容で復活。
    /// 編集は生きているので上書きせず、「ディスク上で変更されました」へ移る。
    /// </summary>
    private static void DeletedThenReturnedWithEdits()
        => Check.Equal(ScriptDiskAction.ShowChangedNotice,
            Decide(exists: true, matches: false, dirty: true, ScriptDiskStatus.DeletedOnDisk),
            "削除の帯 → 中身違いで復活");

    /// <summary>
    /// クラッシュ復元は「消えたファイルのタブを開いて退避内容を流し込む」経路を通るため、
    /// 「見つかりません」表示 × 未保存あり という組み合わせが一瞬だけ生まれる。
    /// ここで読み直すと復元した編集を握り潰すので、必ず帯で知らせる方へ倒す。
    /// </summary>
    private static void MissingWithRecoveredEdits()
    {
        // ファイルはまだ無い → 削除の帯（本文は残る）
        Check.Equal(ScriptDiskAction.ShowDeletedNotice,
            Decide(exists: false, matches: false, dirty: true, ScriptDiskStatus.Missing),
            "見つかりません × 復元した未保存（ファイルは無いまま）");
        // その後ファイルが別内容で現れた → 上書きせず変更の帯
        Check.Equal(ScriptDiskAction.ShowChangedNotice,
            Decide(exists: true, matches: false, dirty: true, ScriptDiskStatus.Missing),
            "見つかりません × 復元した未保存（ファイルが現れた）");
    }

    // ── 全体の性質 ───────────────────────────────────────────

    /// <summary>
    /// この機能で一番失ってはいけないのは利用者の未保存の編集。
    /// 未保存あり × 全ての表示状態 × ディスクの全パターンを総当たりして、
    /// 本文を差し替える操作（Reload / Restore）が 1 つも出ないことを確かめる。
    /// </summary>
    private static void NeverOverwriteUnsavedEdits()
    {
        var statuses = new[]
        {
            ScriptDiskStatus.Normal,
            ScriptDiskStatus.ChangedOnDisk,
            ScriptDiskStatus.DeletedOnDisk,
            // クラッシュ復元では「見つかりません」タブへ退避内容を流し込むため、
            // この状態でも未保存の編集を持ちうる
            ScriptDiskStatus.Missing,
        };
        foreach (var status in statuses)
        foreach (var exists in new[] { true, false })
        foreach (var matches in new[] { true, false })
        {
            var action = Decide(exists, matches && exists, dirty: true, status);
            Check.True(
                action != ScriptDiskAction.Reload && action != ScriptDiskAction.Restore,
                $"未保存ありで本文を差し替えてはいけない（状態={status} 在る={exists} 同じ={matches} → {action}）");
        }
    }
}
