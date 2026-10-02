using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  NavigationMath.cs — 方向キーで「次にフォーカスする部品」を矩形から選ぶ（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  手本は Unity の Selectable の Navigation = Automatic（FindSelectable）:
//    1. 起点 = 今の矩形の、押した向きの辺の中点（右なら右の辺の真ん中）
//    2. 候補の中心へのベクトル v。向きの単位ベクトル d との内積（along = d·v）が正の候補だけを見る
//       （後ろ・真横・今の部品の中に中心がある候補は選ばない）
//    3. 点数 = along / |v|²（大きいほど良い）。|v|² = along² + 直交のずれ² なので、
//       同じ距離なら向きにまっすぐな候補が、同じ向きなら近い候補が勝つ（近い候補ほど直交のずれを嫌う）
//    4. 同点（相対の差が TieRelativeEpsilon 以内）は読む順（中心が上 → 左）、それも同じなら並びの先（添字の小さい方）
//  端（向きに候補が無い）では -1（動かない）。Wrap なら反対側の端の外に仮の矩形を置いて同じ向きに選び直す
//  （行・列の反対の端へ回る。今の部品がいちばん端なら動かない）。
//  ほかに: 最初の部品（読む順）・スクロールで見せる量。座標は画面の画素（左上が原点・Y 下向き）。
//  エンジンに触れない（editor/tests/UiComponentsTests で検算）。
// ============================================================

/// <summary>方向キーの移り先の選び方（純粋な計算）。</summary>
public static class NavigationMath
{
    /// <summary>同点とみなす点数の相対の差（浮動小数の丸めで並びが揺れない）。</summary>
    public const float TieRelativeEpsilon = 1e-4f;

    /// <summary>
    /// 内積が正とみなす下限（画素）。真横の候補（中心が起点の辺の延長の上）を丸めの誤差で拾わない。
    /// </summary>
    public const float MinAlong = 0.5f;

    /// <summary>同じ高さ（読む順で同じ行）とみなす中心の差（画素）。</summary>
    public const float SameLineTolerance = 0.5f;

    /// <summary>Wrap の仮の矩形を候補の外へ離す量（画素。端の候補と辺が重ならないように）。</summary>
    public const float WrapClearance = 1f;

    /// <summary>半分。</summary>
    private const float Half = 0.5f;

    /// <summary>
    /// 今の矩形から向きに「最も近い」候補を選ぶ（Unity の Automatic と同じ点数。端で止まる）。
    /// </summary>
    /// <param name="current">今の部品の矩形（画面の画素）。</param>
    /// <param name="candidates">候補の矩形（今の部品は含めない。含めても内積が正にならないので選ばれない）。</param>
    /// <param name="direction">押した向き。</param>
    /// <returns>候補の添字（無ければ -1）。</returns>
    public static int PickNext(Rect current, IReadOnlyList<Rect> candidates, FocusDirection direction)
        => PickNext(current, candidates, direction, wrap: false);

    /// <summary>
    /// 今の矩形から向きに「最も近い」候補を選ぶ。wrap なら、向きに候補が無いとき反対側の端へ回る
    /// （今の部品がその行・列のいちばん端なら回らない＝-1）。
    /// </summary>
    /// <param name="current">今の部品の矩形（画面の画素）。</param>
    /// <param name="candidates">候補の矩形（今の部品は含めない）。</param>
    /// <param name="direction">押した向き。</param>
    /// <param name="wrap">端で反対側へ回るか。</param>
    /// <returns>候補の添字（無ければ -1）。</returns>
    public static int PickNext(Rect current, IReadOnlyList<Rect> candidates, FocusDirection direction, bool wrap)
    {
        if (direction == FocusDirection.None || candidates is null || !IsUsable(current)) return -1;
        int found = PickFrom(current, candidates, direction, self: null);
        if (found >= 0 || !wrap) return found;
        // 端: 反対側の端の外に今の矩形を写し、同じ向きにもう一度選ぶ（今の部品も候補に入れ、勝ったら動かない）
        var virtualRect = WrapOrigin(current, candidates, direction);
        int wrapped = PickFrom(virtualRect, candidates, direction, self: current);
        return wrapped;
    }

