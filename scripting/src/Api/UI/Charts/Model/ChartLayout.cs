using System;

namespace SEED.UI;

// ============================================================
//  ChartLayout.cs — グラフの枠の割り付けと吹き出し・日付線のハンドルの置き場（W2-8・W2 の手直し P2-4。docs/ui_charts.md §2・§6）
//
//  【枠】グラフのノードの矩形（幅 × 高さ）を、左の縦軸の文字の列（YAxisWidth）・下の横軸の文字の行（XAxisHeight）・
//  上と右の余白（PadTop・PadRight）で割り、残りを描く面（Plot）にする。軸を出さないときは幅・高さ 0。
//  【吹き出し】点の上（間 gap）に置き、入らなければ点の下。左右は枠の中へ収める（はみ出す分だけずらす）。
//  【日付線のハンドル】（P2-4）丸の中心の X = 日付線の X、丸の下端 = 面の下の縁（横軸の線）。横軸の文字の行に重ならず、
//  グラフの子（面の切り抜きの外）なので面の端でも切れない。出すのは日付線が面の中に見えているとき（吹き出しと同じ半単位の許容）。
//  【ハンドルの指の位置】イベントの LocalPosition（ハンドルの見た目の矩形の左上が原点・ノードの単位。エンジンがノードの行列の逆で
//  求めるので dp・Scale・VisualScale の下でもグラフの単位のまま）＋ ハンドルの左上（前のフレームに書いた位置＝エンジンが座標を
//  求めたときの位置）で、グラフのノードの単位の指の位置になる（docs/input_gestures.md §8）。
// ============================================================

/// <summary>グラフの枠の中の矩形（左上・大きさ。グラフのノードの単位）。</summary>
public readonly struct ChartRect
{
    /// <summary>左。</summary>
    public readonly float X;
    /// <summary>上。</summary>
    public readonly float Y;
    /// <summary>幅。</summary>
    public readonly float Width;
    /// <summary>高さ。</summary>
    public readonly float Height;

    /// <summary>矩形を作る（負の大きさは 0）。</summary>
    public ChartRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = MathF.Max(0f, width);
        Height = MathF.Max(0f, height);
    }

    /// <summary>右。</summary>
    public float Right => X + Width;
    /// <summary>下。</summary>
    public float Bottom => Y + Height;

    /// <summary>点が中か（端を含む）。</summary>
    public bool Contains(Vector2 p) => p.x >= X && p.x <= Right && p.y >= Y && p.y <= Bottom;

    /// <summary>デバッグ表示。</summary>
    public override string ToString() => $"({X:0.#}, {Y:0.#}, {Width:0.#}×{Height:0.#})";
}

/// <summary>枠の割り付けの指定（グラフのノードの単位＝dp）。</summary>
public struct ChartFrameSpec
{
    /// <summary>左の縦軸の文字の列の幅（0 = 出さない）。</summary>
    public float YAxisWidth;
    /// <summary>下の横軸の文字の行の高さ（0 = 出さない）。</summary>
    public float XAxisHeight;
    /// <summary>上の余白（一番上の目盛りの文字・点の印がはみ出さない分）。</summary>
    public float PadTop;
    /// <summary>右の余白。</summary>
    public float PadRight;
}

/// <summary>枠の割り付けと吹き出しの置き場（純粋な計算）。</summary>
public static class ChartLayout
{
    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>描く面（Plot）の矩形。</summary>
    public static ChartRect Plot(float width, float height, in ChartFrameSpec spec)
    {
        float left = MathF.Max(0f, spec.YAxisWidth);
        float top = MathF.Max(0f, spec.PadTop);
        return new ChartRect(left, top, width - left - MathF.Max(0f, spec.PadRight), height - top - MathF.Max(0f, spec.XAxisHeight));
    }

    /// <summary>
    /// 吹き出しの左上: 点 <paramref name="anchor"/> の上に間 <paramref name="gap"/> をあけて置き、上に入らなければ下。
    /// 左右は枠 <paramref name="bounds"/> の中へ収める（枠より大きければ枠の左に合わせる）。
    /// </summary>
    public static Vector2 TooltipPosition(Vector2 anchor, Vector2 size, ChartRect bounds, float gap)
    {
        float x = anchor.x - size.x * Half;
        float maxX = bounds.Right - size.x;
        x = maxX < bounds.X ? bounds.X : Math.Clamp(x, bounds.X, maxX);
        float above = anchor.y - gap - size.y;
        float y = above >= bounds.Y ? above : anchor.y + gap;
        return new Vector2(x, y);
    }

    /// <summary>
    /// 面の中の位置 <paramref name="v"/>（面の左上が原点の 1 軸）が長さ <paramref name="length"/> の面の中か（両端に半単位の許容）。
    /// 吹き出し・日付線のハンドルを出す条件（面のちょうど端の点も出す。パンで外へ出たら隠す）。
    /// </summary>
    public static bool InsidePlot(float v, float length) => v >= -Half && v <= length + Half;

    /// <summary>
    /// 日付線のハンドル（直径 <paramref name="diameter"/> の丸）の左上（グラフのノードの単位）: 中心の X = 面の左 + 日付線の X
    /// <paramref name="lineX"/>（面の左上が原点）、丸の下端 = 面の下の縁（中心の Y = 面の下端 − 半径）。
    /// </summary>
    public static Vector2 HandlePosition(ChartRect plot, float lineX, float diameter)
    {
        float d = MathF.Max(0f, diameter);
        return new Vector2(plot.X + lineX - d * Half, plot.Bottom - d);
    }

    /// <summary>
    /// ハンドルを引く指の X（面の左上が原点・グラフのノードの単位）= ハンドルの左上の X（エンジンがイベントの座標を求めたときの位置）
    /// ＋ イベントの LocalPosition の X（ハンドルの見た目の矩形の左上が原点・ノードの単位）− 面の左。
    /// </summary>
    /// <param name="handleTopLeft">ハンドルのノードに最後に書いた位置（左上。pivot・anchor は 0）。</param>
    /// <param name="localPosition">ハンドルのジェスチャーのイベントの LocalPosition。</param>
    /// <param name="plot">描く面。</param>
    public static float HandleFingerPlotX(Vector2 handleTopLeft, Vector2 localPosition, ChartRect plot)
        => handleTopLeft.x + localPosition.x - plot.X;
}
