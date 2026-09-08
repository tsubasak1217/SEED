// ============================================================
//  KeyInsertPrecheck.cs — キー挿入/上書きの前提条件判定（純ロジック）
//
//  【解決したい問題】
//  タイムラインの「現在値が取得できません（対象アクタを選択してください）」は
//  原因の異なる複数の状況を 1 つの文言に丸めてしまい、ユーザーが次に何をすれば
//  よいか分からなかった（実例: フォルダノードを Animator の親にしていた、
//  文脈固定 🔒 中にスナップショットが更新されていなかった、等）。
//
//  ここでは「いま I / U キーでキーを打ってよいか」を判定する部分だけを、
//  WPF から独立した純関数として切り出す（editor/tests から直接テストする）。
//  実際のリトライ（GET_ACTOR_COMPONENTS を送って待つ）は
//  AnimationTimelinePanel 側の責務のまま残す。
// ============================================================

using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>キー挿入前提チェックの判定結果。Ok 以外は挿入を中断し、対応する案内文を出す。</summary>
internal enum KeyInsertPrecheckResult
{
    /// <summary>キー挿入を続行してよい。</summary>
    Ok,

    /// <summary>ファイル単独モード（「直接開く」）で開いているため、アクタ文脈自体が無い。</summary>
    FileOnly,

    /// <summary>Animator アクタ・キー対象アクタのどちらも定まっていない（クリップ未選択などに委ねる）。</summary>
    NoContext,

    /// <summary>キー対象アクタは決まっているが、その現在値スナップショットがまだ届いていない（取得を再試行すべき）。</summary>
    NoSnapshot,

    /// <summary>キー対象アクタがフォルダノード（整理専用・Transform 非保持）で、そもそもアニメーションできない。</summary>
    Folder,

    /// <summary>スナップショットは届いたが、Transform も CanvasTransform も持たない（想定外・要報告）。</summary>
    NoTransform,

    /// <summary>対象アクタの種別（2D/3D）と、クリップのトラックが要求する種別が食い違う。</summary>
    KindMismatch,
}

/// <summary>キー挿入前提チェックの純粋なロジック。</summary>
internal static class KeyInsertPrecheck
{
    /// <summary>
    /// 現在の状態からキー挿入（I）・上書き（U）を実行してよいか判定する。
    /// </summary>
    /// <param name="snapshot">キー対象アクタの現在値スナップショット（未到着なら既定の空インスタンス）。</param>
    /// <param name="keyTargetDfsId">キー対象アクタの DFS ID（-1 = 未定）。</param>
    /// <param name="hasContext">Animator アクタ（編集文脈）が定まっているか。</param>
    /// <param name="fileOnly">ファイル単独モードかどうか。</param>
    /// <param name="clipTracks">編集中クリップのトラック一覧（0 本でもよい）。</param>
    public static KeyInsertPrecheckResult Evaluate(
        AnimActorSnapshot snapshot,
        int keyTargetDfsId,
        bool hasContext,
        bool fileOnly,
        IReadOnlyList<AnimTrack> clipTracks)
    {
        // ファイル単独モードはアクタ文脈自体を持たないため最優先で弾く
        if (fileOnly) return KeyInsertPrecheckResult.FileOnly;

        // Animator も キー対象も定まっていない（クリップが開かれていない等）
        if (!hasContext || keyTargetDfsId < 0) return KeyInsertPrecheckResult.NoContext;

        // スナップショットが「キー対象アクタ自身」のものでなければ、まだ届いていないとみなす。
        // ActorDfsId は Parse("") の既定で -1 になる（現実の DFS ID は 0 以上）ため、
        // 「届いたが中身が無い」（フォルダ／異常系）と区別できる。
        if (snapshot.ActorDfsId != keyTargetDfsId) return KeyInsertPrecheckResult.NoSnapshot;

        // フォルダは整理専用ノードで Transform を一切持たない＝アニメーションの対象にできない
        if (snapshot.IsFolder) return KeyInsertPrecheckResult.Folder;

        // 届いたが Transform も CanvasTransform も無い（通常は起こらない異常系）
        if (snapshot.IsEmpty) return KeyInsertPrecheckResult.NoTransform;

        // トラックが要求する変換コンポーネントの種別（2D/3D）と、実際にアクタが持つ種別を突き合わせる。
        // 0 本のとき（OfferCreateTracks で新規作成する場合）はここに来ないので判定不要。
        var has2D = snapshot.Has(AnimActorSnapshot.CanvasTransformComponent, AnimActorSnapshot.PositionProperty);
        var has3D = snapshot.Has(AnimActorSnapshot.TransformComponent, AnimActorSnapshot.PositionProperty);
        var tracksWant2D = clipTracks.Any(t => t.Target.Component == AnimActorSnapshot.CanvasTransformComponent);
        var tracksWant3D = clipTracks.Any(t => t.Target.Component == AnimActorSnapshot.TransformComponent);

        if (tracksWant2D && !has2D && has3D) return KeyInsertPrecheckResult.KindMismatch;
        if (tracksWant3D && !has3D && has2D) return KeyInsertPrecheckResult.KindMismatch;

        return KeyInsertPrecheckResult.Ok;
    }
}