    /// <summary>
    /// 読む順で最初の候補（いちばん上の行の左端）。いちばん上の候補の縦の幅に中心が入る候補を同じ行とみなす。
    /// 画面を開いたとき・ダイアログが前に出たときの最初のフォーカス（UiNavigation.FocusFirstIn）。
    /// </summary>
    /// <param name="candidates">候補の矩形。</param>
    /// <returns>添字（使える候補が無ければ -1）。</returns>
    public static int PickFirst(IReadOnlyList<Rect> candidates)
    {
        if (candidates is null) return -1;
        // いちばん上の候補（上の辺が最も小さい。同じなら先の添字）
        int top = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!IsUsable(candidates[i])) continue;
            if (top < 0 || candidates[i].YMin < candidates[top].YMin) top = i;
        }
        if (top < 0) return -1;
        // その行（中心がいちばん上の候補の縦の幅に入る）のうち左端（左の辺が最も小さい。同じなら先の添字）
        var band = candidates[top];
        int best = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!IsUsable(c)) continue;
            float cy = c.Center.y;
            if (cy < band.YMin || cy > band.YMax) continue;
            if (best < 0 || c.XMin < candidates[best].XMin) best = i;
        }
        return best;
    }

    /// <summary>
    /// 矩形に最も近い候補（中心どうしの距離。同じなら読む順 → 先の添字）。今のフォーカスが消えた（一覧の行を消した）ときに、
    /// 近くの部品へ移すのに使う。
    /// </summary>
    /// <param name="reference">消えた部品の最後の矩形。</param>
    /// <param name="candidates">候補の矩形。</param>
    /// <returns>添字（使える候補が無ければ -1）。</returns>
    public static int PickNearest(Rect reference, IReadOnlyList<Rect> candidates)
    {
        if (candidates is null || !IsUsable(reference)) return -1;
        var origin = reference.Center;
        int best = -1;
        float bestDistance = float.PositiveInfinity;
        var bestCenter = Vector2.Zero;
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!IsUsable(c)) continue;
            var center = c.Center;
            float dx = center.x - origin.x;
            float dy = center.y - origin.y;
            float distance = dx * dx + dy * dy;
            // 距離が小さい方。同じ（相対の差が TieRelativeEpsilon 以内）なら読む順で先の方（点数とは逆向きの比べ方なので符号を反す）
            float tolerance = MathF.Max(distance, bestDistance) * TieRelativeEpsilon;
            bool better = best < 0
                || distance < bestDistance - tolerance
                || (distance <= bestDistance + tolerance && IsBetter(0f, center, 0f, bestCenter));
            if (!better) continue;
            best = i;
            bestDistance = distance;
            bestCenter = center;
        }
        return best;
    }

    /// <summary>
    /// 矩形を窓の中へ入れるためにスクロールする量（画素。正 = 中身を先へ送る＝CanvasScroll.Position を増やす）。
    /// 入っていれば 0。窓より大きい矩形は先頭（上・左）の辺を合わせる。余白（margin）ぶん内側へ入れる。
    /// </summary>
    /// <param name="target">見せたい矩形（画面の画素）。</param>
    /// <param name="viewport">スクロールの窓の矩形（画面の画素）。</param>
    /// <param name="margin">窓の辺との間に空ける余白（画素。フォーカスの枠の分）。</param>
    public static Vector2 RevealDelta(Rect target, Rect viewport, float margin)
    {
        if (!IsUsable(target) || !IsUsable(viewport)) return Vector2.Zero;
        float m = float.IsFinite(margin) && margin > 0f ? margin : 0f;
        return new Vector2(
            RevealAxis(target.XMin, target.XMax, viewport.XMin, viewport.XMax, m),
            RevealAxis(target.YMin, target.YMax, viewport.YMin, viewport.YMax, m));
    }

    /// <summary>起点（今の矩形の、向きの辺の中点）。</summary>
    /// <param name="rect">今の矩形。</param>
    /// <param name="direction">向き。</param>
    public static Vector2 EdgeMidpoint(Rect rect, FocusDirection direction)
    {
        var c = rect.Center;
        return direction switch
        {
            FocusDirection.Up => new Vector2(c.x, rect.YMin),
            FocusDirection.Down => new Vector2(c.x, rect.YMax),
            FocusDirection.Left => new Vector2(rect.XMin, c.y),
            FocusDirection.Right => new Vector2(rect.XMax, c.y),
            _ => c,
        };
    }

    /// <summary>
    /// 点数（Unity の Automatic: 内積 / 距離²。内積が MinAlong 以下なら NaN＝候補にしない）。
    /// </summary>
    /// <param name="origin">起点。</param>
    /// <param name="candidate">候補の矩形。</param>
    /// <param name="direction">向き。</param>
    public static float Score(Vector2 origin, Rect candidate, FocusDirection direction)
    {
        var d = FocusDirections.UnitOf(direction);
        var c = candidate.Center;
        float vx = c.x - origin.x;
        float vy = c.y - origin.y;
        float along = vx * d.x + vy * d.y;
        if (!(along > MinAlong)) return float.NaN;
        float squared = vx * vx + vy * vy;
        return along / squared;
    }

    /// <summary>矩形が使えるか（位置と大きさが有限で、大きさが負でない）。</summary>
    /// <param name="r">矩形。</param>
    public static bool IsUsable(Rect r)
        => float.IsFinite(r.x) && float.IsFinite(r.y) && float.IsFinite(r.width) && float.IsFinite(r.height)
           && r.width >= 0f && r.height >= 0f;

    // ── 内側 ──────────────────────────────────────────────

    /// <summary>
    /// 起点の矩形から向きに最も点数の高い候補を選ぶ。self（今の部品の本当の矩形）を渡すと、それも候補として比べ、
    /// 勝ったら -1（Wrap で回った先が今の部品自身＝その行・列のいちばん端）。
    /// </summary>
    private static int PickFrom(Rect origin, IReadOnlyList<Rect> candidates, FocusDirection direction, Rect? self)
    {
        var start = EdgeMidpoint(origin, direction);
        int best = -1;
        float bestScore = float.NaN;
        var bestCenter = Vector2.Zero;
        // 今の部品自身を先に比べる（候補と同点なら、ほかの候補どうしと同じく読む順で決める）
        const int SelfIndex = -2;
        if (self is { } s)
        {
            float selfScore = Score(start, s, direction);
            if (!float.IsNaN(selfScore))
            {
                best = SelfIndex;
                bestScore = selfScore;
                bestCenter = s.Center;
            }
        }
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!IsUsable(c)) continue;
            float score = Score(start, c, direction);
            if (float.IsNaN(score)) continue;
            if (best == -1 || IsBetter(score, c.Center, bestScore, bestCenter))
            {
                best = i;
                bestScore = score;
                bestCenter = c.Center;
            }
        }
        return best == SelfIndex ? -1 : best;
    }

    /// <summary>
    /// 点数が上か。同点（相対の差が TieRelativeEpsilon 以内）なら読む順（中心が上 → 左）で先か。それも同じなら false（先の添字を保つ）。
    /// </summary>
    private static bool IsBetter(float score, Vector2 center, float bestScore, Vector2 bestCenter)
    {
        float tolerance = MathF.Max(MathF.Abs(score), MathF.Abs(bestScore)) * TieRelativeEpsilon;
        if (score > bestScore + tolerance) return true;
        if (score < bestScore - tolerance) return false;
        if (center.y < bestCenter.y - SameLineTolerance) return true;
        if (center.y > bestCenter.y + SameLineTolerance) return false;
        return center.x < bestCenter.x - SameLineTolerance;
    }

    /// <summary>
    /// Wrap の起点: 候補と今の矩形をすべて囲む矩形の、向きと反対側の辺の外へ今の矩形を写す（直交の位置は同じ）。
    /// </summary>
    private static Rect WrapOrigin(Rect current, IReadOnlyList<Rect> candidates, FocusDirection direction)
    {
        float minX = current.XMin, minY = current.YMin, maxX = current.XMax, maxY = current.YMax;
        foreach (var c in candidates)
        {
            if (!IsUsable(c)) continue;
            minX = MathF.Min(minX, c.XMin);
            minY = MathF.Min(minY, c.YMin);
            maxX = MathF.Max(maxX, c.XMax);
            maxY = MathF.Max(maxY, c.YMax);
        }
        return direction switch
        {
            // 右へ回る = 全体の左の外から右へ探し直す
            FocusDirection.Right => new Rect(minX - WrapClearance - current.width, current.y, current.width, current.height),
            FocusDirection.Left => new Rect(maxX + WrapClearance, current.y, current.width, current.height),
            FocusDirection.Down => new Rect(current.x, minY - WrapClearance - current.height, current.width, current.height),
            FocusDirection.Up => new Rect(current.x, maxY + WrapClearance, current.width, current.height),
            _ => current,
        };
    }

    /// <summary>1 つの軸の見せる量（窓に入っていれば 0。窓より大きければ先頭の辺を合わせる）。</summary>
    private static float RevealAxis(float min, float max, float viewMin, float viewMax, float margin)
    {
        float wantMin = min - margin;
        float wantMax = max + margin;
        // 窓より大きい（余白込み）: 先頭の辺を合わせる
        if (wantMax - wantMin >= viewMax - viewMin) return wantMin - viewMin;
        if (wantMin < viewMin) return wantMin - viewMin;
        if (wantMax > viewMax) return wantMax - viewMax;
        return 0f;
    }
}
