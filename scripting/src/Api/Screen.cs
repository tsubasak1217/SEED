using System;

namespace SEED;

/// <summary>
/// 画面の情報（描画ターゲットの寸法・安全領域・向き・DPI）。
///
/// <para><b>座標系</b><br/>
/// <see cref="Width"/> / <see cref="Height"/> / <see cref="SafeArea"/> は <see cref="Input.MousePos"/> と同じ
/// 「描画ターゲットの左上原点・Y 下向き・ピクセル」。プロジェクト設定で解像度を固定しているとき
/// （レターボックス表示）は内部解像度の座標になる。
/// </para>
///
/// <para><b>値の更新</b><br/>
/// エンジンがフレームごとに 1 回（スクリプトの呼び出しより前に）差し替える。
/// 端末の回転や安全領域の変化はそのフレームの途中では反映されず、次のフレームから見える。
/// 同じフレームの間は何度読んでも同じ値になる。
/// </para>
///
/// <example>
/// <code>
/// // 安全領域の左上に HUD を寄せる（カメラ穴・ジェスチャーバーを避ける）
/// var safe = SEED.Screen.SafeArea;
/// hudPosition = new SEED.Vector2(safe.XMin + 16f, safe.YMin + 16f);
///
/// // 縦持ちと横持ちでレイアウトを切り替える
/// bool portrait = SEED.Screen.Orientation is SEED.ScreenOrientation.Portrait
///                                         or SEED.ScreenOrientation.PortraitUpsideDown;
/// </code>
/// </example>
/// </summary>
public static class Screen
{
    // 要素数と並びの位置（Rust 側 runtime/src/engine/core/scripting/screen_bridge.rs の
    // SCREEN_*_FLOATS / SCREEN_FIELD_* と一致させる）。

    /// <summary>寸法の問い合わせで返る要素数（幅, 高さ）。</summary>
    private const int SizeCount = 2;
    /// <summary>安全領域の問い合わせで返る要素数（x, y, 幅, 高さ）。</summary>
    private const int SafeAreaCount = 4;
    /// <summary>向き・DPI の問い合わせで返る要素数。</summary>
    private const int ScalarCount = 1;

    /// <summary>安全領域: 左上の X の位置。</summary>
    private const int IndexX = 0;
    /// <summary>安全領域: 左上の Y の位置。</summary>
    private const int IndexY = 1;
    /// <summary>安全領域: 幅の位置。</summary>
    private const int IndexWidth = 2;
    /// <summary>安全領域: 高さの位置。</summary>
    private const int IndexHeight = 3;
    /// <summary>寸法: 幅の位置。</summary>
    private const int IndexSizeWidth = 0;
    /// <summary>寸法: 高さの位置。</summary>
    private const int IndexSizeHeight = 1;
    /// <summary>向き・DPI: 値の位置。</summary>
    private const int IndexScalar = 0;

    /// <summary>
    /// DPI が取得できないとき（ホスト API 未登録など）に返す値。
    /// Windows の表示スケール 100% に当たる 96（エンジン側の既定 platform::DESKTOP_REFERENCE_DPI と同じ）。
    /// </summary>
    public const float DefaultDpi = 96f;

    /// <summary>描画ターゲットの幅（ピクセル。<see cref="Input.MousePos"/> と同じ単位）。取得できなければ 0。</summary>
    public static int Width
    {
        get
        {
            Span<float> v = stackalloc float[ScriptHost.ScreenQueryMaxFloats];
            return ScriptHost.ScreenQuery(ScriptHost.ScreenQuerySize, SizeCount, v) ? (int)v[IndexSizeWidth] : 0;
        }
    }

    /// <summary>描画ターゲットの高さ（ピクセル）。取得できなければ 0。</summary>
    public static int Height
    {
        get
        {
            Span<float> v = stackalloc float[ScriptHost.ScreenQueryMaxFloats];
            return ScriptHost.ScreenQuery(ScriptHost.ScreenQuerySize, SizeCount, v) ? (int)v[IndexSizeHeight] : 0;
        }
    }

    /// <summary>
    /// 安全領域（カメラ穴・切り欠き・ジェスチャーバーなどに隠れない範囲）。
    /// 座標は <see cref="Width"/> / <see cref="Height"/> の矩形の内側（左上原点）。
    /// 安全領域の無い環境（デスクトップ）では全画面（0, 0, Width, Height）。取得できなければ <see cref="Rect.Zero"/>。
    /// </summary>
    public static Rect SafeArea
    {
        get
        {
            Span<float> v = stackalloc float[ScriptHost.ScreenQueryMaxFloats];
            return ScriptHost.ScreenQuery(ScriptHost.ScreenQuerySafeArea, SafeAreaCount, v)
                ? new Rect(v[IndexX], v[IndexY], v[IndexWidth], v[IndexHeight])
                : Rect.Zero;
        }
    }

    /// <summary>
    /// 画面の向き。Android は端末の回転から 4 方向、デスクトップはウィンドウの縦横比から
    /// Portrait（縦長）/ LandscapeLeft（横長・正方形）のどちらか。取得できなければ LandscapeLeft。
    /// </summary>
    public static ScreenOrientation Orientation
    {
        get
        {
            Span<float> v = stackalloc float[ScriptHost.ScreenQueryMaxFloats];
            return ScriptHost.ScreenQuery(ScriptHost.ScreenQueryOrientation, ScalarCount, v)
                ? (ScreenOrientation)(int)v[IndexScalar]
                : ScreenOrientation.LandscapeLeft;
        }
    }

    /// <summary>
    /// OS が報告する論理 DPI（Android は DisplayMetrics.densityDpi、Windows は 96 × 表示スケール）。
    /// 取得できなければ <see cref="DefaultDpi"/>。
    /// </summary>
    public static float DPI
    {
        get
        {
            Span<float> v = stackalloc float[ScriptHost.ScreenQueryMaxFloats];
            return ScriptHost.ScreenQuery(ScriptHost.ScreenQueryDpi, ScalarCount, v) ? v[IndexScalar] : DefaultDpi;
        }
    }
}
