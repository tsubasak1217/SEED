// ============================================================
//  SceneSnapshotWire.cs — シーンの写し（SNAPSHOT_SCENE）と写しの閲覧（SNAPSHOT_VIEW_*）の IPC の文字列（純粋な処理）
//
//  書式の正典はランタイムの runtime/src/engine/core/app_base/scene_snapshot/wire.rs。値を変えるときは両方を直す
//  （PC の名前付きパイプと Android の TCP で同じ 1 行 1 命令。docs/android.md §20.17・§21.13）。
//
//  【写しを書き出す（Play 中のランタイム。端末のアプリ・PC の Play）】
//    SNAPSHOT_SCENE:<ランタイム側の絶対パス（.scene）>
//      → SNAPSHOT_DONE:<パス>|actors=<書いたアクタ数（子を含む）>|skipped=<飛ばしたアクタ数>|ms=<ランタイムでの所要ミリ秒>|cam=<px>,<py>,<pz>,<ex>,<ey>,<ez>
//        （cam はメインカメラの位置と YXZ オイラー角〈度〉。メインカメラが無ければ cam=none）
//      → SNAPSHOT_FAILED:<パス>|<理由>
//
//  【写しを閲覧専用で表示する（エディタの編集用ランタイム。Edit のときだけ）】
//    SNAPSHOT_VIEW_BEGIN:<PC の写しのパス>[|file_ok]
//      編集中のシーンをメモリへ退避してから（地形の実データ・編集タブ・Undo 履歴・ツール）写しを読み込み、閲覧専用にする。
//      file_ok は「メモリへ退避できなければ、戻すときに保存済みのファイルを読み直してよい」（未保存の変更が無いときだけ付ける）。
//      → SNAPSHOT_VIEW_READY:<パス>|actors=<読み込んだアクタ数>|ms=<所要ミリ秒>
//      → SNAPSHOT_VIEW_FAILED:<パス>|<段階: mode / load / stash>|<理由>（何も変えていない）
//    SNAPSHOT_VIEW_END
//      → SNAPSHOT_VIEW_ENDED:<戻し方: memory / file / none / failed>|<戻したシーンのパス、または理由>|ms=<所要ミリ秒>
//    表示中に届いた編集・保存の命令は捨て、SNAPSHOT_VIEW_REFUSED:<命令の名前> を返す（保存は加えて SAVE_ERROR:snapshot_view）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.SceneSnapshot;

/// <summary>シーンの写しと写しの閲覧の IPC の文字列。</summary>
public static class SceneSnapshotWire
{
    // ── 写しを書き出す（Play 中のランタイム）──────────────────────

    /// <summary>写しを書き出す命令の接頭辞（SNAPSHOT_SCENE:{ランタイム側の絶対パス}）。</summary>
    public const string SnapshotScenePrefix = "SNAPSHOT_SCENE:";

    /// <summary>写しを書き出した応答の接頭辞。</summary>
    public const string SnapshotDonePrefix = "SNAPSHOT_DONE:";

    /// <summary>写しを書き出せなかった応答の接頭辞。</summary>
    public const string SnapshotFailedPrefix = "SNAPSHOT_FAILED:";

    // ── 写しを閲覧専用で表示する（エディタの編集用ランタイム）───────────

    /// <summary>写しの表示を始める命令の接頭辞（SNAPSHOT_VIEW_BEGIN:{PC の写しのパス}[|file_ok]）。</summary>
    public const string ViewBeginPrefix = "SNAPSHOT_VIEW_BEGIN:";

    /// <summary>写しの表示をやめて編集中のシーンへ戻す命令。</summary>
    public const string ViewEnd = "SNAPSHOT_VIEW_END";

    /// <summary>写しを表示した応答の接頭辞。</summary>
    public const string ViewReadyPrefix = "SNAPSHOT_VIEW_READY:";

    /// <summary>写しを表示できなかった応答の接頭辞（何も変えていない）。</summary>
    public const string ViewFailedPrefix = "SNAPSHOT_VIEW_FAILED:";

    /// <summary>写しの表示をやめた応答の接頭辞。</summary>
    public const string ViewEndedPrefix = "SNAPSHOT_VIEW_ENDED:";

    /// <summary>表示中に編集・保存の命令を捨てた知らせの接頭辞（SNAPSHOT_VIEW_REFUSED:{命令の名前}）。</summary>
    public const string ViewRefusedPrefix = "SNAPSHOT_VIEW_REFUSED:";

    /// <summary>「メモリへ退避できなければファイルを読み直して戻してよい」の印（SNAPSHOT_VIEW_BEGIN の 2 つ目の欄）。</summary>
    public const string FileRestoreOption = "file_ok";

    // ── 欄 ─────────────────────────────────────────────────

