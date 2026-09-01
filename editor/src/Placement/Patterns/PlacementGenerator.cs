using System;
using System.Collections.Generic;

namespace SEEDEditor.Placement.Patterns;

/// <summary>
/// 配置パターンの点列生成。
///
/// <para>
/// <b>これは Rust 側 <c>runtime/src/engine/placement/generate.rs</c> の写しである。</b>
/// ダイアログの俯瞰プレビューは即時応答が要るため IPC 往復を挟めず、
/// 同じアルゴリズムをエディタ側にも持つ。<b>正典は Rust 側</b>で、
/// 実際にシーンへ置かれる点は必ずランタイムが生成したものになる。
/// 片方を変えたら必ず両方を直し、両側の既知ベクタテストを更新すること
/// （<c>editor/tests/PlacementTests</c> / runtime の <c>placement::tests</c>）。
/// </para>
///
/// <para><b>決定性の契約</b>（Rust 側と同一）:
/// ① 乱数は <see cref="PlacementRng"/> のみ ②乱数の消費数を分岐で変えない
/// ③走査順は固定（段 y → 行 z → 列 x）。
/// </para>
///
/// <para>WPF に依存しない（テストプロジェクトが本ファイルを直接リンクするため）。</para>
/// </summary>
public static class PlacementGenerator
{
    /// <summary>
    /// 1 回の配置で生成できる点数の上限。
    /// Rust 側 <c>MAX_PLACEMENT_POINTS</c> と一致させること。
    /// </summary>
    public const int MaxPlacementPoints = 4096;

    /// <summary>ランダム散布の 1 点あたり試行回数の上限。</summary>
    private const int RandomAttemptsPerPoint = 32;

    /// <summary>「全周」とみなす角度範囲 [度]。</summary>
    private const float FullCircleDegrees = 360f;

    /// <summary>全周判定の許容誤差 [度]。</summary>
    private const float FullCircleEpsilon = 1.0e-3f;

    /// <summary>
    /// ジッター用 RNG のシードソルト。
    /// パターン本体の乱数と同じストリームを共有しないための奇数（Rust 側と同値）。
    /// </summary>
    private const ulong JitterSeedSalt = 0x5EED10C1C0DE1111UL;

    /// <summary>度 → ラジアン。</summary>
    private static float ToRadians(float deg) => (float)(deg * Math.PI / 180.0);

    /// <summary>ラジアン → 度。</summary>
    private static float ToDegrees(float rad) => (float)(rad * 180.0 / Math.PI);

    /// <summary>
    /// 方向ベクトル（XZ 成分）からヨー角 [度] を求める。
    /// 規約は <c>yaw = atan2(dir.x, dir.z)</c>（ヨー 0 で +Z を向く）。
    /// </summary>
    private static float YawFromDir(float dx, float dz)
        => dx == 0f && dz == 0f ? 0f : ToDegrees((float)Math.Atan2(dx, dz));

    /// <summary>
    /// 配置指定から点列を生成する。
    /// 戻り値の点は<b>基準点を原点とするローカル座標</b>で、
    /// ワールドへの移動と地形接地はランタイム側の責務。
    /// </summary>
    /// <param name="spec">パターン指定。</param>
    public static PlacementResult Generate(PlacementSpec spec)
    {
        var result = spec.Pattern switch
        {
            PlacementPattern.Circle => GenerateCircle(spec),
            PlacementPattern.Grid   => GenerateGrid(spec),
            PlacementPattern.Line   => GenerateLine(spec),
            PlacementPattern.Random => GenerateRandom(spec),
            _                       => new PlacementResult(),
        };

        // ── 上限で切り詰める（黙って切らずに警告へ載せる）──
        if (result.Points.Count > MaxPlacementPoints)
        {
            int dropped = result.Points.Count - MaxPlacementPoints;
            result.Points.RemoveRange(MaxPlacementPoints, dropped);
            result.Warning = $"生成点が上限（{MaxPlacementPoints} 点）を超えたため {dropped} 点を切り詰めました";
        }

        ApplyFaceForward(spec, result.Points);
        ApplyJitter(spec, result.Points);
        return result;
    }

    // ── 円形・円弧 ──────────────────────────────────────────

