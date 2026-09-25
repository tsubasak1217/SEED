// ============================================================
//  SceneSnapshotReply.cs — 写しの書き出し（SNAPSHOT_SCENE）の応答を読む（純粋な処理）
//
//    SNAPSHOT_DONE:<パス>|actors=<n>|skipped=<k>|ms=<t>|cam=<px>,<py>,<pz>,<ex>,<ey>,<ez>  … 書き出した
//    SNAPSHOT_FAILED:<パス>|<理由>                                                        … 書き出せなかった
//  書式の説明は SceneSnapshotWire.cs、正典はランタイムの scene_snapshot/wire.rs。
//  名前付きの欄は順番に依らず読み、知らない欄は読み飛ばす（ランタイムが欄を足しても古いエディタが壊れない）。
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.SceneSnapshot;

/// <summary>写しを書き出した応答（SNAPSHOT_DONE）の中身。</summary>
/// <param name="RuntimePath">ランタイムが書いたパス（端末・PC のランタイム側の絶対パス）。</param>
/// <param name="Actors">書いたアクタの数（子を含む）。</param>
/// <param name="Skipped">保存できずに飛ばしたアクタの数（子を含む）。</param>
/// <param name="RuntimeMilliseconds">ランタイムでの所要ミリ秒（集める・直列化・書き込み）。</param>
/// <param name="Camera">メインカメラの位置と向き（メインカメラが無ければ null）。</param>
public sealed record SceneSnapshotReply(
    string RuntimePath, int Actors, int Skipped, double RuntimeMilliseconds, SceneSnapshotCameraPose? Camera)
{
    /// <summary>応答の書式が違うときの理由の書式（{0}=受け取った行）。</summary>
    private const string MalformedReasonFormat = "写しの応答を読めません: {0}";

    /// <summary>失敗の応答に理由が無いときの理由。</summary>
    private const string UnknownFailureReason = "理由が返りませんでした";

    /// <summary>
    /// 応答を読む。
    /// </summary>
    /// <param name="line">受け取った行（SNAPSHOT_DONE: / SNAPSHOT_FAILED:）。</param>
    /// <param name="reply">書き出せたときの中身（失敗・読めないときは null）。</param>
    /// <param name="failureReason">書き出せなかった・読めないときの理由（成功なら null）。</param>
    /// <returns>書き出せていれば true。</returns>
    public static bool TryParse(string line, out SceneSnapshotReply? reply, out string? failureReason)
    {
        reply = null;
        failureReason = null;
        if (line.StartsWith(SceneSnapshotWire.SnapshotFailedPrefix, StringComparison.Ordinal))
        {
            // 失敗: パスの後ろの欄をそのまま理由にする（理由に | は入れない約束だが、入っても残りを全部つなぐ）
            var body = line[SceneSnapshotWire.SnapshotFailedPrefix.Length..];
            var separator = body.IndexOf(SceneSnapshotWire.FieldSeparator);
            var reason = separator >= 0 ? body[(separator + 1)..].Trim() : string.Empty;
            failureReason = reason.Length > 0 ? reason : UnknownFailureReason;
            return false;
        }
        if (!line.StartsWith(SceneSnapshotWire.SnapshotDonePrefix, StringComparison.Ordinal))
        {
            failureReason = string.Format(MalformedReasonFormat, line);
            return false;
        }

        var fields = line[SceneSnapshotWire.SnapshotDonePrefix.Length..].Split(SceneSnapshotWire.FieldSeparator);
        var path = fields[0].Trim();
        var named = fields.AsSpan(1);
        var actorsText = SceneSnapshotWire.FindValue(named, SceneSnapshotWire.ActorsKey);
        var skippedText = SceneSnapshotWire.FindValue(named, SceneSnapshotWire.SkippedKey);
        var millisecondsText = SceneSnapshotWire.FindValue(named, SceneSnapshotWire.MillisecondsKey);
        var cameraText = SceneSnapshotWire.FindValue(named, SceneSnapshotWire.CameraKey);
        if (path.Length == 0
            || !int.TryParse(actorsText, NumberStyles.None, CultureInfo.InvariantCulture, out var actors)
            || !int.TryParse(skippedText, NumberStyles.None, CultureInfo.InvariantCulture, out var skipped)
            || !double.TryParse(millisecondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            failureReason = string.Format(MalformedReasonFormat, line);
            return false;
        }
        // cam は読めなければ「メインカメラ無し」と同じ扱い（写し自体は使える）
        reply = new SceneSnapshotReply(path, actors, skipped, milliseconds, SceneSnapshotCameraPose.TryParse(cameraText));
        return true;
    }
}
