// ============================================================================
//  DialogueWindow.cs
//  会話窓（吹き出し・名札・本文・送りマーク）の「表示」だけを担当する。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 会話窓の表示担当コンポーネント。会話窓アクター（Actor2D）のルートに付ける。
///
/// 【責務】
///  1. 窓全体の表示／非表示（Show / Hide）
///  2. 名札の文字列差し替え（SetSpeaker）
///  3. 本文の 1 文字ずつの表示（BeginText / CompleteText / IsTextComplete）
///  4. 送りマークの点滅（本文を出し切っている間だけ）
///
/// 「次に何を喋るか」「入力で送るか」といった進行判断は持たない。
/// それは DialogueDirector の責務（単一責任）。
///
/// 【表示切替の実装方針】
/// アクター単位の表示 ON/OFF を行うスクリプト API は現状無い
/// （Model.Visible は 3D モデル専用で、2D の Sprite / Text には無い）。
/// そこで参照している Sprite / Text のアルファ値を 0 にすることで隠す。
/// 元の色はシーンで設定された値を初回に控え、Show 時にそれへ戻す。
///
/// 【シーン側の設定】
///  - 参照フィールドには同じ会話窓アクター配下の子を「子名|スロット名」で指定する
///    （例: DialogueBodyText|Text）。
/// </summary>
public class DialogueWindow : SEEDScript
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>完全に透明にするアルファ倍率。</summary>
    private const float AlphaScaleHidden = 0f;

    /// <summary>元の色をそのまま使うアルファ倍率。</summary>
    private const float AlphaScaleVisible = 1f;

    /// <summary>1 文字あたりの表示間隔（秒）の既定値。</summary>
    private const float DefaultCharInterval = 0.04f;

    /// <summary>文字送り効果音の既定パス（会話窓・チュートリアル窓で共通の「ぴこぴこ」音）。</summary>
    private const string DefaultMessageSePath = "assets://mainGame/audios/message.mp3";

    /// <summary>送りマークの点滅周期（秒）の既定値。</summary>
    private const float DefaultArrowBlinkPeriod = 0.8f;

    /// <summary>点滅周期がこれ以下なら点滅させない（0 除算回避）。</summary>
    private const float MinBlinkPeriod = 0.0001f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の下限。</summary>
    private const float ArrowBlinkMin = 0.15f;

    /// <summary>送りマークの点滅で取り得るアルファ倍率の上限。</summary>
    private const float ArrowBlinkMax = 1f;

    /// <summary>点滅周期を往路の長さへ直す係数（1 周期 = 往復）。</summary>
    private const float BlinkHalfPeriodRatio = 0.5f;

    /// <summary>話者名が未設定のときに表示する文字列。</summary>
    private const string EmptyText = "";

    // ── インスペクタ公開フィールド（参照）───────────────────

    /// <summary>吹き出し本体のスプライト（表示／非表示に使う）。</summary>
    [SerializeField(Label = "吹き出し", Tooltip = "吹き出し本体の Sprite（子名|Sprite）")]
    public SEED.Sprite? balloonSprite;

    /// <summary>名札（帯）のスプライト。</summary>
    [SerializeField(Label = "名札", Tooltip = "名札（帯）の Sprite（子名|Sprite）")]
    public SEED.Sprite? nameplateSprite;

    /// <summary>名札に載せる話者名の Text。</summary>
    [SerializeField(Label = "話者名テキスト", Tooltip = "名札に載せる話者名の Text（子名|Text）")]
    public SEED.Text? speakerText;

    /// <summary>本文の Text。</summary>
    [SerializeField(Label = "本文テキスト", Tooltip = "台詞本文の Text（子名|Text）")]
    public SEED.Text? bodyText;

    /// <summary>送りマーク（本文を出し切ったときだけ点滅表示する）。</summary>
    [SerializeField(Label = "送りマーク", Tooltip = "本文を出し切ったときに点滅する Sprite（子名|Sprite）")]
    public SEED.Sprite? nextArrowSprite;

    // ── インスペクタ公開フィールド（値）─────────────────────

    /// <summary>1 文字あたりの表示間隔（秒）。0 以下なら即座に全文表示する。</summary>
    [SerializeField(Label = "文字送り間隔(秒)", Tooltip = "1 文字を表示する間隔。0 で即時全文表示")]
    public float charInterval = DefaultCharInterval;

    /// <summary>送りマークの点滅周期（秒）。</summary>
    [SerializeField(Label = "送りマーク点滅周期(秒)", Tooltip = "送りマークが 1 往復するのに掛かる秒数")]
    public float arrowBlinkPeriod = DefaultArrowBlinkPeriod;

    /// <summary>
    /// 文字送り中に鳴らす効果音のアセットパス（空なら鳴らさない）。
    /// 実際の再生は <see cref="TypewriterText"/> が間隔を見て行う（窓は設定を渡すだけ）。
    /// </summary>
    [SerializeField(Label = "文字送りSE", Tooltip = "文字送り中に鳴らす効果音。空なら鳴らさない")]
    [AssetReference("mp3", "wav", "ogg")]
    public string messageSePath = DefaultMessageSePath;

    /// <summary>文字送り効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "文字送りSEの音量", Tooltip = "文字送り効果音の音量（0〜1）")]
    public float messageSeVolume = TypewriterText.DefaultSeVolume;

    /// <summary>
    /// 文字送り効果音の再生間隔（秒）。1 文字ごとではなくこの間隔で間引く。
    /// 実際の間隔は <see cref="messageSeLength"/> との大きい方（音が重ならないため）。
    /// </summary>
    [SerializeField(Label = "文字送りSEの間隔(秒)", Tooltip = "効果音を鳴らす間隔。音の実尺より短くしても重ねては鳴らさない")]
    public float messageSeInterval = TypewriterText.DefaultSeIntervalSeconds;

    /// <summary>
    /// 文字送り効果音の実尺（秒）。前の音が鳴り終わる前に次を鳴らさないための下限。
    /// 音を差し替えたら、その音の長さをここへ入れる。
    /// </summary>
    [SerializeField(Label = "文字送りSEの長さ(秒)", Tooltip = "効果音の実尺。この時間が経つまで次を鳴らさない（重複防止）")]
    public float messageSeLength = TypewriterText.DefaultSeLengthSeconds;

    /// <summary>
    /// 文字送り・送りマークの点滅を<b>実時間</b>（<see cref="SEED.Time.UnscaledDeltaTime"/>）で
    /// 進めるか。
    ///
    /// 既定は false（ゲーム時間）。プロローグのように時間停止を掛けないシーンでは
    /// これで問題ない。一方、本編のストーリー会話は <c>Time.Scale = 0</c> で
    /// ゲームを止めたまま流すため、false のままだと文字送りが 1 文字も進まない。
    /// そういうシーンでは true にすること。
    /// </summary>
    [SerializeField(Label = "実時間で進める", Tooltip = "Time.Scale = 0 で止めたまま会話を流すシーンでは true にする")]
    public bool useUnscaledTime;

    /// <summary>
    /// 話者名・本文に共通で使うフォントファイル（.ttf / .otf）の assets:// 参照。
    /// 空文字なら各 Text コンポーネント側の設定をそのまま使う（＝この機能を使わない）。
    /// </summary>
    [SerializeField(Label = "フォント", Tooltip = "話者名・本文に使うフォント（.ttf / .otf）。空なら各 Text の設定のまま")]
    [AssetReference("ttf", "otf")]
    public string fontPath = EmptyText;

    // ── 内部状態 ────────────────────────────────────────────

    /// <summary>シーンで設定された元の色を控えたか（初回アクセス時に一度だけ行う）。</summary>
    private bool _baseColorsCaptured;

    /// <summary>吹き出しの元の色。</summary>
    private SEED.Color _balloonBaseColor;

    /// <summary>名札の元の色。</summary>
    private SEED.Color _nameplateBaseColor;

    /// <summary>話者名テキストの元の色。</summary>
    private SEED.Color _speakerBaseColor;

    /// <summary>本文テキストの元の色。</summary>
    private SEED.Color _bodyBaseColor;

    /// <summary>送りマークの元の色。</summary>
    private SEED.Color _arrowBaseColor;

    /// <summary>窓を表示中か。</summary>
    private bool _visible;

    /// <summary>
    /// 名札（帯）と話者名 Text を表示すべきか。
    /// 話者名が空白のみ（ナレーション・心の声）のときは false になり、
    /// ApplyVisibility 内で窓全体の表示状態と AND を取って隠す。
    /// 既定値は true（未設定時は従来どおり名札を出す）。
    /// </summary>
    private bool _nameplateVisible = true;

    /// <summary>
    /// 文字送りの進行【文字送りロジックの唯一の置き場】。
    ///
    /// 書記素クラスタ単位の分解とインライン画像記法のまとめ扱いは
    /// <see cref="TypewriterText"/>（common/scripts/UI）が担当する。
    /// チュートリアル窓（TutorialWindow）と実装を共有するため純 C# クラスへ切り出してある。
    /// </summary>
    private readonly TypewriterText _writer = new();

    /// <summary>点滅用の経過時間（秒）。</summary>
    private float _blinkTimer;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>本文をすべて表示し終えているか（送り待ちの状態か）。</summary>
    public bool IsTextComplete => _writer.IsComplete;

    /// <summary>窓を表示中か。</summary>
    public bool IsVisible => _visible;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。シーンで設定された色を控えておく。
    /// </summary>
    public override void OnStart()
    {
        CaptureBaseColors();
        ApplyFont();
    }

    /// <summary>
    /// 毎フレーム、文字送りと送りマークの点滅を進める。
    /// </summary>
    /// <param name="ctx">フレーム情報（DeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 非表示中は何も進めない（隠したまま文字送りが進むのを防ぐ）
        if (!_visible) return;

        // ゲーム時間か実時間かは useUnscaledTime で切り替える（時間停止中の会話に対応）
        float deltaTime = useUnscaledTime ? SEED.Time.UnscaledDeltaTime : ctx.DeltaTime;

        AdvanceText(deltaTime);
        UpdateArrow(deltaTime);
    }

    // ── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 窓を表示する（各パーツの色を元の値へ戻す）。
    /// </summary>
    public void Show()
    {
        CaptureBaseColors();
        _visible = true;
        ApplyVisibility();
    }

    /// <summary>
    /// 窓を隠す（各パーツのアルファを 0 にする）。
    /// </summary>
    public void Hide()
    {
        CaptureBaseColors();
        _visible = false;
        ApplyVisibility();
    }

    /// <summary>
    /// 名札の話者名を差し替える。
    /// 話者名が空白のみ（null・空文字・スペースのみ）の場合はナレーション／心の声とみなし、
    /// 名札 Sprite と話者名 Text を非表示にする。話者名があれば表示に戻す。
    /// </summary>
    /// <param name="speaker">表示する話者名（null・空文字・空白文字のみなら名札ごと非表示）。</param>
    public void SetSpeaker(string speaker)
    {
        // Show/Hide より先に呼ばれる可能性があるため、ここでも一度控えておく
        CaptureBaseColors();

        // 空白のみならナレーション扱いとして名札を隠す
        _nameplateVisible = !string.IsNullOrWhiteSpace(speaker);

        if (speakerText is { } label && label.IsValid)
            label.Content = speaker ?? EmptyText;

        // 現在の窓表示状態と組み合わせて、名札の見た目へ即座に反映する
        ApplyVisibility();
    }

    /// <summary>
    /// 本文の 1 文字ずつの表示を開始する。
    /// </summary>
    /// <param name="text">表示する本文（改行コードで改行）。</param>
    public void BeginText(string text)
    {
        CaptureBaseColors();
        ApplyTypewriterSeSettings();

        _writer.Begin(text);
        _blinkTimer = 0f;

        // 文字送りを行わない設定なら最初から全文を出す（判断は共通ヘルパー側の閾値に従う）
        if (charInterval <= TypewriterText.MinCharInterval) { _writer.Complete(); }
        ApplyBodyContent();
    }

    /// <summary>
    /// 本文を最後まで一気に表示する（送り入力による全文表示に使う）。
    /// </summary>
    public void CompleteText()
    {
        if (_writer.Complete()) { ApplyBodyContent(); }
    }

    /// <summary>
    /// 文字送り効果音の設定を <see cref="_writer"/> へ渡す【SE 設定の唯一の受け渡し点】。
    /// 本文を差し替えるたびに呼ぶので、インスペクタでの変更がその場で効く。
    /// </summary>
    private void ApplyTypewriterSeSettings()
    {
        _writer.SePath            = messageSePath;
        _writer.SeVolume          = messageSeVolume;
        _writer.SeIntervalSeconds = messageSeInterval;
        _writer.SeLengthSeconds   = messageSeLength;
    }

    // ── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// インスペクタで指定されたフォントを話者名 Text と本文 Text へ適用する。
    ///
    /// 会話窓の中で書体が食い違わないよう、2 つの Text へまとめて同じフォントを流す。
    /// fontPath が空のときは何もしない（各 Text がシーンで持っている設定を尊重する）。
    /// </summary>
    private void ApplyFont()
    {
        if (string.IsNullOrEmpty(fontPath)) return;

        if (speakerText is { } speaker && speaker.IsValid) speaker.FontPath = fontPath;
        if (bodyText    is { } body    && body.IsValid)    body.FontPath    = fontPath;
    }

    /// <summary>
    /// シーンで設定された各パーツの色を初回だけ控える。
    ///
    /// OnStart だけでは足りない。参照元スクリプト（DialogueDirector）の処理が
    /// 自分の OnStart より先に走り、そこから Hide() が呼ばれることがあり得るため、
    /// 公開 API の入口でも呼んで「必ず控えてから触る」ようにしている。
    /// </summary>
    private void CaptureBaseColors()
    {
        if (_baseColorsCaptured) return;
        _baseColorsCaptured = true;

        if (balloonSprite   is { } balloon   && balloon.IsValid)   _balloonBaseColor   = balloon.Color;
        if (nameplateSprite is { } nameplate && nameplate.IsValid) _nameplateBaseColor = nameplate.Color;
        if (speakerText     is { } speaker   && speaker.IsValid)   _speakerBaseColor   = speaker.Color;
        if (bodyText        is { } body      && body.IsValid)      _bodyBaseColor      = body.Color;
        if (nextArrowSprite is { } arrow     && arrow.IsValid)     _arrowBaseColor     = arrow.Color;
    }

    /// <summary>
    /// 現在の表示状態を各パーツの色へ反映する。
    /// 送りマークは「本文完了時のみ表示」なのでここでは必ず消し、
    /// UpdateArrow が毎フレーム上書きする。
    /// </summary>
    private void ApplyVisibility()
    {
        float alphaScale = _visible ? AlphaScaleVisible : AlphaScaleHidden;

        // 名札（帯）と話者名 Text は「窓が見えている」かつ「話者名がある」の両方を満たすときだけ表示する。
        // 窓が隠れていれば当然名札も出ないし、窓が見えていても話者名が空白なら名札だけ隠す。
        float nameplateAlphaScale = (_visible && _nameplateVisible) ? AlphaScaleVisible : AlphaScaleHidden;

        if (balloonSprite   is { } balloon   && balloon.IsValid)
            balloon.Color = _balloonBaseColor.WithAlpha(_balloonBaseColor.a * alphaScale);
        if (nameplateSprite is { } nameplate && nameplate.IsValid)
            nameplate.Color = _nameplateBaseColor.WithAlpha(_nameplateBaseColor.a * nameplateAlphaScale);
        if (speakerText     is { } speaker   && speaker.IsValid)
            speaker.Color = _speakerBaseColor.WithAlpha(_speakerBaseColor.a * nameplateAlphaScale);
        if (bodyText        is { } body      && body.IsValid)
            body.Color = _bodyBaseColor.WithAlpha(_bodyBaseColor.a * alphaScale);
        if (nextArrowSprite is { } arrow     && arrow.IsValid)
            arrow.Color = _arrowBaseColor.WithAlpha(_arrowBaseColor.a * AlphaScaleHidden);
    }

    /// <summary>
    /// 経過時間ぶんだけ文字送りを進める。
    /// </summary>
    /// <param name="deltaTime">前フレームからの経過秒。</param>
    private void AdvanceText(float deltaTime)
    {
        // 進んだフレームだけ Text へ書き戻す（毎フレームの無駄な代入を避ける）
        if (_writer.Advance(deltaTime, charInterval)) { ApplyBodyContent(); }
    }

    /// <summary>
    /// 送りマークの表示を更新する（本文完了時のみ点滅表示）。
    /// </summary>
    /// <param name="deltaTime">前フレームからの経過秒。</param>
    private void UpdateArrow(float deltaTime)
    {
        if (nextArrowSprite is not { } arrow || !arrow.IsValid) return;

        // 本文がまだ流れている間は出さない（もう送れると誤解させないため）
        if (!IsTextComplete)
        {
            _blinkTimer = 0f;
            arrow.Color = _arrowBaseColor.WithAlpha(_arrowBaseColor.a * AlphaScaleHidden);
            return;
        }

        _blinkTimer += deltaTime;

        // 周期が実質 0 なら点滅させず出しっぱなしにする
        float blink = ArrowBlinkMax;
        if (arrowBlinkPeriod > MinBlinkPeriod)
        {
            // 往復 1 周期の三角波を作り、下限を持たせて完全には消さない
            float half = arrowBlinkPeriod * BlinkHalfPeriodRatio;
            float wave = SEED.Mathf.PingPong(_blinkTimer, half) / half;
            blink = SEED.Mathf.Lerp(ArrowBlinkMin, ArrowBlinkMax, wave);
        }

        arrow.Color = _arrowBaseColor.WithAlpha(_arrowBaseColor.a * blink);
    }

    /// <summary>
    /// 現在の表示ぶんを本文 Text へ反映する。
    /// </summary>
    private void ApplyBodyContent()
    {
        if (bodyText is { } body && body.IsValid)
            body.Content = _writer.Shown;
    }
}