    /// <summary>
    /// 円形・円弧。全周のときは開始角と終了角が重なるため分母を count にして
    /// 重複点を作らない。円弧のときは両端に点を置く。
    /// </summary>
    private static PlacementResult GenerateCircle(PlacementSpec spec)
    {
        var r = new PlacementResult();
        int count = (int)spec.Count;
        if (count <= 0) return r;

        bool full = Math.Abs(Math.Abs(spec.AngleSpan) - FullCircleDegrees) <= FullCircleEpsilon;
        float denom = full ? count : Math.Max(count, 2) - 1;

        for (int i = 0; i < count; i++)
        {
            float deg = spec.StartAngle + spec.AngleSpan * i / denom;
            float rad = ToRadians(deg);
            float sin = (float)Math.Sin(rad);
            float cos = (float)Math.Cos(rad);

            float yaw = spec.FaceCenter ? YawFromDir(-cos, -sin)
                      : spec.FaceForward ? YawFromDir(-sin, cos)
                      : 0f;

            var p = PlacementPoint.Default();
            p.X = spec.Radius * cos;
            p.Z = spec.Radius * sin;
            p.Yaw = yaw;
            r.Points.Add(p);
        }
        return r;
    }

    // ── グリッド ────────────────────────────────────────────

    /// <summary>
    /// グリッド（行 × 列 × 段）。走査順は <b>段 y → 行 z → 列 x</b> で固定する
    /// （この順が生成アクタの連番 _01, _02, … の順序になる）。
    /// </summary>
    private static PlacementResult GenerateGrid(PlacementSpec spec)
    {
        var r = new PlacementResult();
        int cols   = (int)Math.Max(spec.Cols, 1u);
        int rows   = (int)Math.Max(spec.Rows, 1u);
        int layers = (int)Math.Max(spec.Layers, 1u);

        float Off(int n, float spacing) => spec.CenterAlign ? (n - 1) * 0.5f * spacing : 0f;
        float offX = Off(cols, spec.SpacingX);
        float offZ = Off(rows, spec.SpacingZ);
        float offY = Off(layers, spec.SpacingY);

        for (int ly = 0; ly < layers; ly++)
        {
            for (int row = 0; row < rows; row++)
            {
                float checker = spec.CheckerOffset && row % 2 == 1 ? spec.SpacingX * 0.5f : 0f;
                for (int c = 0; c < cols; c++)
                {
                    var p = PlacementPoint.Default();
                    p.X = c * spec.SpacingX - offX + checker;
                    p.Y = ly * spec.SpacingY - offY;
                    p.Z = row * spec.SpacingZ - offZ;
                    r.Points.Add(p);
                }
            }
        }
        return r;
    }

    // ── 直線 ────────────────────────────────────────────────

    /// <summary>直線（方向角 + 間隔 × 個数）。中心揃えなら線の中心が基準点に来る。</summary>
    private static PlacementResult GenerateLine(PlacementSpec spec)
    {
        var r = new PlacementResult();
        int count = (int)spec.Count;
        if (count <= 0) return r;

        float rad = ToRadians(spec.LineAngle);
        // ヨー規約 yaw = atan2(x, z) に合わせ、方向ベクトルは (sin, cos)。
        float dx = (float)Math.Sin(rad);
        float dz = (float)Math.Cos(rad);
        float start = spec.CenterAlign ? -(count - 1) * 0.5f * spec.LineSpacing : 0f;
        // 直線は点の並ぶ向きが自明なので、進行方向はここで確定させる
        // （間隔 0 でも向きが定まる点が一般則より正確）。
        float yaw = spec.FaceForward ? spec.LineAngle : 0f;

        for (int i = 0; i < count; i++)
        {
            float t = start + i * spec.LineSpacing;
            var p = PlacementPoint.Default();
            p.X = dx * t;
            p.Z = dz * t;
            p.Yaw = yaw;
            r.Points.Add(p);
        }
        return r;
    }

    // ── ランダム散布 ────────────────────────────────────────

