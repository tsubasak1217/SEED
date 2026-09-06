using System;

namespace SEED;

/// <summary>
/// 3D プリミティブのイミディエイトモード描画 API（ワールド空間）。
///
/// <para>
/// <b>使い方</b>: Update などから<b>毎フレーム呼ぶ</b>。呼んだフレームだけ描かれ、
/// エンジンはフレーム終了時にコマンドを破棄する（保持されるオブジェクトは作られない）。
/// 釣り糸のたるみ・水面の距離リング・索敵範囲のワイヤ球など、デバッグ表示にも
/// <b>ゲーム本編の表現にも</b>使える。
/// </para>
///
/// <para>
/// <b>座標</b>: すべて<b>ワールド空間</b>の <see cref="Vector3"/>。
/// 2D 版（<see cref="Draw"/>）のようなキャンバス／スクリーン座標の概念は無い。
/// </para>
///
/// <para>
/// <b>線の太さ・点の大きさは画面ピクセル</b>: <c>thicknessPx</c> / <c>sizePx</c> は
/// カメラからの距離に依らず一定の画面 px になる（頂点シェーダーが押し出す）。
/// </para>
///
/// <para>
/// <b>前後関係</b>: <c>depthTest</c>（既定 true）で 3D シーンに正しく隠れる。
/// false にすると常に手前へ描かれる（デバッグ表示向け）。レイヤーの概念は無く、
/// 同じ depthTest 内の重なりは<b>呼び出し順</b>で決まる。
/// 描画位置は「半透明・3D キャンバススプライトの後、2D UI の前」。
/// </para>
///
/// <para>
/// <b>上限</b>: 1 フレームあたり 4096 図形・1 図形あたり 1024 点。
/// 超過分は描画されず警告ログが出る。
/// </para>
/// </summary>
public static unsafe class Draw3D
{
    // ── FFI 規約の定数（Rust 側 primitive3d/queue.rs と一致必須）──────

    /// <summary>図形種別: 折れ線（Line を含む）。</summary>
    private const int KindPolyline = 0;
    /// <summary>図形種別: 平面多角形（Triangle / Quad）。</summary>
    private const int KindPolygon = 1;
    /// <summary>図形種別: 円。</summary>
    private const int KindCircle = 2;
    /// <summary>図形種別: リング（円環バンド）。</summary>
    private const int KindRing = 3;
    /// <summary>図形種別: 円弧。</summary>
    private const int KindArc = 4;
    /// <summary>図形種別: ワイヤ球。</summary>
    private const int KindWireSphere = 5;
    /// <summary>図形種別: ワイヤ直方体。</summary>
    private const int KindWireBox = 6;
    /// <summary>図形種別: ワイヤカプセル。</summary>
    private const int KindWireCapsule = 7;
    /// <summary>図形種別: 矢印。</summary>
    private const int KindArrow = 8;
    /// <summary>図形種別: 点（画面を向く正方形）。</summary>
    private const int KindPoint = 9;

    /// <summary>共通ヘッダの float 個数（color4 + mode + thicknessPx + depthTest）。</summary>
    private const int HeaderFloats = 7;
    /// <summary>図形別スカラの float 個数（最大は WireBox のサイズ 3 + 回転 3）。</summary>
    private const int ExtraFloats = 6;
    /// <summary>パラメータ配列の総 float 個数（Rust 側 PRIM3D_PARAM_FLOATS と一致必須）。</summary>
    private const int ParamFloats = HeaderFloats + ExtraFloats;

    /// <summary>1 図形あたりの点数上限（Rust 側 MAX_POINTS_PER_PRIMITIVE3D と一致）。</summary>
    public const int MaxPointsPerPrimitive = 1024;
    /// <summary>1 フレームあたりの図形数上限（Rust 側 MAX_PRIMITIVES3D_PER_FRAME と一致）。</summary>
    public const int MaxPrimitivesPerFrame = 4096;

    /// <summary>スタック上に確保する点バッファの上限（これを超える点列は配列を直接渡す）。</summary>
    private const int StackPointLimit = 64;

    // ── 既定値（マジックナンバー禁止）──────────────────────────

