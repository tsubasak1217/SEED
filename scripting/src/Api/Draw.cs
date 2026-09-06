using System;

namespace SEED;

/// <summary>
/// 2D プリミティブのイミディエイトモード描画 API。
///
/// <para>
/// <b>使い方</b>: Update などから<b>毎フレーム呼ぶ</b>。呼んだフレームだけ描かれ、
/// エンジンはフレーム終了時にコマンドを破棄する（保持されるオブジェクトは作られない）。
/// Unity の Gizmos / Debug.DrawLine に似ているが、こちらはデバッグ用ではなく
/// <b>ゲーム本編の UI として使える描画物</b>である。
/// </para>
///
/// <para>
/// <b>座標空間</b>: 全メソッドの最後の引数 <c>space</c> で切り替える。
/// <list type="bullet">
/// <item><c>null</c>（既定）= スクリーンスペース。<b>左上原点・1 単位 = 1px・Y 下向き</b>。</item>
/// <item><see cref="CanvasTransform"/> を渡す = そのアクターの<b>ローカル空間</b>。
/// アンカー・ピボット・親子スケール・自動解像度がスプライトとまったく同じ規則で効く
/// （＝そのアクターの子として置いたスプライトと同じ座標系）。
/// 3D ワールドキャンバス配下のノードを渡した場合は、そのキャンバス平面上へ
/// ワールド空間で描かれる（3D シーンに正しく隠れる）。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>レイヤー</b>: <c>layer</c> はスプライト・テキストと<b>同じソート軸</b>
/// （大きいほど手前）。同じレイヤー内の前後は「スプライト → プリミティブ → テキスト」の順。
/// </para>
///
/// <para>
/// <b>上限</b>: 1 フレームあたり 4096 図形・1 図形あたり 1024 点。
/// 超過分は描画されず警告ログが出る。
/// </para>
/// </summary>
public static unsafe class Draw
{
    // ── FFI 規約の定数（Rust 側 primitive2d/queue.rs と一致必須）──────

    /// <summary>図形種別: 任意の閉じた多角形（Rect / Triangle / Polygon）。</summary>
    private const int KindPolygon = 0;
    /// <summary>図形種別: 折れ線（Line を含む）。</summary>
    private const int KindPolyline = 1;
    /// <summary>図形種別: 円・楕円。</summary>
    private const int KindCircle = 2;
    /// <summary>図形種別: 正多角形。</summary>
    private const int KindRegularPolygon = 3;
    /// <summary>図形種別: リング（円環セクタ）。</summary>
    private const int KindRing = 4;
    /// <summary>図形種別: 円弧。</summary>
    private const int KindArc = 5;
    /// <summary>図形種別: 角丸多角形。</summary>
    private const int KindRoundedRect = 6;
    /// <summary>図形種別: 3 次ベジエ曲線。</summary>
    private const int KindBezier = 7;

    /// <summary>共通ヘッダの float 個数（color4 + mode + thickness + layer + srt5）。</summary>
    private const int HeaderFloats = 12;
    /// <summary>図形別スカラの float 個数。</summary>
    private const int ExtraFloats = 5;
    /// <summary>パラメータ配列の総 float 個数（Rust 側 PRIM_PARAM_FLOATS と一致必須）。</summary>
    private const int ParamFloats = HeaderFloats + ExtraFloats;

    /// <summary>1 図形あたりの点数上限（Rust 側 MAX_POINTS_PER_PRIMITIVE と一致）。</summary>
    public const int MaxPointsPerPrimitive = 1024;
    /// <summary>1 フレームあたりの図形数上限（Rust 側 MAX_PRIMITIVES_PER_FRAME と一致）。</summary>
    public const int MaxPrimitivesPerFrame = 4096;

    /// <summary>スタック上に確保する点バッファの上限（これを超える点列はヒープ配列を使う）。</summary>
    private const int StackPointLimit = 64;

    /// <summary>Circle / RegularPolygon の既定スケール（等方）。</summary>
    private static Vector2 DefaultScale => new(1f, 1f);
    /// <summary>Bezier の既定分割数。</summary>
    private const int DefaultBezierSegments = 32;
    /// <summary>Ring / Arc の全周を表す終了角。</summary>
    private const float FullCircleDegrees = 360f;

