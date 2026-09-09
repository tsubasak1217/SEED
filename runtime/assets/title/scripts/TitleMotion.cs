// ============================================================================
//  TitleMotion.cs
//  タイトル画面の「ロゴ」と「Click to Start」の常時モーション。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// タイトル画面のロゴと開始文言を動かし続ける演出。
///
/// 【責務】
///  - 開始文言（Text）: <c>|sin|</c> で上下させ、跳ねている感じにする
///    （谷で一瞬止まって跳ね返る波形になる）。
///  - ロゴ（Sprite）: ゆっくり・かすかに左右へ傾け、わずかに上下させる。
///
/// 【シーン側の設定】
///  タイトルの進行（<see cref="TitleFlow"/>）と同じアクタに付けるだけでよい。
///  動かす対象はアクタ名のパス（既定 <c>LogoCanvas/Sprite</c> / <c>LogoCanvas/Actor</c>）で
///  <see cref="OnStart"/> に解決するので、シーンの結線は不要（名前を変えたら Inspector で合わせる）。
///  基準位置はシーンに保存された座標をそのまま使う（開始時に控える）。
///
/// 時間は実時間（<c>Time.UnscaledElapsedTime</c>）で数え、Time.Scale の影響を受けない。
/// </summary>
public class TitleMotion : SEEDScript
{
    // ── 定数 ────────────────────────────────────────────────

    /// <summary>1 周（ラジアン）。周波数から角速度を作るのに使う。</summary>
    private const float TwoPi = 2f * 3.14159265f;

    // ── インスペクタ公開フィールド（対象） ──────────────────

    /// <summary>ロゴ（Sprite を持つ 2D アクタ）へのパス。空にするとロゴは動かさない。</summary>
    [Header("対象"), SerializeField(Label = "ロゴのアクタパス")]
    private string logoActorPath = "LogoCanvas/Sprite";

    /// <summary>開始文言（Text を持つ 2D アクタ）へのパス。空にすると文言は動かさない。</summary>
    [SerializeField(Label = "開始文言のアクタパス")]
    private string startTextActorPath = "LogoCanvas/Actor";

    // ── インスペクタ公開フィールド（文言の跳ね） ────────────

    /// <summary>文言が跳ねる高さ（キャンバス px。上向き）。</summary>
    [Header("開始文言の跳ね"), SerializeField(Label = "跳ねる高さ(px)")]
    private float textBounceHeightPx = 12f;

    /// <summary>文言が 1 秒に跳ねる回数（|sin| なので sin の半周期＝1 回の跳ね）。</summary>
    [SerializeField(Label = "跳ねる回数(回/秒)")]
    private float textBouncesPerSecond = 1.2f;

    // ── インスペクタ公開フィールド（ロゴの揺れ） ────────────

    /// <summary>ロゴの傾きの振幅（度）。かすかに揺らすので小さめ。</summary>
    [Header("ロゴの揺れ"), SerializeField(Label = "傾きの振幅(度)")]
    private float logoSwayDegrees = 1.5f;

    /// <summary>ロゴの傾きが 1 往復する秒数。</summary>
    [SerializeField(Label = "傾きの周期(秒)")]
    private float logoSwayPeriodSeconds = 4.0f;

    /// <summary>ロゴの上下の振幅（キャンバス px）。0 で上下させない。</summary>
    [SerializeField(Label = "上下の振幅(px)")]
    private float logoDriftPx = 6f;

    /// <summary>ロゴの上下が 1 往復する秒数（傾きと別の周期にすると単調さが減る）。</summary>
    [SerializeField(Label = "上下の周期(秒)")]
    private float logoDriftPeriodSeconds = 5.3f;

    // ── 実行時の状態 ────────────────────────────────────────

    /// <summary>ロゴの CanvasTransform（未解決なら null）。</summary>
    private SEED.CanvasTransform? logoTransform = null;

    /// <summary>開始文言の CanvasTransform（未解決なら null）。</summary>
    private SEED.CanvasTransform? textTransform = null;

    /// <summary>ロゴの基準位置（シーン保存値。開始時に控える）。</summary>
    private SEED.Vector2 logoBasePosition;

    /// <summary>ロゴの基準回転（度。開始時に控える）。</summary>
    private float logoBaseRotation;

    /// <summary>開始文言の基準位置（シーン保存値。開始時に控える）。</summary>
    private SEED.Vector2 textBasePosition;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>対象をパスで解決し、基準位置を控える。</summary>
    public override void OnStart()
    {
        logoTransform = ResolveCanvasTransform(logoActorPath);
        textTransform = ResolveCanvasTransform(startTextActorPath);

        if (logoTransform is { } lt)
        {
            logoBasePosition = lt.Position;
            logoBaseRotation = lt.Rotation;
        }
        if (textTransform is { } tt)
        {
            textBasePosition = tt.Position;
        }
    }

    /// <summary>毎フレーム、実時間で波形を評価して位置・回転を書き込む。</summary>
    /// <param name="ctx">フレーム情報（未使用。時間は実時間の累計を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        float t = SEED.Time.UnscaledElapsedTime;

        // 開始文言: |sin| で跳ねる。キャンバスの Y は下向きなので、上へ跳ねるには引く。
        if (textTransform is { IsValid: true } text)
        {
            float phase = t * textBouncesPerSecond * 3.14159265f;   // |sin| の半周期＝1 跳ね
            float lift = SEED.Mathf.Abs(SEED.Mathf.Sin(phase)) * textBounceHeightPx;
            text.Position = new SEED.Vector2(textBasePosition.x, textBasePosition.y - lift);
        }

        // ロゴ: ゆっくり傾き、別周期でかすかに上下する。
        if (logoTransform is { IsValid: true } logo)
        {
            float swayPeriod = SEED.Mathf.Max(logoSwayPeriodSeconds, float.Epsilon);
            float driftPeriod = SEED.Mathf.Max(logoDriftPeriodSeconds, float.Epsilon);
            float sway = SEED.Mathf.Sin(t * TwoPi / swayPeriod) * logoSwayDegrees;
            float drift = SEED.Mathf.Sin(t * TwoPi / driftPeriod) * logoDriftPx;
            logo.Rotation = logoBaseRotation + sway;
            logo.Position = new SEED.Vector2(logoBasePosition.x, logoBasePosition.y + drift);
        }
    }

    // ── 内部 ────────────────────────────────────────────────

    /// <summary>
    /// アクタ名のパス（<c>Root/Child</c>）から CanvasTransform を取る。
    /// 空パス・見つからない・2D でない場合は null。
    /// </summary>
    /// <param name="path">シーンのルートからのアクタ名パス。</param>
    private static SEED.CanvasTransform? ResolveCanvasTransform(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }

        // 先頭の名前はシーン全体から探し、残りは子孫パスとして辿る
        // （GameObject.Find は名前検索のみで、パス書式は FindChild が解釈する）。
        int slash = path.IndexOf(PathSeparator);
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        var go = SEED.GameObject.Find(rootName);
        if (!go.IsValid) { return null; }
        if (slash >= 0)
        {
            go = go.FindChild(path.Substring(slash + 1));
            if (!go.IsValid) { return null; }
        }
        return go.GetComponent<SEED.CanvasTransform>();
    }

    /// <summary>アクタパスの区切り文字。</summary>
    private const char PathSeparator = '/';
}
