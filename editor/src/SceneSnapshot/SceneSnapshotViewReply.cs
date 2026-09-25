// ============================================================
//  SceneSnapshotViewReply.cs — 写しの閲覧（SNAPSHOT_VIEW_BEGIN / END）の応答を読む（純粋な処理）
//
//    SNAPSHOT_VIEW_READY:<パス>|actors=<n>|ms=<t>                     … 表示した
//    SNAPSHOT_VIEW_FAILED:<パス>|<段階: mode / load / stash>|<理由>   … 表示できなかった（何も変えていない）
//    SNAPSHOT_VIEW_ENDED:<戻し方: memory / file / none / failed>|<戻したシーンのパス、または理由>|ms=<t>
//  書式の説明は SceneSnapshotWire.cs、正典はランタイムの scene_snapshot/wire.rs。
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.SceneSnapshot;

/// <summary>写しの表示を始めた応答の結果。</summary>
public enum SceneSnapshotViewBeginOutcome
{
    /// <summary>表示した。</summary>
    Ready,

    /// <summary>編集用ランタイムが Edit でない（Play 中等）。</summary>
    WrongMode,

    /// <summary>写しのファイルを読めない。</summary>
    LoadFailed,

    /// <summary>編集中のシーンをメモリへ退避できない（未保存の変更があるなら保存を促す）。</summary>
    StashFailed,

    /// <summary>応答の書式が違う・知らない段階。</summary>
    Malformed,
}

/// <summary>写しの表示を始めた応答。</summary>
/// <param name="Outcome">結果。</param>
/// <param name="Path">写しのパス。</param>
/// <param name="Actors">読み込んだアクタの数（表示したときだけ。それ以外は 0）。</param>
/// <param name="RuntimeMilliseconds">ランタイムでの所要ミリ秒（表示したときだけ。それ以外は 0）。</param>
/// <param name="Reason">表示できなかった理由（表示したら null）。</param>
public sealed record SceneSnapshotViewBeginReply(
    SceneSnapshotViewBeginOutcome Outcome, string Path, int Actors, double RuntimeMilliseconds, string? Reason)
{
    /// <summary>表示したか。</summary>
    public bool IsReady => Outcome == SceneSnapshotViewBeginOutcome.Ready;

    /// <summary>
    /// 応答を読む（READY / FAILED 以外・書式違いは Malformed）。
    /// </summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>読んだ応答。</returns>
    public static SceneSnapshotViewBeginReply Parse(string line)
    {
        if (line.StartsWith(SceneSnapshotWire.ViewReadyPrefix, StringComparison.Ordinal))
        {
            var fields = line[SceneSnapshotWire.ViewReadyPrefix.Length..].Split(SceneSnapshotWire.FieldSeparator);
            var named = fields.AsSpan(1);
            int.TryParse(SceneSnapshotWire.FindValue(named, SceneSnapshotWire.ActorsKey), NumberStyles.None,
                CultureInfo.InvariantCulture, out var actors);
            double.TryParse(SceneSnapshotWire.FindValue(named, SceneSnapshotWire.MillisecondsKey), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var milliseconds);
            return new SceneSnapshotViewBeginReply(SceneSnapshotViewBeginOutcome.Ready, fields[0].Trim(), actors, milliseconds, null);
        }
        if (line.StartsWith(SceneSnapshotWire.ViewFailedPrefix, StringComparison.Ordinal))
        {
            // <パス>|<段階>|<理由>（理由に | が入っても残りを全部つなぐ）
            var fields = line[SceneSnapshotWire.ViewFailedPrefix.Length..].Split(SceneSnapshotWire.FieldSeparator, 3);
            var path = fields[0].Trim();
            var stage = fields.Length > 1 ? fields[1].Trim() : string.Empty;
            var reason = fields.Length > 2 ? fields[2].Trim() : string.Empty;
            var outcome = stage switch
            {
                SceneSnapshotWire.StageMode => SceneSnapshotViewBeginOutcome.WrongMode,
                SceneSnapshotWire.StageLoad => SceneSnapshotViewBeginOutcome.LoadFailed,
                SceneSnapshotWire.StageStash => SceneSnapshotViewBeginOutcome.StashFailed,
                _ => SceneSnapshotViewBeginOutcome.Malformed,
            };
            return new SceneSnapshotViewBeginReply(outcome, path, 0, 0, reason.Length > 0 ? reason : line);
        }
        return new SceneSnapshotViewBeginReply(SceneSnapshotViewBeginOutcome.Malformed, string.Empty, 0, 0, line);
    }
}

/// <summary>写しの表示をやめたときの戻し方。</summary>
public enum SceneSnapshotRestoreKind
{
    /// <summary>メモリへ退避した編集中のシーンへ戻した（未保存の変更も戻る）。</summary>
    Memory,

    /// <summary>保存済みのファイルを読み直した（メモリの退避を使えなかった）。</summary>
    File,

    /// <summary>表示していなかった。</summary>
    None,

    /// <summary>戻せなかった（写しが残っている）。</summary>
    Failed,
}

/// <summary>写しの表示をやめた応答。</summary>
/// <param name="Restore">戻し方。</param>
/// <param name="Detail">戻したシーンのパス（Memory / File）か理由（Failed）。</param>
/// <param name="RuntimeMilliseconds">ランタイムでの所要ミリ秒。</param>
public sealed record SceneSnapshotViewEndReply(SceneSnapshotRestoreKind Restore, string Detail, double RuntimeMilliseconds)
{
    /// <summary>
    /// 応答を読む（ENDED でない・知らない戻し方は Failed として理由に行を入れる）。
    /// </summary>
    /// <param name="line">受け取った行。</param>
    /// <returns>読んだ応答。</returns>
    public static SceneSnapshotViewEndReply Parse(string line)
    {
        if (!line.StartsWith(SceneSnapshotWire.ViewEndedPrefix, StringComparison.Ordinal))
        {
            return new SceneSnapshotViewEndReply(SceneSnapshotRestoreKind.Failed, line, 0);
        }
        var fields = line[SceneSnapshotWire.ViewEndedPrefix.Length..].Split(SceneSnapshotWire.FieldSeparator);
        var kind = fields[0].Trim() switch
        {
            SceneSnapshotWire.RestoredFromMemory => SceneSnapshotRestoreKind.Memory,
            SceneSnapshotWire.RestoredFromFile => SceneSnapshotRestoreKind.File,
            SceneSnapshotWire.NothingToRestore => SceneSnapshotRestoreKind.None,
            SceneSnapshotWire.RestoreFailed => SceneSnapshotRestoreKind.Failed,
            _ => (SceneSnapshotRestoreKind?)null,
        };
        if (kind is null) return new SceneSnapshotViewEndReply(SceneSnapshotRestoreKind.Failed, line, 0);
        var detail = fields.Length > 1 ? fields[1].Trim() : string.Empty;
        double.TryParse(SceneSnapshotWire.FindValue(fields.AsSpan(1), SceneSnapshotWire.MillisecondsKey), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var milliseconds);
        return new SceneSnapshotViewEndReply(kind.Value, detail, milliseconds);
    }
}