    /// <summary>欄の区切り（Windows のファイル名に使えない文字なのでパスと混ざらない。差し替えの応答と同じ）。</summary>
    public const char FieldSeparator = '|';

    /// <summary>名前付きの欄の名前と値の区切り（actors=12）。</summary>
    public const char KeyValueSeparator = '=';

    /// <summary>欄: アクタの数（子を含む）。</summary>
    public const string ActorsKey = "actors";

    /// <summary>欄: 飛ばしたアクタの数（子を含む。保存できなかったもの）。</summary>
    public const string SkippedKey = "skipped";

    /// <summary>欄: ランタイムでの所要ミリ秒。</summary>
    public const string MillisecondsKey = "ms";

    /// <summary>欄: メインカメラの位置と向き（SceneSnapshotCameraPose の書式。無ければ none）。</summary>
    public const string CameraKey = "cam";

    // ── SNAPSHOT_VIEW_FAILED の段階 ─────────────────────────────

    /// <summary>段階: 編集用ランタイムが Edit でない（Play 中等）。</summary>
    public const string StageMode = "mode";

    /// <summary>段階: 写しのファイルを読めない。</summary>
    public const string StageLoad = "load";

    /// <summary>段階: 編集中のシーンをメモリへ退避できない（未保存の変更があるなら保存を促す）。</summary>
    public const string StageStash = "stash";

    // ── SNAPSHOT_VIEW_ENDED の戻し方 ───────────────────────────

    /// <summary>メモリへ退避した編集中のシーンへ戻した（未保存の変更も戻る）。</summary>
    public const string RestoredFromMemory = "memory";

    /// <summary>保存済みのシーンのファイルを読み直した（メモリへ退避できなかった・戻せなかった）。</summary>
    public const string RestoredFromFile = "file";

    /// <summary>表示していなかった（何もしていない）。</summary>
    public const string NothingToRestore = "none";

    /// <summary>戻せなかった（写しが残っている。ランタイムの保存の突き合わせが元のシーンへの上書きを弾く）。</summary>
    public const string RestoreFailed = "failed";

    // ── 命令を作る ─────────────────────────────────────────

    /// <summary>写しを書き出す命令を作る。</summary>
    /// <param name="runtimePath">ランタイムが書く絶対パス（.scene。区切りの | を含めない）。</param>
    /// <returns>1 行の命令。</returns>
    public static string SnapshotScene(string runtimePath) => SnapshotScenePrefix + runtimePath;

    /// <summary>写しの表示を始める命令を作る。</summary>
    /// <param name="snapshotPath">PC の写しのパス（区切りの | を含めない）。</param>
    /// <param name="allowFileRestore">メモリへ退避できなければファイルの読み直しで戻してよいか（未保存の変更が無いときだけ true）。</param>
    /// <returns>1 行の命令。</returns>
    public static string ViewBegin(string snapshotPath, bool allowFileRestore) =>
        allowFileRestore
            ? ViewBeginPrefix + snapshotPath + FieldSeparator + FileRestoreOption
            : ViewBeginPrefix + snapshotPath;

    // ── 応答の見分け ───────────────────────────────────────

    /// <summary>写しの書き出しの応答（成功・失敗）か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>応答なら true。</returns>
    public static bool IsSnapshotReply(string line) =>
        line.StartsWith(SnapshotDonePrefix, StringComparison.Ordinal)
        || line.StartsWith(SnapshotFailedPrefix, StringComparison.Ordinal);

    /// <summary>写しの表示を始めた応答（成功・失敗）か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>応答なら true。</returns>
    public static bool IsViewBeginReply(string line) =>
        line.StartsWith(ViewReadyPrefix, StringComparison.Ordinal)
        || line.StartsWith(ViewFailedPrefix, StringComparison.Ordinal);

    /// <summary>写しの表示をやめた応答か。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>応答なら true。</returns>
    public static bool IsViewEndReply(string line) => line.StartsWith(ViewEndedPrefix, StringComparison.Ordinal);

    /// <summary>表示中に命令を捨てた知らせか。</summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>知らせなら true。</returns>
    public static bool IsViewRefused(string line) => line.StartsWith(ViewRefusedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 「名前=値」の欄から値を取り出す（純粋な処理）。無ければ null。
    /// </summary>
    /// <param name="fields">欄（区切りで分けたもの）。</param>
    /// <param name="key">名前。</param>
    /// <returns>値（無ければ null）。</returns>
    public static string? FindValue(ReadOnlySpan<string> fields, string key)
    {
        foreach (var field in fields)
        {
            var separator = field.IndexOf(KeyValueSeparator);
            if (separator <= 0) continue;
            if (string.Equals(field[..separator].Trim(), key, StringComparison.Ordinal)) return field[(separator + 1)..].Trim();
        }
        return null;
    }
}