    // ── 図形 API ────────────────────────────────────────────────

    /// <summary>4 点で指定した四角形を描く（点はローカル座標・srt が適用される）。</summary>
    /// <param name="p0">頂点 0（時計回り／反時計回りどちらでも良い）。</param>
    /// <param name="p1">頂点 1。</param>
    /// <param name="p2">頂点 2。</param>
    /// <param name="p3">頂点 3。</param>
    /// <param name="srt">点列へ適用する SRT。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Rect(
        Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Transform2D srt, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[4] { p0, p1, p2, p3 };
        Submit(KindPolygon, color, mode, thickness, layer, srt, default, pts, 4, space);
    }

    /// <summary>中心とサイズで指定した軸平行の四角形を描く（簡易版）。</summary>
    /// <param name="center">中心座標。</param>
    /// <param name="size">幅・高さ。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Rect(
        Vector2 center, Vector2 size, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        float hw = size.x * 0.5f, hh = size.y * 0.5f;
        Rect(
            new Vector2(center.x - hw, center.y - hh),
            new Vector2(center.x + hw, center.y - hh),
            new Vector2(center.x + hw, center.y + hh),
            new Vector2(center.x - hw, center.y + hh),
            Transform2D.Identity, color, mode, thickness, layer, space);
    }

    /// <summary>三角形を描く。</summary>
    /// <param name="a">頂点 A。</param>
    /// <param name="b">頂点 B。</param>
    /// <param name="c">頂点 C。</param>
    /// <param name="srt">点列へ適用する SRT。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Triangle(
        Vector2 a, Vector2 b, Vector2 c, Transform2D srt, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[3] { a, b, c };
        Submit(KindPolygon, color, mode, thickness, layer, srt, default, pts, 3, space);
    }

    /// <summary>2 点を結ぶ直線を描く。</summary>
    /// <param name="a">始点。</param>
    /// <param name="b">終点。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thickness">線の太さ（px）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Line(
        Vector2 a, Vector2 b, Color color,
        float thickness = 1f, int layer = 0, CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[2] { a, b };
        // extras[0] = closed（直線なので 0）
        var extras = new Extras(0f);
        Submit(KindPolyline, color, DrawMode.Fill, thickness, layer, Transform2D.Identity,
            extras, pts, 2, space);
    }

    /// <summary>円（scale で楕円）を描く。</summary>
    /// <param name="center">中心座標。</param>
    /// <param name="radius">半径（px）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="scale">XY 個別スケール（null = 等方 1）。楕円にしたいときに使う。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Circle(
        Vector2 center, float radius, Color color, Vector2? scale = null,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        var s = scale ?? DefaultScale;
        Vector2* pts = stackalloc Vector2[1] { center };
        var extras = new Extras(radius, s.x, s.y);
        Submit(KindCircle, color, mode, thickness, layer, Transform2D.Identity,
            extras, pts, 1, space);
    }

    /// <summary>正多角形を描く（三角形・五角形・星形の土台など）。</summary>
    /// <param name="center">中心座標。</param>
    /// <param name="radius">外接円半径（px）。</param>
    /// <param name="vertices">頂点数（3 未満は 3 に丸められる）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="rotationDegrees">回転（度・時計回り）。</param>
    /// <param name="scale">XY 個別スケール（null = 等方 1）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void RegularPolygon(
        Vector2 center, float radius, int vertices, Color color,
        float rotationDegrees = 0f, Vector2? scale = null,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        var s = scale ?? DefaultScale;
        Vector2* pts = stackalloc Vector2[1] { center };
        var extras = new Extras(radius, vertices, rotationDegrees, s.x, s.y);
        Submit(KindRegularPolygon, color, mode, thickness, layer, Transform2D.Identity,
            extras, pts, 1, space);
    }

    /// <summary>
    /// 円弧を描く。
    /// <para>
    /// <see cref="DrawMode.Fill"/> のときは「半径 radius を帯の中心とする太さ thickness のリング」、
    /// <see cref="DrawMode.Outline"/> のときは「半径 radius 上を通る太さ thickness の線」になる。
    /// どちらも見た目はほぼ同じで、Fill のほうが継ぎ目が出ないため<b>ゲージには Fill を推奨</b>。
    /// </para>
    /// </summary>
    /// <param name="center">中心座標。</param>
    /// <param name="radius">半径（px）。</param>
    /// <param name="startDegrees">開始角（度）。0 = +X 方向、正の角度は時計回り。</param>
    /// <param name="endDegrees">終了角（度）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">リング（Fill）／線（Outline）。</param>
    /// <param name="thickness">帯・線の太さ（px）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Arc(
        Vector2 center, float radius, float startDegrees, float endDegrees, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[1] { center };
        var extras = new Extras(radius, startDegrees, endDegrees);
        Submit(KindArc, color, mode, thickness, layer, Transform2D.Identity,
            extras, pts, 1, space);
    }

    /// <summary>内半径・外半径で指定するリング（円環セクタ）を描く。ゲージ表示向け。</summary>
    /// <param name="center">中心座標。</param>
    /// <param name="innerRadius">内半径（0 なら扇形になる）。</param>
    /// <param name="outerRadius">外半径。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="startDegrees">開始角（度・時計回り）。</param>
    /// <param name="endDegrees">終了角（度）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Ring(
        Vector2 center, float innerRadius, float outerRadius, Color color,
        float startDegrees = 0f, float endDegrees = FullCircleDegrees,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[1] { center };
        var extras = new Extras(innerRadius, outerRadius, startDegrees, endDegrees);
        Submit(KindRing, color, mode, thickness, layer, Transform2D.Identity,
            extras, pts, 1, space);
    }

    /// <summary>中心とサイズで指定した角丸四角形を描く。</summary>
    /// <param name="center">中心座標。</param>
    /// <param name="size">幅・高さ。</param>
    /// <param name="cornerRadius">角丸半径（辺の半分を超える指定は自動的に切り詰められる）。</param>
    /// <param name="srt">点列へ適用する SRT。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void RoundedRect(
        Vector2 center, Vector2 size, float cornerRadius, Transform2D srt, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        float hw = size.x * 0.5f, hh = size.y * 0.5f;
        RoundedRect(
            new Vector2(center.x - hw, center.y - hh),
            new Vector2(center.x + hw, center.y - hh),
            new Vector2(center.x + hw, center.y + hh),
            new Vector2(center.x - hw, center.y + hh),
            cornerRadius, srt, color, mode, thickness, layer, space);
    }

    /// <summary>4 点で指定した四角形の角を丸めて描く。</summary>
    /// <param name="p0">頂点 0。</param>
    /// <param name="p1">頂点 1。</param>
    /// <param name="p2">頂点 2。</param>
    /// <param name="p3">頂点 3。</param>
    /// <param name="cornerRadius">角丸半径。</param>
    /// <param name="srt">点列へ適用する SRT。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void RoundedRect(
        Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float cornerRadius,
        Transform2D srt, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[4] { p0, p1, p2, p3 };
        var extras = new Extras(cornerRadius);
        Submit(KindRoundedRect, color, mode, thickness, layer, srt, extras, pts, 4, space);
    }

    /// <summary>折れ線を描く。</summary>
    /// <param name="points">点列（2 点以上）。</param>
    /// <param name="closed">true なら末尾と先頭を繋いで閉じる。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thickness">線の太さ（px）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Polyline(
        Vector2[] points, bool closed, Color color,
        float thickness = 1f, int layer = 0, CanvasTransform? space = null)
    {
        SubmitArray(KindPolyline, points, Transform2D.Identity, color, DrawMode.Fill,
            thickness, layer, new Extras(closed ? 1f : 0f), space);
    }

    /// <summary>
    /// 多角形を描く。
    /// <para>
    /// <b>制限</b>: 自己交差しない単純多角形のみ対応（凹多角形は耳刈りで正しく塗られる）。
    /// 穴あき・自己交差の形状は結果が保証されない（クラッシュはしない）。
    /// </para>
    /// </summary>
    /// <param name="points">輪郭の点列（3 点以上）。</param>
    /// <param name="srt">点列へ適用する SRT。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">塗り／輪郭。</param>
    /// <param name="thickness">輪郭の太さ（Outline のときのみ有効）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Polygon(
        Vector2[] points, Transform2D srt, Color color,
        DrawMode mode = DrawMode.Fill, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        SubmitArray(KindPolygon, points, srt, color, mode, thickness, layer, default, space);
    }

    /// <summary>3 次ベジエ曲線を描く（線のみ）。</summary>
    /// <param name="p0">始点。</param>
    /// <param name="p1">制御点 1。</param>
    /// <param name="p2">制御点 2。</param>
    /// <param name="p3">終点。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="segments">分割数（2〜512 にクランプされる）。</param>
    /// <param name="thickness">線の太さ（px）。</param>
    /// <param name="layer">描画レイヤー（大きいほど手前）。</param>
    /// <param name="space">座標空間（null = スクリーンスペース）。</param>
    public static void Bezier(
        Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color color,
        int segments = DefaultBezierSegments, float thickness = 1f, int layer = 0,
        CanvasTransform? space = null)
    {
        Vector2* pts = stackalloc Vector2[4] { p0, p1, p2, p3 };
        var extras = new Extras(segments);
        Submit(KindBezier, color, DrawMode.Fill, thickness, layer, Transform2D.Identity,
            extras, pts, 4, space);
    }

    // ── 内部実装 ────────────────────────────────────────────────

    /// <summary>
    /// 図形別の追加スカラ（最大 5 個）。
    /// 意味は図形種別ごとに異なる（Rust 側 PrimitiveKind のコメントが正典）。
    /// </summary>
    private readonly struct Extras
    {
        public readonly float E0, E1, E2, E3, E4;

        public Extras(float e0 = 0f, float e1 = 0f, float e2 = 0f, float e3 = 0f, float e4 = 0f)
        {
            E0 = e0; E1 = e1; E2 = e2; E3 = e3; E4 = e4;
        }
    }

    /// <summary>
    /// 配列で受け取った点列を（上限まで）スタックまたはヒープへ写して発行する。
    /// </summary>
    private static void SubmitArray(
        int kind, Vector2[] points, Transform2D srt, Color color, DrawMode mode,
        float thickness, int layer, Extras extras, CanvasTransform? space)
    {
        if (points == null || points.Length == 0) return;
        int n = points.Length > MaxPointsPerPrimitive ? MaxPointsPerPrimitive : points.Length;
        if (n <= StackPointLimit)
        {
            Vector2* buf = stackalloc Vector2[StackPointLimit];
            for (int i = 0; i < n; i++) buf[i] = points[i];
            Submit(kind, color, mode, thickness, layer, srt, extras, buf, n, space);
        }
        else
        {
            // 大きな点列はスタックを溢れさせないよう固定した配列を直接渡す
            fixed (Vector2* buf = points)
            {
                Submit(kind, color, mode, thickness, layer, srt, extras, buf, n, space);
            }
        }
    }

    /// <summary>
    /// パラメータ配列を組み立てて FFI へ 1 コマンド発行する（全図形の唯一の出口）。
    /// </summary>
    private static void Submit(
        int kind, Color color, DrawMode mode, float thickness, int layer,
        Transform2D srt, Extras extras, Vector2* points, int pointCount,
        CanvasTransform? space)
    {
        float* p = stackalloc float[ParamFloats];
        // 共通ヘッダ（Rust 側 PRIM_HEADER_FLOATS の並びと一致必須）
        p[0] = color.r; p[1] = color.g; p[2] = color.b; p[3] = color.a;
        p[4] = (float)mode;
        p[5] = thickness;
        p[6] = layer;
        p[7] = srt.Position.x; p[8] = srt.Position.y;
        p[9] = srt.RotationDegrees;
        p[10] = srt.Scale.x; p[11] = srt.Scale.y;
        // 図形別スカラ
        p[12] = extras.E0; p[13] = extras.E1; p[14] = extras.E2;
        p[15] = extras.E3; p[16] = extras.E4;

        // Vector2 は float 2 個のみの構造体なので float* として渡せる
        var entity = space.HasValue ? space.Value.Owner : Entity.None;
        ScriptHost.DrawPrimitive(kind, entity, p, ParamFloats, (float*)points, pointCount);
    }
}