    /// <summary>円・円弧・リング・球の既定分割数。</summary>
    public const int DefaultSegments = 48;
    /// <summary>カプセル・矢尻の既定分割数（細かくしても見えないので控えめ）。</summary>
    public const int DefaultCoarseSegments = 16;
    /// <summary>線の既定の太さ（px）。</summary>
    private const float DefaultThicknessPx = 1f;
    /// <summary>全周を表す終了角（度）。</summary>
    private const float FullCircleDegrees = 360f;
    /// <summary>bool を float で渡すときの真値（データ表現規約）。</summary>
    private const float BoolTrue = 1f;
    /// <summary>bool を float で渡すときの偽値。</summary>
    private const float BoolFalse = 0f;

    // ── 線 ──────────────────────────────────────────────────────

    /// <summary>2 点を結ぶ直線を描く。</summary>
    /// <param name="a">始点（ワールド）。</param>
    /// <param name="b">終点（ワールド）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px。距離に依らず一定）。</param>
    /// <param name="depthTest">3D シーンに隠れるか（false = 常に手前）。</param>
    public static void Line(
        Vector3 a, Vector3 b, Color color,
        float thicknessPx = DefaultThicknessPx, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { a, b };
        // extras[0] = closed（直線なので 0）
        Submit(KindPolyline, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(BoolFalse), pts, 2);
    }

