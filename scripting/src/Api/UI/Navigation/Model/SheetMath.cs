using System;

namespace SEED.UI;

// ============================================================
//  SheetMath.cs — 下からのシートの段（半分・全体）・幕の濃さ・安全領域の余白（W2-7。純粋な計算）
//
//  【作り】シートは縦の CanvasScroll（W2-3。Track）の中身として「隙間（Gap。領域の高さ）」と「板（Panel。シートの高さ）」を
//  縦に並べる。スクロールの位置 p = 板がどれだけ出ているか（0 = 閉じた・板の高さ = 全部開いた）。
//  - 段: 半分あり（HalfDetent）なら位置 {0, 板/2, 板}、無ければ {0, 板}。CanvasScroll の Interval のスナップ（間隔 = 板/2 か 板）を
//    そのまま使うので、指のドラッグ・フリックの速度（W2-2）から止まる段を Rust の物理が決める（Flutter の FrictionSimulation.through）
//  - 閉じる: 止まった位置が 0（閉じた段）なら閉じる（つまみのドラッグ・下へのフリック・隙間のタップ・戻る）
//  - 入れ子: 中身の縦の一覧（hand_off_to_parent = true）が端に達した残りのドラッグ・フリックは、同じ向きの祖先の窓＝Track へ渡る
//    （W2-3 の受け渡し。一覧が先頭で下へ引くとシートが下がる）
//  - 幕の濃さ: 最初の段（半分があれば半分）まで開く間に 0 → 最大。それより上は最大のまま
// ============================================================

/// <summary>シートの段。</summary>
public enum SheetDetent
{
    /// <summary>閉じた（位置 0）。</summary>
    Closed = 0,
    /// <summary>半分（板の高さの半分）。</summary>
    Half = 1,
    /// <summary>全部（板の高さ）。</summary>
    Full = 2,
    /// <summary>段の間（動いている途中）。</summary>
    Between = 3,
}

/// <summary>下からのシートの指定。</summary>
public sealed class SheetOptions
{
    /// <summary>半分の段を持つか。</summary>
    public bool HalfDetent { get; init; } = true;
    /// <summary>開いたとき半分で止めるか（false なら全部。半分の段が無ければ全部）。</summary>
    public bool StartHalf { get; init; } = true;
    /// <summary>板の高さ（覆う領域の高さに対する割合。0 以下ならテーマの ratio.sheet_max_height）。</summary>
    public float HeightFraction { get; init; }
    /// <summary>隙間（幕）のタップで閉じるか。</summary>
    public bool DismissOnScrimTap { get; init; } = true;
    /// <summary>戻るで閉じるか（false でも戻るは受ける）。</summary>
    public bool CancelableByBack { get; init; } = true;
    /// <summary>板の中身のプレハブ（空なら板だけ。assets:// の .actor）。</summary>
    public string ContentPrefab { get; init; } = string.Empty;
    /// <summary>中身へ渡す値（中身の UiScreen.OnScreenEnter に届く）。</summary>
    public object? Args { get; init; }
}

/// <summary>シートの段と幕の計算。</summary>
public static class SheetMath
{
    /// <summary>段に「着いた」とみなす位置の誤差（キャンバスの単位）。</summary>
    public const float DetentTolerance = 0.5f;
    /// <summary>半分の段の位置（板の高さに対する割合）。</summary>
    public const float HalfFraction = 0.5f;

    /// <summary>板の高さ（領域の高さ × 割合。割合は 0〜1 へ収める）。</summary>
    public static float PanelHeight(float areaHeight, float fraction)
    {
        if (!(areaHeight > 0f)) return 0f;
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 1f;
        return areaHeight * f;
    }

    /// <summary>スナップの間隔（半分があれば板の半分、無ければ板）。</summary>
    public static float SnapInterval(float panelHeight, bool halfDetent)
        => halfDetent ? panelHeight * HalfFraction : panelHeight;

    /// <summary>段の位置。</summary>
    public static float DetentPosition(SheetDetent detent, float panelHeight) => detent switch
    {
        SheetDetent.Half => panelHeight * HalfFraction,
        SheetDetent.Full => panelHeight,
        _ => 0f,
    };

