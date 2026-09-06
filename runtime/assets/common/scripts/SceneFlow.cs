// ============================================================================
//  SceneFlow.cs
//  シーン遷移とフェード演出を担う共通コンポーネント。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// シーン遷移の共通コンポーネント。
///
/// 【責務】
///  1. 画面全体を覆う矩形の α（フェード）を毎フレーム更新・描画する。
///  2. GoTo(sceneName) が呼ばれたら「事前読み込み → フェードアウト →
///     シーン切替」という定型手順を 1 か所で実行する。
///
/// 各シーン固有の「いつ・どこへ遷移するか」の判断はこのクラスには書かない。
/// タイトル／プロローグ／チュートリアルの進行スクリプト（TitleFlow 等）が
/// 参照フィールド経由でこのインスタンスを持ち、GoTo() を呼ぶ。
/// これによりフェード演出の実装は 1 つで済み、進行スクリプトはシーン固有の
/// 分岐だけに集中できる（単一責任）。
///
/// 【使い方（シーン側）】
///  - 遷移を担当するアクター（例: "Flow"）に本スクリプトを付ける。
///  - 同じアクターへ各シーンの進行スクリプトを付け、その参照フィールド
///    sceneFlow に同じアクターを指定する。
///  - debugForceFromPrologue を true にすると、セーブデータの進行フラグを
///    無視して常にプロローグ→チュートリアルの順で通す（開発用デバッグ機能）。
///
/// 【遷移の時間軸】
///   フレーム N     : GoTo() で Scene.Load(遷移先) を発行、フェードアウト開始
///   フレーム N+k   : フェードアウト完了（α = 1）
///   フレーム N+k+1 : Scene.Transition(遷移先) を発行（このフレームでは他の
///                    シーン操作を一切行わない。ランタイム仕様上、Transition と
///                    同フレームの Instantiate / Destroy は破棄されるため）
/// </summary>
public class SceneFlow : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>フェード矩形の描画レイヤー。他の UI より確実に手前へ出すための大きな値。</summary>
    private const int FadeDrawLayer = 30000;

    /// <summary>
    /// フェード矩形の一辺の長さ（px）。
    /// スクリーンスペース描画に画面サイズ取得 API が無いため、
    /// 想定しうる解像度を十分に超える固定サイズで画面外まで塗り潰す。
    /// </summary>
    private const float FadeCoverExtent = 16384f;

    /// <summary>フェード矩形の中心（スクリーンスペース原点＝画面左上）。ここを中心に広げて全画面を覆う。</summary>
    private const float FadeCoverCenter = 0f;

    /// <summary>フェード時間がこの値以下なら「即時」とみなす（0 除算回避）。</summary>
    private const float MinFadeDuration = 0.0001f;

    /// <summary>完全に透明な α。</summary>
    private const float AlphaClear = 0f;

    /// <summary>完全に不透明な α。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>フェードの既定時間（秒）。</summary>
    private const float DefaultFadeDuration = 0.5f;

    /// <summary>フェード色の既定値（黒）。</summary>
    private const float DefaultFadeColorChannel = 0f;

    /// <summary>「常にプロローグから通す」デバッグフラグの既定値（本番相当の挙動＝セーブデータに従う）。</summary>
    private const bool DefaultDebugForceFromPrologue = false;

    // ── フェードの進行状態 ──────────────────────────────────

    /// <summary>フェード／遷移の進行段階。</summary>
    private enum FlowState
    {
        /// <summary>何もしていない（フェード完全解除・入力受付可）。</summary>
        Idle,
        /// <summary>暗転から明転していく途中（シーン開始直後）。</summary>
        FadingIn,
        /// <summary>暗転していく途中（遷移予約済み）。</summary>
        FadingOut,
        /// <summary>暗転完了。次フレームで Scene.Transition を発行する。</summary>
        WaitingTransition,
        /// <summary>Transition 発行済み。以降は暗転したまま何もしない。</summary>
        Transitioning,
    }

    // ── インスペクタ公開フィールド ──────────────────────────

    /// <summary>Go() で遷移する既定の遷移先シーン名（プロジェクト設定のシーンマネージャ登録名）。</summary>
    [SerializeField(Label = "既定の遷移先シーン", Tooltip = "Go() で遷移するシーン名（プロジェクト設定の登録名）")]
    public string nextScene = "";

    /// <summary>フェードイン／フェードアウトに掛ける秒数。</summary>
    [SerializeField(Label = "フェード時間(秒)", Tooltip = "フェードイン・フェードアウトそれぞれに掛かる秒数")]
    public float fadeDuration = DefaultFadeDuration;

    /// <summary>フェード色 R 成分（0〜1）。Color 型はインスペクタ非対応のため 3 成分に分けて公開する。</summary>
    [SerializeField(Label = "フェード色 R")]
    public float fadeColorR = DefaultFadeColorChannel;

    /// <summary>フェード色 G 成分（0〜1）。</summary>
    [SerializeField(Label = "フェード色 G")]
    public float fadeColorG = DefaultFadeColorChannel;

    /// <summary>フェード色 B 成分（0〜1）。</summary>
    [SerializeField(Label = "フェード色 B")]
    public float fadeColorB = DefaultFadeColorChannel;

    /// <summary>シーン開始時にフェード色からフェードインするか。</summary>
    [SerializeField(Label = "開始時にフェードイン")]
    public bool fadeInOnStart = true;

    /// <summary>
    /// 【デバッグ用】true の間、セーブデータのチュートリアル完了フラグを無視して
    /// タイトルから常にプロローグ→チュートリアルの順で遷移させる（TitleFlow が参照）。
    /// 本番出荷時は false のままにしておくこと。
    /// </summary>
    [SerializeField(Label = "【デバッグ】常にプロローグから通す", Tooltip = "true のときセーブデータのチュートリアル完了フラグを無視し、タイトルから必ずプロローグ→チュートリアルの順で通す（開発用）")]
    public bool debugForceFromPrologue = DefaultDebugForceFromPrologue;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>現在の進行段階。</summary>
    private FlowState _state = FlowState.Idle;

    /// <summary>現在のフェード α（0 = 透明・1 = フェード色で完全に覆う）。</summary>
    private float _alpha = AlphaClear;

    /// <summary>フェードアウト完了後に切り替える先のシーン名。</summary>
    private string _pendingScene = "";

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>
    /// 遷移処理中か（フェードアウト開始〜Transition 発行済み）。
    /// 進行スクリプトは true の間、入力を無視して二重遷移を防ぐ。
    /// </summary>
    public bool IsTransitioning =>
        _state == FlowState.FadingOut ||
        _state == FlowState.WaitingTransition ||
        _state == FlowState.Transitioning;

    // ── 静的ユーティリティ ──────────────────────────────────

    /// <summary>
    /// 「決定」入力が押された瞬間か。
    ///
    /// 各シーンの進行スクリプトが同じ操作感になるよう、判定をここへ集約する。
    /// 現状のエンジンにゲームパッド入力 API が無いため、キーボードの
    /// Enter / Space のみを決定として扱う。
    /// </summary>
    /// <returns>このフレームに決定キーが押されたなら true。</returns>
    public static bool IsConfirmPressed()
        => SEED.Input.GetKeyDown(SEED.KeyCode.Enter)
        || SEED.Input.GetKeyDown(SEED.KeyCode.Space);

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。フェードインを行う設定なら「完全に覆った状態」から開始する。
    /// </summary>
    public override void OnStart()
    {
        if (fadeInOnStart)
        {
            // 暗転済みの状態から明転を始める
            _alpha = AlphaOpaque;
            _state = FlowState.FadingIn;
        }
        else
        {
            // フェードなしで即プレイ可能な状態にする
            _alpha = AlphaClear;
            _state = FlowState.Idle;
        }
    }

    /// <summary>
    /// 毎フレーム、フェードの進行と描画を行う。
    /// フェードアウト完了の「次のフレーム」でシーン切替を発行する。
    /// </summary>
    /// <param name="ctx">フレーム情報（DeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 1) 暗転完了の次フレーム: シーン切替を発行する。
        //    このフレームでは他のシーン操作を行わない（Transition と同フレームの
        //    Instantiate / Destroy は破棄される仕様のため）。
        if (_state == FlowState.WaitingTransition)
        {
            SEED.Scene.Transition(_pendingScene);
            _state = FlowState.Transitioning;
            DrawFadeOverlay();
            return;
        }

        // 2) フェードの進行（α を時間で動かす）
        UpdateFadeAlpha(ctx.DeltaTime);

        // 3) 今フレームの α で全画面を覆う（α が 0 のときは描画しない）
        DrawFadeOverlay();
    }

    // ── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 既定の遷移先（nextScene）へ遷移する。
    /// </summary>
    /// <returns>遷移を受け付けたら true。</returns>
    public bool Go() => GoTo(nextScene);

    /// <summary>
    /// 指定シーンへフェードアウトしながら遷移する。
    ///
    /// 呼び出し時点で遷移先を事前読み込み（Scene.Load）しておき、
    /// フェードアウトに掛かる時間をロード時間として使う。
    /// 実際の切替（Scene.Transition）はフェード完了後のフレームで行う。
    /// </summary>
    /// <param name="sceneName">遷移先シーン名（プロジェクト設定の登録名、または assets:// パス）。</param>
    /// <returns>遷移を受け付けたら true。既に遷移中／シーン名が空なら false。</returns>
    public bool GoTo(string sceneName)
    {
        // 二重遷移の防止（フェードアウト中の再要求は無視する）
        if (IsTransitioning) return false;

        // 遷移先未設定は事故（無言で暗転したまま止まる）になるためログを出して弾く
        if (string.IsNullOrEmpty(sceneName))
        {
            SEED.Debug.LogWarning("[SceneFlow] 遷移先シーン名が空です。遷移を中止しました。");
            return false;
        }

        // 事前読み込み（現在のシーンはそのまま。フェード中にロードが進む）
        SEED.Scene.Load(sceneName);
        _pendingScene = sceneName;

        // フェードアウト開始（フェード時間が実質 0 なら即座に暗転完了扱い）
        _state = FlowState.FadingOut;
        if (fadeDuration <= MinFadeDuration)
        {
            _alpha = AlphaOpaque;
            _state = FlowState.WaitingTransition;
        }
        return true;
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 進行段階に応じて α を時間で更新し、完了したら次の段階へ進める。
    /// </summary>
    /// <param name="deltaTime">前フレームからの経過秒。</param>
    private void UpdateFadeAlpha(float deltaTime)
    {
        // フェード時間が実質 0 のときは 1 フレームで完了させる（0 除算回避）
        float step = (fadeDuration <= MinFadeDuration)
            ? AlphaOpaque
            : deltaTime / fadeDuration;

        switch (_state)
        {
            case FlowState.FadingIn:
                // 覆いを薄くしていき、透明になったら通常状態へ
                _alpha = SEED.Mathf.Clamped01(_alpha - step);
                if (_alpha <= AlphaClear) _state = FlowState.Idle;
                break;

            case FlowState.FadingOut:
                // 覆いを濃くしていき、完全に覆ったら次フレームの切替を待つ
                _alpha = SEED.Mathf.Clamped01(_alpha + step);
                if (_alpha >= AlphaOpaque) _state = FlowState.WaitingTransition;
                break;

            default:
                // Idle / WaitingTransition / Transitioning では α を動かさない
                break;
        }
    }

    /// <summary>
    /// 現在の α で画面全体を覆う矩形を描画する。
    /// Draw はイミディエイト描画（そのフレームだけ）なので毎フレーム呼ぶ必要がある。
    /// α が 0 のときは描画コマンドを積まない。
    /// </summary>
    private void DrawFadeOverlay()
    {
        if (_alpha <= AlphaClear) return;

        SEED.Draw.Rect(
            new SEED.Vector2(FadeCoverCenter, FadeCoverCenter),
            new SEED.Vector2(FadeCoverExtent, FadeCoverExtent),
            new SEED.Color(fadeColorR, fadeColorG, fadeColorB, _alpha),
            layer: FadeDrawLayer);
    }
}
