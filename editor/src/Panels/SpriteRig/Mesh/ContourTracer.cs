using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Mesh;

/// <summary>
/// 二値マスク（不透明 / 透明）から輪郭ポリゴンを抽出する。
///
/// 【方式】ピクセル境界追跡（crack following）
/// マーチングスクエアのように補間点を作らず、<b>ピクセルの境界線そのもの</b>を
/// 辿って閉ループを作る。得られる輪郭は整数格子上の階段状ポリゴンで、
/// 「1 ピクセルもはみ出さない」ことが構造的に保証される（後段の簡略化で初めて誤差が入る）。
///
/// 【向きの規約】
/// 不透明ピクセルが常に進行方向の<b>右側</b>に来るよう有向辺を張る。
/// 「+X 右・+Y 下」の座標系ではこの向きは符号付き面積が正になるため、
/// <b>外周輪郭 = 正の面積 / 穴 = 負の面積</b>で機械的に判別できる。
///
/// 【連結性】
/// 分岐点（斜めに接する 2 つの不透明ピクセル）では常に右折を優先する。
/// これは前景を 8 連結・背景を 4 連結として扱うことに相当し、
/// 斜めに繋がった絵が別々の島に割れるのを防ぐ。
/// </summary>
public static class ContourTracer
{
    /// <summary>方向テーブル（右折・直進・左折・引き返しの順に試す）ための回転数。</summary>
    private const int TurnCandidateCount = 4;

    /// <summary>
    /// 抽出された 1 本の輪郭。
    /// </summary>
    /// <param name="Points">閉じた頂点列（末尾と先頭が暗黙に繋がる）。</param>
    /// <param name="IsHole">true = 穴（負の符号付き面積）、false = 外周。</param>
    /// <param name="SignedArea">符号付き面積（ピクセル^2）。</param>
    public sealed record Contour(List<Vec2> Points, bool IsHole, double SignedArea);

    /// <summary>
    /// 二値マスクから全輪郭（外周・穴）を抽出する。
    /// </summary>
    /// <param name="solid">長さ width*height の不透明フラグ（行優先）。</param>
    /// <param name="width">マスクの横幅。</param>
    /// <param name="height">マスクの高さ。</param>
    /// <param name="minAbsArea">この絶対面積未満の輪郭はノイズとして捨てる（ピクセル^2）。</param>
    /// <returns>抽出した輪郭の一覧（順不同）。</returns>
    public static List<Contour> Trace(bool[] solid, int width, int height, double minAbsArea = 0.0)
    {
        if (width <= 0 || height <= 0) return new List<Contour>();
        if (solid.Length != width * height)
            throw new ArgumentException("マスク長が width*height と一致しません", nameof(solid));

        // ── 1. 境界の有向辺を全部張る ────────────────────────────
        // 頂点は (width+1) x (height+1) の整数格子。頂点番号 = vy*(width+1)+vx。
        int vertexStride = width + 1;
        var edgeFrom = new List<int>();
        var edgeTo = new List<int>();
        // 出発頂点 -> その頂点から出る辺番号の一覧
        var outgoing = new Dictionary<int, List<int>>();

        // ローカル関数: 有向辺を 1 本登録する
        void AddEdge(int fromVx, int fromVy, int toVx, int toVy)
        {
            int from = fromVy * vertexStride + fromVx;
            int to = toVy * vertexStride + toVx;
            int id = edgeFrom.Count;
            edgeFrom.Add(from);
            edgeTo.Add(to);
            if (!outgoing.TryGetValue(from, out var list))
            {
                list = new List<int>(2);
                outgoing[from] = list;
            }
            list.Add(id);
        }

        // ローカル関数: 範囲外を透明として扱うマスク参照
        bool IsSolid(int x, int y)
            => x >= 0 && y >= 0 && x < width && y < height && solid[y * width + x];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!IsSolid(x, y)) continue;

                // 不透明ピクセルの各辺について、隣が透明なら境界。
                // 進行方向の右側が常に自分（不透明）になる向きで張る。
                if (!IsSolid(x, y - 1)) AddEdge(x, y, x + 1, y);             // 上辺: +X
                if (!IsSolid(x + 1, y)) AddEdge(x + 1, y, x + 1, y + 1);     // 右辺: +Y
                if (!IsSolid(x, y + 1)) AddEdge(x + 1, y + 1, x, y + 1);     // 下辺: -X
                if (!IsSolid(x - 1, y)) AddEdge(x, y + 1, x, y);             // 左辺: -Y
            }
        }

        // ── 2. 有向辺を繋いで閉ループにする ──────────────────────
        var used = new bool[edgeFrom.Count];
        var contours = new List<Contour>();

        for (int startEdge = 0; startEdge < edgeFrom.Count; startEdge++)
        {
            if (used[startEdge]) continue;

            var points = new List<Vec2>();
            int current = startEdge;
            while (true)
            {
                used[current] = true;
                int from = edgeFrom[current];
                points.Add(new Vec2(from % vertexStride, from / vertexStride));

                int next = SelectNextEdge(current, edgeFrom, edgeTo, outgoing, used, vertexStride);
                if (next < 0) break;
                current = next;
            }

            if (points.Count < 3) continue;
            double area = Geometry2D.SignedArea(points);
            if (Math.Abs(area) < minAbsArea) continue;

            contours.Add(new Contour(points, area < 0.0, area));
        }

        return contours;
    }

    /// <summary>
    /// 現在の有向辺の終点から出る「次の辺」を選ぶ。
    ///
    /// 分岐している場合は右折 → 直進 → 左折 → 引き返し の順に採用する。
    /// 右折優先は「不透明側へ寄る」ことを意味し、斜め接続を 1 本の輪郭として保つ。
    /// </summary>
    /// <returns>次の辺番号。未使用の候補が無ければ -1（ループ終了）。</returns>
    private static int SelectNextEdge(
        int currentEdge,
        List<int> edgeFrom,
        List<int> edgeTo,
        Dictionary<int, List<int>> outgoing,
        bool[] used,
        int vertexStride)
    {
        int endVertex = edgeTo[currentEdge];
        if (!outgoing.TryGetValue(endVertex, out var candidates)) return -1;

        // 現在の進行方向（格子上なので単位ベクトル）
        int startVertex = edgeFrom[currentEdge];
        int dx = (endVertex % vertexStride) - (startVertex % vertexStride);
        int dy = (endVertex / vertexStride) - (startVertex / vertexStride);

        // 右折 → 直進 → 左折 → 引き返し の順に候補方向を並べる
        // （+Y 下の座標系では右折は (dx,dy) -> (-dy,dx)）
        Span<int> wantDx = stackalloc int[TurnCandidateCount] { -dy, dx, dy, -dx };
        Span<int> wantDy = stackalloc int[TurnCandidateCount] { dx, dy, -dx, -dy };

        for (int k = 0; k < TurnCandidateCount; k++)
        {
            foreach (int candidate in candidates)
            {
                if (used[candidate]) continue;
                int to = edgeTo[candidate];
                int cdx = (to % vertexStride) - (endVertex % vertexStride);
                int cdy = (to / vertexStride) - (endVertex / vertexStride);
                if (cdx == wantDx[k] && cdy == wantDy[k]) return candidate;
            }
        }
        return -1;
    }
}