    /// <summary>折れ線を描く（釣り糸のたるみ・軌跡など）。</summary>
    /// <param name="points">頂点列（ワールド）。1024 点を超える分は切り捨てられる。</param>
    /// <param name="closed">true なら末尾と先頭を繋いで閉じる。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Polyline(
        Vector3[] points, bool closed, Color color,
        float thicknessPx = DefaultThicknessPx, bool depthTest = true)
    {
        SubmitArray(KindPolyline, points, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(closed ? BoolTrue : BoolFalse));
    }

    // ── 円・弧・リング ──────────────────────────────────────────

    /// <summary>任意の平面上に円を描く。</summary>
    /// <param name="center">中心（ワールド）。</param>
    /// <param name="normal">円が乗る平面の法線（正規化不要。0 なら真上扱い）。</param>
    /// <param name="radius">半径（ワールド単位）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">Outline = 輪郭線 / Fill = 円盤。</param>
    /// <param name="thicknessPx">輪郭の太さ（画面 px。Outline のときのみ有効）。</param>
    /// <param name="segments">円周の分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Circle(
        Vector3 center, Vector3 normal, float radius, Color color,
        DrawMode mode = DrawMode.Outline, float thicknessPx = DefaultThicknessPx,
        int segments = DefaultSegments, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { center, normal };
        Submit(KindCircle, color, mode, thicknessPx, depthTest,
            new Extras(radius, segments), pts, 2);
    }

    /// <summary>
    /// 任意の平面上にリング（内半径〜外半径の帯）を描く。常に塗りつぶし。
    /// 水面上の距離リングやフィールドの範囲表示に使う。
    /// </summary>
    /// <param name="center">中心（ワールド）。</param>
    /// <param name="normal">リングが乗る平面の法線。</param>
    /// <param name="innerRadius">内半径。0 なら扇形になる。</param>
    /// <param name="outerRadius">外半径。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="startDegrees">開始角（度）。0 度は法線から決まる基準軸方向。</param>
    /// <param name="endDegrees">終了角（度）。</param>
    /// <param name="segments">円周の分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Ring(
        Vector3 center, Vector3 normal, float innerRadius, float outerRadius, Color color,
        float startDegrees = 0f, float endDegrees = FullCircleDegrees,
        int segments = DefaultSegments, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { center, normal };
        Submit(KindRing, color, DrawMode.Fill, DefaultThicknessPx, depthTest,
            new Extras(innerRadius, outerRadius, startDegrees, endDegrees, segments), pts, 2);
    }

    /// <summary>任意の平面上に円弧（線）を描く。</summary>
    /// <param name="center">中心（ワールド）。</param>
    /// <param name="normal">円弧が乗る平面の法線。</param>
    /// <param name="radius">半径。</param>
    /// <param name="startDegrees">開始角（度）。</param>
    /// <param name="endDegrees">終了角（度）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px）。</param>
    /// <param name="segments">分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Arc(
        Vector3 center, Vector3 normal, float radius, float startDegrees, float endDegrees,
        Color color, float thicknessPx = DefaultThicknessPx,
        int segments = DefaultSegments, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { center, normal };
        Submit(KindArc, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(radius, startDegrees, endDegrees, segments), pts, 2);
    }

    // ── ワイヤフレーム形状 ──────────────────────────────────────

    /// <summary>ワイヤ球（直交する 3 つの大円）を描く。索敵範囲などの可視化に。</summary>
    /// <param name="center">中心（ワールド）。</param>
    /// <param name="radius">半径。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px）。</param>
    /// <param name="segments">各大円の分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void WireSphere(
        Vector3 center, float radius, Color color,
        float thicknessPx = DefaultThicknessPx, int segments = DefaultSegments,
        bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[1] { center };
        Submit(KindWireSphere, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(radius, segments), pts, 1);
    }

    /// <summary>ワイヤ直方体（12 辺）を描く。当たり判定・領域の可視化に。</summary>
    /// <param name="center">中心（ワールド）。</param>
    /// <param name="size">各軸の全長（幅・高さ・奥行き）。</param>
    /// <param name="rotationEulerDegrees">回転（度。エンジン本体と同じ YXZ 規約）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void WireBox(
        Vector3 center, Vector3 size, Vector3 rotationEulerDegrees, Color color,
        float thicknessPx = DefaultThicknessPx, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[1] { center };
        Submit(KindWireBox, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(size.x, size.y, size.z,
                rotationEulerDegrees.x, rotationEulerDegrees.y, rotationEulerDegrees.z),
            pts, 1);
    }

    /// <summary>ワイヤカプセル（両端の球 + 側面）を描く。キャラの当たり判定の可視化に。</summary>
    /// <param name="p0">一方の球の中心（ワールド）。</param>
    /// <param name="p1">もう一方の球の中心（ワールド）。</param>
    /// <param name="radius">半径。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="thicknessPx">線の太さ（画面 px）。</param>
    /// <param name="segments">円の分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void WireCapsule(
        Vector3 p0, Vector3 p1, float radius, Color color,
        float thicknessPx = DefaultThicknessPx, int segments = DefaultCoarseSegments,
        bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { p0, p1 };
        Submit(KindWireCapsule, color, DrawMode.Outline, thicknessPx, depthTest,
            new Extras(radius, segments), pts, 2);
    }

    // ── 面 ──────────────────────────────────────────────────────

    /// <summary>三角形を描く（塗りは両面・アンリット）。</summary>
    /// <param name="a">頂点 A（ワールド）。</param>
    /// <param name="b">頂点 B。</param>
    /// <param name="c">頂点 C。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">Fill = 塗り / Outline = 輪郭線。</param>
    /// <param name="thicknessPx">輪郭の太さ（画面 px。Outline のときのみ有効）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Triangle(
        Vector3 a, Vector3 b, Vector3 c, Color color,
        DrawMode mode = DrawMode.Fill, float thicknessPx = DefaultThicknessPx,
        bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[3] { a, b, c };
        Submit(KindPolygon, color, mode, thicknessPx, depthTest, default, pts, 3);
    }

    /// <summary>
    /// 四角形を描く（塗りは 2 三角形・両面・アンリット）。
    /// 頂点は<b>凸になる順（時計回り or 反時計回り）</b>で渡すこと。
    /// </summary>
    /// <param name="a">頂点 A（ワールド）。</param>
    /// <param name="b">頂点 B。</param>
    /// <param name="c">頂点 C。</param>
    /// <param name="d">頂点 D。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="mode">Fill = 塗り / Outline = 輪郭線。</param>
    /// <param name="thicknessPx">輪郭の太さ（画面 px。Outline のときのみ有効）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Quad(
        Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color,
        DrawMode mode = DrawMode.Fill, float thicknessPx = DefaultThicknessPx,
        bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[4] { a, b, c, d };
        Submit(KindPolygon, color, mode, thicknessPx, depthTest, default, pts, 4);
    }

    // ── 矢印・点 ────────────────────────────────────────────────

    /// <summary>矢印を描く（軸は線・矢尻は塗りつぶしの円錐）。</summary>
    /// <param name="from">始点（ワールド）。</param>
    /// <param name="to">終点（矢尻の先端）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="headLength">矢尻の長さ（ワールド単位。全長を超えるとクランプされる）。</param>
    /// <param name="headRadius">矢尻の根元の半径（ワールド単位）。</param>
    /// <param name="thicknessPx">軸の太さ（画面 px）。</param>
    /// <param name="segments">矢尻の円周分割数（3〜256 にクランプ）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Arrow(
        Vector3 from, Vector3 to, Color color, float headLength, float headRadius,
        float thicknessPx = DefaultThicknessPx, int segments = DefaultCoarseSegments,
        bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[2] { from, to };
        Submit(KindArrow, color, DrawMode.Fill, thicknessPx, depthTest,
            new Extras(headLength, headRadius, segments), pts, 2);
    }

    /// <summary>点を描く（常に画面を向く正方形。大きさは画面 px）。</summary>
    /// <param name="p">位置（ワールド）。</param>
    /// <param name="sizePx">一辺の長さ（画面 px）。</param>
    /// <param name="color">色（RGBA）。</param>
    /// <param name="depthTest">3D シーンに隠れるか。</param>
    public static void Point(Vector3 p, float sizePx, Color color, bool depthTest = true)
    {
        Vector3* pts = stackalloc Vector3[1] { p };
        Submit(KindPoint, color, DrawMode.Fill, DefaultThicknessPx, depthTest,
            new Extras(sizePx), pts, 1);
    }

    // ── 内部実装 ────────────────────────────────────────────────

    /// <summary>
    /// 図形別の追加スカラ（最大 6 個）。
    /// 意味は図形種別ごとに異なる（Rust 側 Primitive3dKind のコメントが正典）。
    /// </summary>
    private readonly struct Extras
    {
        public readonly float E0, E1, E2, E3, E4, E5;

        public Extras(
            float e0 = 0f, float e1 = 0f, float e2 = 0f,
            float e3 = 0f, float e4 = 0f, float e5 = 0f)
        {
            E0 = e0; E1 = e1; E2 = e2; E3 = e3; E4 = e4; E5 = e5;
        }
    }

    /// <summary>
    /// 配列で受け取った点列を（上限まで）スタックまたは固定配列から発行する。
    /// </summary>
    private static void SubmitArray(
        int kind, Vector3[] points, Color color, DrawMode mode,
        float thicknessPx, bool depthTest, Extras extras)
    {
        if (points == null || points.Length == 0) return;
        int n = points.Length > MaxPointsPerPrimitive ? MaxPointsPerPrimitive : points.Length;
        if (n <= StackPointLimit)
        {
            Vector3* buf = stackalloc Vector3[StackPointLimit];
            for (int i = 0; i < n; i++) buf[i] = points[i];
            Submit(kind, color, mode, thicknessPx, depthTest, extras, buf, n);
        }
        else
        {
            // 大きな点列はスタックを溢れさせないよう固定した配列を直接渡す
            fixed (Vector3* buf = points)
            {
                Submit(kind, color, mode, thicknessPx, depthTest, extras, buf, n);
            }
        }
    }

    /// <summary>
    /// パラメータ配列を組み立てて FFI へ 1 コマンド発行する（全図形の唯一の出口）。
    /// </summary>
    private static void Submit(
        int kind, Color color, DrawMode mode, float thicknessPx, bool depthTest,
        Extras extras, Vector3* points, int pointCount)
    {
        float* p = stackalloc float[ParamFloats];
        // 共通ヘッダ（Rust 側 PRIM3D_HEADER_FLOATS の並びと一致必須）
        p[0] = color.r; p[1] = color.g; p[2] = color.b; p[3] = color.a;
        p[4] = (float)mode;
        p[5] = thicknessPx;
        p[6] = depthTest ? BoolTrue : BoolFalse;
        // 図形別スカラ
        p[7] = extras.E0; p[8] = extras.E1; p[9] = extras.E2;
        p[10] = extras.E3; p[11] = extras.E4; p[12] = extras.E5;

        // Vector3 は float 3 個のみの構造体なので float* として渡せる
        ScriptHost.DrawPrimitive3D(kind, p, ParamFloats, (float*)points, pointCount);
    }
}
