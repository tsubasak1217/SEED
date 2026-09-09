// ============================================================================
//  WaterSplashSettings.cs
//  水しぶき（波紋＋水柱）の「規模」を決める設定値のかたまり。
// ============================================================================

/// <summary>
/// 水しぶきの生成パラメータ【しぶきの規模を表す唯一のデータ型】。
///
/// <b>なぜ独立した型にするか</b>
/// しぶきを出したい側（<see cref="CatchPresenter"/> など）はインスペクタで
/// 数値を調整したいが、実際に生成する処理（<see cref="WaterSplashSpawner"/>）は
/// 静的クラスでシーンに置きたくない。そこで「調整値の入れ物」だけを本型に切り出し、
/// <b>呼び出し側がインスペクタ公開したフィールドから組み立てて渡す</b>形にしている。
/// これにより
/// <list type="bullet">
///   <item>生成処理はシーンへの配置・結線が一切不要（静的クラスのまま使える）</item>
///   <item>調整値はしぶきを出す側のインスペクタに並ぶ（＝演出ごとに別々に詰められる）</item>
///   <item>既定値（<see cref="Default"/>）だけで動くので、未設定でも破綻しない</item>
/// </list>
///
/// <b>「規模」の決まり方</b>
/// 各項目は Min／Max の対で持ち、魚の大きさから作った強度
/// （<c>intensity01</c>：0〜1）で<b>線形補間</b>して使う。
/// 補間そのものは <see cref="WaterSplashSpawner"/> が行う（式の置き場を 1 か所に保つ）。
/// </summary>
public struct WaterSplashSettings
{
    // ─── 既定値（マジックナンバー禁止のための名前付き定数）───────

    /// <summary>波紋アクタの既定パス。</summary>
    public const string DefaultRippleActorPath = "assets://mainGame/actors/Effects/WaterRipple.actor";

    /// <summary>水柱アクタの既定パス。</summary>
    public const string DefaultColumnActorPath = "assets://mainGame/actors/Effects/WaterColumn.actor";

    /// <summary>波紋の個数の既定（最小強度）。</summary>
    public const int DefaultRippleCountMin = 2;

    /// <summary>波紋の個数の既定（最大強度）。</summary>
    public const int DefaultRippleCountMax = 7;

    /// <summary>波紋を散らす半径の既定（最小強度・メートル）。</summary>
    public const float DefaultRippleRadiusMin = 0.35f;

    /// <summary>波紋を散らす半径の既定（最大強度・メートル）。</summary>
    public const float DefaultRippleRadiusMax = 1.60f;

    /// <summary>波紋のスケール倍率の既定（最小強度）。</summary>
    public const float DefaultRippleScaleMin = 0.60f;

    /// <summary>波紋のスケール倍率の既定（最大強度）。</summary>
    public const float DefaultRippleScaleMax = 2.20f;

    /// <summary>水柱の本数の既定（最小強度）。中心の 1 本を含む。</summary>
    public const int DefaultColumnCountMin = 1;

    /// <summary>水柱の本数の既定（最大強度）。中心の 1 本を含む。</summary>
    public const int DefaultColumnCountMax = 5;

    /// <summary>水柱を散らす半径の既定（最小強度・メートル）。</summary>
    public const float DefaultColumnRadiusMin = 0.15f;

    /// <summary>水柱を散らす半径の既定（最大強度・メートル）。</summary>
    public const float DefaultColumnRadiusMax = 0.70f;

    /// <summary>水柱のスケール倍率の既定（最小強度）。</summary>
    public const float DefaultColumnScaleMin = 0.50f;

    /// <summary>水柱のスケール倍率の既定（最大強度）。</summary>
    public const float DefaultColumnScaleMax = 1.80f;

    /// <summary>個体ごとのスケールのばらつきの既定（±の割合）。</summary>
    public const float DefaultScaleJitter = 0.25f;

    /// <summary>水面から浮かせる高さの既定（メートル。水面との Z ファイト避け）。</summary>
    public const float DefaultSurfaceOffsetY = 0.02f;

    // ─── 参照するアクタ ───────────────────────────────────────

    /// <summary>波紋アクタの <c>assets://</c> パス。空なら波紋を出さない。</summary>
    public string rippleActorPath;

    /// <summary>水柱アクタの <c>assets://</c> パス。空なら水柱を出さない。</summary>
    public string columnActorPath;

    // ─── 波紋（強度で補間する対）─────────────────────────────

    /// <summary>波紋の個数（強度 0）。</summary>
    public int rippleCountMin;

    /// <summary>波紋の個数（強度 1）。</summary>
    public int rippleCountMax;

    /// <summary>波紋を散らす半径（強度 0・メートル）。</summary>
    public float rippleRadiusMin;

    /// <summary>波紋を散らす半径（強度 1・メートル）。</summary>
    public float rippleRadiusMax;

    /// <summary>波紋のスケール倍率（強度 0）。アクタ側の既定スケールに掛かる。</summary>
    public float rippleScaleMin;

    /// <summary>波紋のスケール倍率（強度 1）。アクタ側の既定スケールに掛かる。</summary>
    public float rippleScaleMax;

    // ─── 水柱（強度で補間する対）─────────────────────────────

    /// <summary>水柱の本数（強度 0）。中心の 1 本を含む。</summary>
    public int columnCountMin;

    /// <summary>水柱の本数（強度 1）。中心の 1 本を含む。</summary>
    public int columnCountMax;

    /// <summary>水柱を散らす半径（強度 0・メートル）。</summary>
    public float columnRadiusMin;

    /// <summary>水柱を散らす半径（強度 1・メートル）。</summary>
    public float columnRadiusMax;

    /// <summary>水柱のスケール倍率（強度 0）。</summary>
    public float columnScaleMin;

    /// <summary>水柱のスケール倍率（強度 1）。</summary>
    public float columnScaleMax;

    // ─── 共通 ─────────────────────────────────────────────────

    /// <summary>
    /// 1 個ごとのスケールのばらつき（±の割合。0.25 なら 0.75〜1.25 倍）。
    /// 0 で全個体が同じ大きさになる。
    /// </summary>
    public float scaleJitter;

    /// <summary>水面から浮かせる高さ（メートル）。水面と重なってちらつくのを防ぐ。</summary>
    public float surfaceOffsetY;

    /// <summary>
    /// 何も設定しないときに使う既定値一式。
    /// インスペクタ側の初期値もこの定数群を参照するので、
    /// 「コードの既定」と「インスペクタの既定」が食い違わない。
    /// </summary>
    public static WaterSplashSettings Default => new()
    {
        rippleActorPath = DefaultRippleActorPath,
        columnActorPath = DefaultColumnActorPath,
        rippleCountMin  = DefaultRippleCountMin,
        rippleCountMax  = DefaultRippleCountMax,
        rippleRadiusMin = DefaultRippleRadiusMin,
        rippleRadiusMax = DefaultRippleRadiusMax,
        rippleScaleMin  = DefaultRippleScaleMin,
        rippleScaleMax  = DefaultRippleScaleMax,
        columnCountMin  = DefaultColumnCountMin,
        columnCountMax  = DefaultColumnCountMax,
        columnRadiusMin = DefaultColumnRadiusMin,
        columnRadiusMax = DefaultColumnRadiusMax,
        columnScaleMin  = DefaultColumnScaleMin,
        columnScaleMax  = DefaultColumnScaleMax,
        scaleJitter     = DefaultScaleJitter,
        surfaceOffsetY  = DefaultSurfaceOffsetY,
    };
}
