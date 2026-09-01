using System;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Placement.Patterns;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace PlacementTests;

/// <summary>
/// ロジック配置（パターン生成）のアルゴリズム単体テスト。
///
/// <para>検証の柱:</para>
/// <list type="number">
///   <item>各パターンの点数・幾何（半径・間隔・角度範囲・中心揃え）</item>
///   <item>ランダム散布の最小間隔保証と決定性（同シード同出力）</item>
///   <item><b>Rust 実装（engine::placement）との一致</b>を固定する既知ベクタ</item>
///   <item>IPC ペイロード（LOGIC_PLACE の JSON）が想定どおりの形であること</item>
/// </list>
///
/// <para>
/// 3 番目が本テストの主目的。エディタのプレビューとランタイムの実生成が
/// ずれると「見た目と違う配置になる」ため、両者のテストが同じ数値を持つ。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>浮動小数比較の許容誤差（三角関数の言語差を吸収する）。</summary>
    private const double Tolerance = 1.0e-3;

    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();

        harness.Add("全周の円は個数どおり・半径一定", CircleFullHasUniformRadius);
        harness.Add("円弧は両端に点が置かれる", CircleArcPlacesBothEnds);
        harness.Add("個数 1 の円弧でも 0 除算しない", CircleSingleCountIsSafe);
        harness.Add("中心を向くヨーが中心方向を指す", CircleFaceCenterPointsInward);
        harness.Add("グリッドは行×列×段の点数と間隔になる", GridCountAndSpacing);
        harness.Add("中心揃えでグリッドの重心が原点に来る", GridCenterAlign);
        harness.Add("市松オフセットは奇数行だけずらす", GridCheckerOffset);
        harness.Add("直線 0 度は +Z 方向へ等間隔に並ぶ", LineAlongPositiveZ);
        harness.Add("直線の中心揃えと方向 90 度", LineCenterAlignAndDirection);
        harness.Add("ランダム散布は最小間隔を必ず守る", RandomRespectsMinSpacing);
        harness.Add("ランダム散布は円範囲の外へ出ない", RandomStaysInsideCircle);
        harness.Add("置けない最小間隔では減らして警告する", RandomWarnsWhenUnreachable);
        harness.Add("同じシードなら完全に同じ点列になる", SameSeedIsDeterministic);
        harness.Add("シードを変えると結果が変わる", DifferentSeedDiffers);
        harness.Add("上限超過は切り詰めて警告する", MaxPointsTruncates);
        harness.Add("[Rust 一致] splitmix64 の既知列", RngKnownVectorMatchesRust);
        harness.Add("[Rust 一致] 円形の既知ベクタ", CircleKnownVectorMatchesRust);
        harness.Add("[Rust 一致] ランダム散布の既知ベクタ", RandomKnownVectorMatchesRust);
        harness.Add("LOGIC_PLACE の JSON が 1 行で必要な項目を含む", IpcCommandShape);

        return harness.Run();
    }

    /// <summary>指定パターンの既定 spec を作る（テストの前提を 1 か所に集約する）。</summary>
    private static PlacementSpec SpecFor(PlacementPattern pattern) => new() { Pattern = pattern };

    /// <summary>XZ 平面上の原点からの距離。</summary>
    private static double RadiusXZ(PlacementPoint p) => Math.Sqrt(p.X * p.X + p.Z * p.Z);

    // ── 円形 ─────────────────────────────────────────────────

    /// <summary>全周の円が「個数どおり・半径一定・始終点が重ならない」こと。</summary>
    private static void CircleFullHasUniformRadius()
    {
        var spec = SpecFor(PlacementPattern.Circle);
        spec.Count = 8; spec.Radius = 5f;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(8, r.Points.Count, "点数");
        Check.True(r.Warning is null, "警告は出ないこと");
        foreach (var p in r.Points)
        {
            Check.Close(5.0, RadiusXZ(p), Tolerance, "半径");
            Check.Close(0.0, p.Y, Tolerance, "平面パターンの Y");
        }
        var first = r.Points[0];
        var last  = r.Points[7];
        Check.True(Math.Abs(first.X - last.X) > Tolerance || Math.Abs(first.Z - last.Z) > Tolerance,
                   "全周では始点と終点が重複しないこと");
    }

    /// <summary>円弧（角度範囲 &lt; 360）の両端に点が置かれること。</summary>
    private static void CircleArcPlacesBothEnds()
    {
        var spec = SpecFor(PlacementPattern.Circle);
        spec.Count = 3; spec.Radius = 1f; spec.StartAngle = 0f; spec.AngleSpan = 90f;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(3, r.Points.Count, "点数");
        Check.Close(1.0, r.Points[0].X, Tolerance, "始点は開始角 0 度");
        Check.Close(0.0, r.Points[0].Z, Tolerance, "始点の Z");
        Check.Close(0.0, r.Points[2].X, Tolerance, "終点の X");
        Check.Close(1.0, r.Points[2].Z, Tolerance, "終点は 90 度");
    }

    /// <summary>個数 1 の円弧でも 0 除算せず、開始角に 1 点だけ置くこと。</summary>
    private static void CircleSingleCountIsSafe()
    {
        var spec = SpecFor(PlacementPattern.Circle);
        spec.Count = 1; spec.Radius = 3f; spec.AngleSpan = 90f;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(1, r.Points.Count, "点数");
        Check.True(!float.IsNaN(r.Points[0].X) && !float.IsInfinity(r.Points[0].X), "NaN/Inf を出さないこと");
        Check.Close(3.0, r.Points[0].X, Tolerance, "開始角に置かれること");
    }

    /// <summary>「中心を向く」のヨーが中心方向を指すこと。</summary>
    private static void CircleFaceCenterPointsInward()
    {
        var spec = SpecFor(PlacementPattern.Circle);
        spec.Count = 4; spec.Radius = 2f; spec.FaceCenter = true;
        var r = PlacementGenerator.Generate(spec);

        foreach (var p in r.Points)
        {
            // ヨー規約 yaw = atan2(dir.x, dir.z)。dir は点 → 中心。
            double expected = Math.Atan2(-p.X, -p.Z) * 180.0 / Math.PI;
            double diff = Math.Abs(p.Yaw - expected);
            Check.True(diff < Tolerance || Math.Abs(diff - 360.0) < Tolerance,
                       $"中心向きヨー: 期待 {expected} / 実際 {p.Yaw}");
        }
    }

    // ── グリッド ─────────────────────────────────────────────

    /// <summary>行×列×段の点数と間隔が指定どおりであること（走査順は段→行→列）。</summary>
    private static void GridCountAndSpacing()
    {
        var spec = SpecFor(PlacementPattern.Grid);
        spec.Rows = 3; spec.Cols = 4; spec.Layers = 2;
        spec.SpacingX = 2f; spec.SpacingZ = 3f; spec.SpacingY = 5f;
        spec.CenterAlign = false;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(3 * 4 * 2, r.Points.Count, "行×列×段の点数");
        Check.Close(0.0, r.Points[0].X, Tolerance, "先頭は原点 X");
        Check.Close(2.0, r.Points[1].X, Tolerance, "列方向の間隔");
        Check.Close(3.0, r.Points[4].Z, Tolerance, "行方向の間隔");
        Check.Close(5.0, r.Points[12].Y, Tolerance, "段方向の間隔");
    }

    /// <summary>中心揃えでグリッドの重心が原点に来ること。</summary>
    private static void GridCenterAlign()
    {
        var spec = SpecFor(PlacementPattern.Grid);
        spec.Rows = 3; spec.Cols = 3; spec.Layers = 1;
        spec.SpacingX = 2f; spec.SpacingZ = 2f; spec.CenterAlign = true;
        var r = PlacementGenerator.Generate(spec);

        double cx = r.Points.Average(p => (double)p.X);
        double cz = r.Points.Average(p => (double)p.Z);
        Check.Close(0.0, cx, Tolerance, "重心 X");
        Check.Close(0.0, cz, Tolerance, "重心 Z");
        Check.Close(-2.0, r.Points[0].X, Tolerance, "端は -2");
        Check.Close(-2.0, r.Points[0].Z, Tolerance, "端は -2");
    }

    /// <summary>市松オフセットが奇数行だけを半間隔ずらすこと。</summary>
    private static void GridCheckerOffset()
    {
        var spec = SpecFor(PlacementPattern.Grid);
        spec.Rows = 2; spec.Cols = 2; spec.Layers = 1;
        spec.SpacingX = 4f; spec.SpacingZ = 4f;
        spec.CenterAlign = false; spec.CheckerOffset = true;
        var r = PlacementGenerator.Generate(spec);

        Check.Close(0.0, r.Points[0].X, Tolerance, "0 行目はずれない");
        Check.Close(2.0, r.Points[2].X, Tolerance, "1 行目は半間隔ずれる");
    }

    // ── 直線 ─────────────────────────────────────────────────

    /// <summary>方向 0 度は +Z 方向へ等間隔に並ぶこと。</summary>
    private static void LineAlongPositiveZ()
    {
        var spec = SpecFor(PlacementPattern.Line);
        spec.Count = 4; spec.LineAngle = 0f; spec.LineSpacing = 2.5f; spec.CenterAlign = false;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(4, r.Points.Count, "点数");
        for (int i = 0; i < r.Points.Count; i++)
        {
            Check.Close(0.0, r.Points[i].X, Tolerance, "0 度の直線は X が 0");
            Check.Close(i * 2.5, r.Points[i].Z, Tolerance, "間隔どおり");
        }
    }

    /// <summary>方向 90 度は +X 方向、中心揃えで線の中心が原点に来ること。</summary>
    private static void LineCenterAlignAndDirection()
    {
        var spec = SpecFor(PlacementPattern.Line);
        spec.Count = 3; spec.LineAngle = 90f; spec.LineSpacing = 2f; spec.CenterAlign = true;
        var r = PlacementGenerator.Generate(spec);

        Check.Close(-2.0, r.Points[0].X, Tolerance, "中心揃えで始点は -2");
        Check.Close(0.0, r.Points[1].X, Tolerance, "中央の点が原点");
        Check.Close(2.0, r.Points[2].X, Tolerance, "終点は +2");
        foreach (var p in r.Points) Check.Close(0.0, p.Z, Tolerance, "90 度の直線は Z が 0");
    }

    // ── ランダム散布 ─────────────────────────────────────────

    /// <summary>最小間隔が必ず守られること。</summary>
    private static void RandomRespectsMinSpacing()
    {
        var spec = SpecFor(PlacementPattern.Random);
        spec.Count = 20; spec.Seed = 42; spec.AreaCircle = true; spec.AreaRadius = 10f;
        spec.MinSpacing = 2f;
        var r = PlacementGenerator.Generate(spec);

        Check.True(r.Points.Count > 0, "1 点以上は置けること");
        for (int i = 0; i < r.Points.Count; i++)
        {
            for (int j = i + 1; j < r.Points.Count; j++)
            {
                double dx = r.Points[i].X - r.Points[j].X;
                double dz = r.Points[i].Z - r.Points[j].Z;
                double d  = Math.Sqrt(dx * dx + dz * dz);
                Check.True(d >= 2.0 - Tolerance, $"最小間隔違反: {d} < 2.0（{i} と {j}）");
            }
        }
    }

    /// <summary>範囲（円）の外へ出ないこと。最小間隔 0 なら要求数を必ず満たすこと。</summary>
    private static void RandomStaysInsideCircle()
    {
        var spec = SpecFor(PlacementPattern.Random);
        spec.Count = 50; spec.Seed = 7; spec.AreaCircle = true; spec.AreaRadius = 4f;
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(50, r.Points.Count, "最小間隔 0 なら要求数を満たす");
        foreach (var p in r.Points)
            Check.True(RadiusXZ(p) <= 4.0 + Tolerance, $"円範囲外: ({p.X}, {p.Z})");
    }

    /// <summary>達成不能な最小間隔では減らしたうえで警告すること（黙って減らさない）。</summary>
    private static void RandomWarnsWhenUnreachable()
    {
        var spec = SpecFor(PlacementPattern.Random);
        spec.Count = 50; spec.Seed = 1; spec.AreaCircle = true; spec.AreaRadius = 1f;
        spec.MinSpacing = 5f; // 半径 1m の円に 5m 間隔は原理的に 1 点しか置けない
        var r = PlacementGenerator.Generate(spec);

        Check.True(r.Points.Count < 50, "置けない要求は減らされること");
        Check.True(r.Warning is not null, "減らしたことを必ず警告すること");
    }

    // ── 決定性 ───────────────────────────────────────────────

    /// <summary>同じシードなら完全に同じ点列を返すこと（本機能の中核契約）。</summary>
    private static void SameSeedIsDeterministic()
    {
        var spec = SpecFor(PlacementPattern.Random);
        spec.Count = 30; spec.Seed = 20260901;
        spec.MinSpacing = 1f; spec.JitterPos = 0.5f; spec.JitterRot = 30f;
        spec.RandomRotation = true; spec.ScaleVariance = 0.3f;

        var a = PlacementGenerator.Generate(spec);
        var b = PlacementGenerator.Generate(spec);
        Check.Equal(a.Points.Count, b.Points.Count, "点数");
        for (int i = 0; i < a.Points.Count; i++)
        {
            Check.Equal(a.Points[i].X, b.Points[i].X, $"点 {i} の X");
            Check.Equal(a.Points[i].Z, b.Points[i].Z, $"点 {i} の Z");
            Check.Equal(a.Points[i].Yaw, b.Points[i].Yaw, $"点 {i} のヨー");
            Check.Equal(a.Points[i].Scale, b.Points[i].Scale, $"点 {i} のスケール");
        }
    }

    /// <summary>シードを変えれば結果が変わること。</summary>
    private static void DifferentSeedDiffers()
    {
        var a = SpecFor(PlacementPattern.Random); a.Count = 20; a.Seed = 1;
        var b = SpecFor(PlacementPattern.Random); b.Count = 20; b.Seed = 2;
        var pa = PlacementGenerator.Generate(a).Points;
        var pb = PlacementGenerator.Generate(b).Points;
        Check.True(pa.Zip(pb).Any(t => t.First.X != t.Second.X || t.First.Z != t.Second.Z),
                   "シードが違えば点列も違うこと");
    }

    /// <summary>上限を超える要求は切り詰めたうえで警告すること。</summary>
    private static void MaxPointsTruncates()
    {
        var spec = SpecFor(PlacementPattern.Grid);
        spec.Rows = 100; spec.Cols = 100; spec.Layers = 1; // 10,000 点 > 上限
        var r = PlacementGenerator.Generate(spec);

        Check.Equal(PlacementGenerator.MaxPlacementPoints, r.Points.Count, "上限で切り詰めること");
        Check.True(r.Warning is not null, "切り詰めたことを警告すること");
    }

    // ── Rust 実装との一致（既知ベクタ）───────────────────────

    /// <summary>
    /// splitmix64 の既知列が Rust 側 <c>placement::rng::tests</c> と一致すること。
    /// ここがずれると、以降のランダム系がすべてずれる。
    /// </summary>
    private static void RngKnownVectorMatchesRust()
    {
        var rng = new PlacementRng(1);
        ulong[] expected =
        {
            10451216379200822465UL,
            13757245211066428519UL,
            17911839290282890590UL,
            8196980753821780235UL,
        };
        for (int i = 0; i < expected.Length; i++)
            Check.Equal(expected[i], rng.NextUInt64(), $"splitmix64(seed=1) の {i} 番目");
    }

    /// <summary>
    /// 円形パターンの既知ベクタが Rust 側 <c>placement::tests</c> と一致すること。
    /// </summary>
    private static void CircleKnownVectorMatchesRust()
    {
        var spec = SpecFor(PlacementPattern.Circle);
        spec.Count = 4; spec.Radius = 10f; spec.StartAngle = 0f; spec.AngleSpan = 360f;
        var r = PlacementGenerator.Generate(spec);

        (double X, double Z)[] expected =
        {
            (10.0, 0.0),
            (0.0, 10.0),
            (-10.0, 0.0),
            (0.0, -10.0),
        };
        Check.Equal(expected.Length, r.Points.Count, "点数");
        for (int i = 0; i < expected.Length; i++)
        {
            Check.Close(expected[i].X, r.Points[i].X, Tolerance, $"点 {i} の X");
            Check.Close(expected[i].Z, r.Points[i].Z, Tolerance, $"点 {i} の Z");
        }
    }

    /// <summary>
    /// ランダム散布の既知ベクタが Rust 側と一致すること
    /// （乱数の消費順まで含めて同じであることの証明）。
    /// </summary>
    private static void RandomKnownVectorMatchesRust()
    {
        var spec = SpecFor(PlacementPattern.Random);
        spec.Count = 3; spec.Seed = 1;
        spec.AreaCircle = false; spec.AreaSizeX = 10f; spec.AreaSizeZ = 10f;
        spec.MinSpacing = 0f;
        var r = PlacementGenerator.Generate(spec);

        (double X, double Z)[] expected =
        {
            ( 0.6656152,  2.4578172),
            (-0.5573535,  2.6289433),
            (-2.1449137,  2.9399657),
        };
        Check.Equal(expected.Length, r.Points.Count, "点数");
        for (int i = 0; i < expected.Length; i++)
        {
            Check.Close(expected[i].X, r.Points[i].X, Tolerance, $"点 {i} の X");
            Check.Close(expected[i].Z, r.Points[i].Z, Tolerance, $"点 {i} の Z");
        }
    }

    // ── IPC ペイロード ───────────────────────────────────────

    /// <summary>
    /// <c>LOGIC_PLACE</c> のコマンド文字列が
    /// 「1 行・接頭辞付き・Rust 側フィールド名」で組み立てられること。
    ///
    /// IPC は行区切りで届くため、JSON に改行が混ざるとコマンドが途中で切れる。
    /// </summary>
    private static void IpcCommandShape()
    {
        var req = new LogicPlaceRequest
        {
            Target     = LogicPlaceRequest.TargetActors,
            Is2D       = false,
            ParentDfs  = 7,
            Base       = LogicPlaceRequest.BaseParent,
            GroupName  = "円形配置",
            NamePrefix = "円形配置",
            Ground     = true,
        };
        req.Spec.Pattern = PlacementPattern.Grid;
        req.Spec.Rows = 4;

        var cmd = req.ToIpcCommand();
        Check.True(cmd.StartsWith("LOGIC_PLACE:", StringComparison.Ordinal), "接頭辞");
        Check.True(!cmd.Contains('\n') && !cmd.Contains('\r'), "改行を含まないこと（IPC は行区切り）");

        using var doc = JsonDocument.Parse(cmd["LOGIC_PLACE:".Length..]);
        var root = doc.RootElement;
        Check.Equal("actors", root.GetProperty("target").GetString(), "target");
        Check.Equal(7, root.GetProperty("parent_dfs").GetInt32(), "parent_dfs");
        Check.Equal("parent", root.GetProperty("base").GetString(), "base");
        Check.Equal("円形配置", root.GetProperty("group_name").GetString(), "group_name（非 ASCII が壊れないこと）");
        Check.Equal(true, root.GetProperty("ground").GetBoolean(), "ground");
        // spec はネストして送る。パターンは Rust の serde 表現（バリアント名の文字列）。
        var spec = root.GetProperty("spec");
        Check.Equal("Grid", spec.GetProperty("pattern").GetString(), "spec.pattern");
        Check.Equal(4, spec.GetProperty("rows").GetInt32(), "spec.rows");
    }
}