    /// <summary>
    /// ランダム散布（拒否サンプリングによる最小間隔保証）。
    ///
    /// <b>乱数の消費順（Rust 側と厳密に一致させること）</b>:
    /// 1 回の試行につき必ず u, v の 2 個を引く。採用されたときだけ
    /// さらに rot, scale の 2 個を引く（フラグの有無に関わらず必ず引く）。
    /// </summary>
    private static PlacementResult GenerateRandom(PlacementSpec spec)
    {
        var r = new PlacementResult();
        int count = (int)spec.Count;
        if (count <= 0) return r;

        var rng = new PlacementRng(spec.Seed);
        float minSpacing = Math.Max(spec.MinSpacing, 0f);
        float minSq = minSpacing * minSpacing;
        int maxAttempts = count * RandomAttemptsPerPoint;

        for (int attempt = 0; attempt < maxAttempts && r.Points.Count < count; attempt++)
        {
            float u = rng.NextFloat();
            float v = rng.NextFloat();

            float x, z;
            if (spec.AreaCircle)
            {
                // 円は面積一様になるよう半径に sqrt を掛ける。
                float radius = spec.AreaRadius * (float)Math.Sqrt(u);
                float a = v * (float)(Math.PI * 2.0);
                x = radius * (float)Math.Cos(a);
                z = radius * (float)Math.Sin(a);
            }
            else
            {
                x = (u - 0.5f) * spec.AreaSizeX;
                z = (v - 0.5f) * spec.AreaSizeZ;
            }

            if (minSq > 0f)
            {
                bool tooClose = false;
                foreach (var q in r.Points)
                {
                    float ddx = q.X - x;
                    float ddz = q.Z - z;
                    if (ddx * ddx + ddz * ddz < minSq) { tooClose = true; break; }
                }
                if (tooClose) continue;
            }

            // 採用。回転・スケールの乱数はフラグに関わらず必ず引く（決定性）。
            float rotR = rng.NextFloat();
            float sclR = rng.NextFloat();

            var p = PlacementPoint.Default();
            p.X = x;
            p.Z = z;
            p.Yaw = spec.RandomRotation ? rotR * FullCircleDegrees : 0f;
            p.Scale = spec.ScaleVariance != 0f
                ? Math.Max(1f + (sclR * 2f - 1f) * spec.ScaleVariance, 0f)
                : 1f;
            r.Points.Add(p);
        }

        if (r.Points.Count < count)
        {
            r.Warning = $"最小間隔 {minSpacing:F2}m では {count} 個を配置できませんでした"
                      + $"（{r.Points.Count} 個で打ち切り）。範囲を広げるか最小間隔を小さくしてください";
        }
        return r;
    }

    // ── 後処理 ──────────────────────────────────────────────

    /// <summary>
    /// 「進行方向を向く」をグリッド・ランダムへ適用する。
    /// 円形・直線はパターン側で向きを確定済みなので触らない。
    /// 先頭の点は次の点の向きを借りる。
    /// </summary>
    private static void ApplyFaceForward(PlacementSpec spec, List<PlacementPoint> points)
    {
        if (!spec.FaceForward) return;
        if (spec.Pattern != PlacementPattern.Grid && spec.Pattern != PlacementPattern.Random) return;
        if (points.Count < 2) return;

        // 先に全区間のヨーを求めてから書き戻す（前の点の書き換えに引きずられないため）。
        var yaws = new float[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            int a = i == 0 ? 0 : i - 1;
            int b = i == 0 ? 1 : i;
            yaws[i] = YawFromDir(points[b].X - points[a].X, points[b].Z - points[a].Z);
        }
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            p.Yaw = yaws[i];
            points[i] = p;
        }
    }

    /// <summary>
    /// 位置・回転のジッターを適用する。
    /// パターン本体とは独立したストリーム（シードにソルトを XOR）を使い、
    /// <b>点ごとに必ず 4 個の乱数を引く</b>（位置 XYZ + ヨー）。
    /// </summary>
    private static void ApplyJitter(PlacementSpec spec, List<PlacementPoint> points)
    {
        var rng = new PlacementRng(spec.Seed ^ JitterSeedSalt);
        for (int i = 0; i < points.Count; i++)
        {
            float jx = rng.NextSigned();
            float jy = rng.NextSigned();
            float jz = rng.NextSigned();
            float jr = rng.NextSigned();
            var p = points[i];
            p.X += jx * spec.JitterPos;
            p.Y += jy * spec.JitterPos;
            p.Z += jz * spec.JitterPos;
            p.Yaw += jr * spec.JitterRot;
            points[i] = p;
        }
    }
}
