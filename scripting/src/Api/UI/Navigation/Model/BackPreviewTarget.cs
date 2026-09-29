using System;
using SEED.Platform;

namespace SEED.UI;

// ============================================================
//  BackPreviewTarget.cs — 予測型の戻るのプレビューの相手の口と姿勢の計算（W2 の手直し 3b。純粋な計算）
//
//  Android 13 以上の予測型の戻る（プロジェクト設定 android.predictive_back。3a）で、戻るの手ぶりの途中に「戻るとこれが閉じる」
//  ものを縮めて見せる。縮めるのは戻るの段（BackChain）で最初に戻るを受ける層の相手（IBackPreviewTarget）:
//    - 画面のスタック（ScreenStack）… いちばん上の画面の枠（縮めて、指の動く向きへ少しずらす。下の画面の実体があれば見せる）
//    - ダイアログ（Dialog）          … 札（真ん中の周りに縮む）
//    - 下からのシート（BottomSheet） … 板（下の辺を留めて縮む）
//    - 上からの覆い（TopSheet）      … 板（上の辺を留めて縮む）
//  フォーカス（入力欄）・タブの切り替え・スクリプトが足した層・閉じない面（戻るで閉じないダイアログ・シート）は相手なし（何も縮めない）。
//  倍率はエンジンの CanvasLayoutItem.VisualScale（矩形の中心の周り。子孫・当たり判定がそろって縮む）で当てる。
//
//  姿勢（BackPreviewPose）= 倍率 1 − (1 − いちばん小さい倍率) × 見た目の進み、ずらし = 向き × ずらす量 × 見た目の進み
//  （見た目の進み = テーマの曲線 motion.back_preview_curve を通した手ぶりの進み具合。いちばん小さい倍率 ratio.back_preview_scale・
//   ずらす量 size.back_preview_shift。どれもテーマのトークン＝データで差し替えられる）。
// ============================================================

/// <summary>予測型の戻るのプレビューの姿勢（W2 の手直し 3b）。</summary>
/// <param name="Scale">倍率（1 = 元の大きさ。縦横同じ）。</param>
/// <param name="ShiftX">横のずらし（キャンバスの単位。正が右。画面だけが使う）。</param>
public readonly record struct BackPreviewPose(float Scale, float ShiftX)
{
    /// <summary>元の姿勢（倍率 1・ずらし 0）。</summary>
    public static BackPreviewPose Identity => new(1f, 0f);
}

/// <summary>縮めても動かさない辺（プレビューの相手が決める）。</summary>
public enum BackPreviewAnchor
{
    /// <summary>真ん中の周りに縮む（画面・ダイアログの札）。</summary>
    Center = 0,
    /// <summary>上の辺を留める（上からの覆い）。</summary>
    Top = 1,
    /// <summary>下の辺を留める（下からのシート）。</summary>
    Bottom = 2,
}

/// <summary>
/// 予測型の戻るのプレビューの相手（ScreenStack のいちばん上の画面・ModalHost の面が実装する。W2 の手直し 3b）。
/// BackDispatcher（BackPreview）が手ぶりの間に姿勢を当て、取り消し・諦めたら元へ戻し、確定したら閉じる動きが終わるまで待つ。
/// </summary>
public interface IBackPreviewTarget
{
    /// <summary>
    /// 今もプレビューできるか（同じ画面・面がいちばん上のまま、出入り・閉じる動きの途中でなく、部品が消えていない）。
    /// false になったら呼び手はすぐ元の姿勢へ戻して手放す（画面の切り替え・層の破棄での取り残し防止）。
    /// </summary>
    bool IsBackPreviewValid { get; }

    /// <summary>
    /// 確定して閉じる・下ろす動きの途中か（呼び手は姿勢を保ったまま待ち、false になったら元の姿勢へ戻す。ノードが消えていれば何もしない）。
    /// </summary>
    bool IsBackPreviewExiting { get; }

    /// <summary>姿勢を当てる（前の値と同じなら書かない）。</summary>
    void ApplyBackPreview(BackPreviewPose pose);

    /// <summary>元の姿勢へ戻す（倍率 1・ずらし 0。プレビューのために一時的に見せた物は隠し直す）。</summary>
    void ClearBackPreview();
}

/// <summary>予測型の戻るのプレビューの姿勢の計算（純粋な計算）。</summary>
public static class BackPreviewMath
{
    /// <summary>縮めた分のうち、片側の辺が動く割合（真ん中の周りに縮むと、減った大きさの半分ずつ両側の辺が内へ動く）。</summary>
    private const float HalfOfShrink = 0.5f;

    /// <summary>
    /// 見た目の進み → 姿勢。
    /// </summary>
    /// <param name="visual">見た目の進み（0〜1。曲線を通した手ぶりの進み具合。範囲の外・NaN は収める）。</param>
    /// <param name="shiftSign">ずらす向き（<see cref="ShiftSign"/>。+1 右・−1 左・0 なし）。</param>
    /// <param name="minScale">いちばん小さい倍率（見た目の進み 1 の倍率）。</param>
    /// <param name="maxShift">いちばん大きなずらし（キャンバスの単位）。</param>
    public static BackPreviewPose Pose(float visual, float shiftSign, float minScale, float maxShift)
    {
        float v = float.IsFinite(visual) ? Math.Clamp(visual, 0f, 1f) : 0f;
        return new BackPreviewPose(1f - (1f - minScale) * v, shiftSign * maxShift * v);
    }

    /// <summary>
    /// 手ぶりを始めた端 → ずらす向き（指の動く向き: 左の端からなら右 +1、右の端からなら左 −1、端でない〈ボタンの戻る〉なら 0）。
    /// Material 3 の予測型の戻るで、縮めた画面が指に付いて動く向き（記憶による）。
    /// </summary>
    public static float ShiftSign(BackEdge edge) => edge switch
    {
        BackEdge.Left => 1f,
        BackEdge.Right => -1f,
        _ => 0f,
    };

    /// <summary>
    /// 真ん中の周りに縮めたノードの、留める辺を元の位置へ戻す縦のずらし（キャンバスの単位・下が正）。
    /// </summary>
    /// <param name="height">ノードの高さ（キャンバスの単位。0 以下・有限でなければずらさない）。</param>
    /// <param name="scale">倍率。</param>
    /// <param name="anchor">留める辺。</param>
    public static float AnchorOffset(float height, float scale, BackPreviewAnchor anchor)
    {
        if (!(height > 0f) || !float.IsFinite(height) || !float.IsFinite(scale)) return 0f;
        float edgeMove = height * (1f - scale) * HalfOfShrink;
        return anchor switch
        {
            // 上の辺は下へ edgeMove 動くので、上へ戻す
            BackPreviewAnchor.Top => -edgeMove,
            // 下の辺は上へ edgeMove 動くので、下へ戻す
            BackPreviewAnchor.Bottom => edgeMove,
            _ => 0f,
        };
    }
}