    /// <summary>開いたときに止める段。</summary>
    public static SheetDetent OpenDetent(SheetOptions options)
        => options.HalfDetent && options.StartHalf ? SheetDetent.Half : SheetDetent.Full;

    /// <summary>
    /// 位置 → 段（どの段にも着いていなければ Between）。半分の段が無いときの板の半分は Between。
    /// </summary>
    public static SheetDetent Classify(float position, float panelHeight, bool halfDetent)
    {
        if (!float.IsFinite(position) || !(panelHeight > 0f)) return SheetDetent.Closed;
        if (MathF.Abs(position) <= DetentTolerance) return SheetDetent.Closed;
        if (MathF.Abs(position - panelHeight) <= DetentTolerance) return SheetDetent.Full;
        if (halfDetent && MathF.Abs(position - panelHeight * HalfFraction) <= DetentTolerance) return SheetDetent.Half;
        return SheetDetent.Between;
    }

    /// <summary>
    /// 幕の濃さ（最初の段まで開く間に 0 → 最大。上は最大のまま）。
    /// </summary>
    public static float ScrimAlpha(float position, float panelHeight, bool halfDetent, float maxAlpha)
    {
        float first = halfDetent ? panelHeight * HalfFraction : panelHeight;
        if (!(first > 0f) || !float.IsFinite(position)) return 0f;
        return Math.Clamp(position / first, 0f, 1f) * Math.Clamp(maxAlpha, 0f, 1f);
    }

    /// <summary>
    /// 安全領域の辺の余白（画素 → キャンバスの単位）。シート・覆いの中身をジェスチャーバー・ステータスバーから離す（背景は端まで）。
    /// </summary>
    /// <param name="insetPx">画面の端から安全領域までの画素（負は 0）。</param>
    /// <param name="dpScale">1 dp の画素数（Screen.DpScale。dp のルートキャンバスの単位）。</param>
    public static float InsetUnits(float insetPx, float dpScale)
    {
        if (!(insetPx > 0f) || !float.IsFinite(insetPx)) return 0f;
        return dpScale > 0f && float.IsFinite(dpScale) ? insetPx / dpScale : insetPx;
    }
}

/// <summary>ドラッグで閉じる判定（上からの覆いのつまみ・トーストのスワイプ。W2-7。純粋な計算）。</summary>
public static class DragDismissMath
{
    /// <summary>
    /// 離したときに閉じるか。閉じる向きの速さがフリックの速さ以上なら閉じる。逆向きに速ければ戻す。
    /// それ以外は閉じる向きへ距離のしきい値以上引いていたら閉じる。
    /// </summary>
    /// <param name="offset">閉じる向きへ引いた距離（dp。逆向きは負）。</param>
    /// <param name="velocity">閉じる向きの速さ（dp/秒。W2-2 の VelocityDp の成分。逆向きは負）。</param>
    /// <param name="distanceThreshold">距離のしきい値（size.drag_dismiss）。</param>
    /// <param name="flingVelocity">フリックの速さ（speed.fling_dismiss）。</param>
    public static bool ShouldDismiss(float offset, float velocity, float distanceThreshold, float flingVelocity)
    {
        float v = float.IsFinite(velocity) ? velocity : 0f;
        float d = float.IsFinite(offset) ? offset : 0f;
        if (flingVelocity > 0f)
        {
            if (v >= flingVelocity) return true;
            if (v <= -flingVelocity) return false;
        }
        return distanceThreshold > 0f && d >= distanceThreshold;
    }

    /// <summary>
    /// 引いた距離を当てる。閉じる向きはそのまま、逆向きは抵抗（resistance。0〜1。0 で動かない）を掛けて最大 maxBack まで。
    /// </summary>
    public static float ApplyDrag(float offset, float delta, float resistance, float maxBack)
    {
        if (!float.IsFinite(delta)) return offset;
        float next = offset + (offset + delta < 0f ? delta * Math.Clamp(resistance, 0f, 1f) : delta);
        return Math.Max(next, -Math.Max(0f, maxBack));
    }
}
