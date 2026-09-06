// ============================================================================
//  DialogueWindow.cs
//  会話窓（吹き出し・名札・本文・送りマーク）の「表示」だけを担当する。
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

    /// <summary>送りマークの点滅周期（秒）の既定値。</summary>
    private const float DefaultArrowBlinkPeriod = 0.8f;

    /// <summary>これ以下の間隔は「即時全表示」とみなす（0 除算・無限ループ回避）。</summary>
    private const float MinCharInterval = 0.0001f;

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

    /// <summary>1 フレームに進められる最大文字数（極端な DeltaTime での暴走を防ぐ）。</summary>
    private const int MaxCharsPerFrame = 64;

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
    /// 表示対象の本文を書記素クラスタ（見た目 1 文字）単位に分解したもの。
    /// サロゲートペア・結合文字を途中で切らないため char 単位ではなくこれを使う。
    /// </summary>
    private readonly List<string> _textElements = new();

    /// <summary>現在までに表示した文字数（書記素クラスタ数）。</summary>
    private int _shownCount;

    /// <summary>文字送りの端数時間（秒）。</summary>
    private float _charTimer;

    /// <summary>表示中の文字列を組み立てるバッファ（毎フレームの文字列連結を避ける）。</summary>
    private readonly StringBuilder _shownBuilder = new();

    /// <summary>点滅用の経過時間（秒）。</summary>
    private float _blinkTimer;

    // ── 公開プロパティ ──────────────────────────────────────

    /// <summary>本文をすべて表示し終えているか（送り待ちの状態か）。</summary>
    public bool IsTextComplete => _shownCount >= _textElements.Count;

    /// <summary>窓を表示中か。</summary>
    public bool IsVisible => _visible;

    // ── ライフサイクル ──────────────────────────────────────

    /// <summary>
    /// 初期化。シーンで設定された色を控えておく。
    /// </summary>
    public override void OnStart()
    {
        CaptureBaseColors();
    }

    /// <summary>
    /// 毎フレーム、文字送りと送りマークの点滅を進める。
    /// </summary>
    /// <param name="ctx">フレーム情報（DeltaTime を使う）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        // 非表示中は何も進めない（隠したまま文字送りが進むのを防ぐ）
        if (!_visible) return;

        AdvanceText(ctx.DeltaTime);
        UpdateArrow(ctx.DeltaTime);
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
    /// </summary>
    /// <param name="speaker">表示する話者名（null・空文字なら空欄）。</param>
    public void SetSpeaker(string speaker)
    {
        if (speakerText is { } label && label.IsValid)
            label.Content = speaker ?? EmptyText;
    }

    /// <summary>
    /// 本文の 1 文字ずつの表示を開始する。
    /// </summary>
    /// <param name="text">表示する本文（改行コードで改行）。</param>
    public void BeginText(string text)
    {
        CaptureBaseColors();

        // 書記素クラスタ単位へ分解し直す
        _textElements.Clear();
        if (!string.IsNullOrEmpty(text))
        {
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
                _textElements.Add((string)enumerator.Current);
        }

        _shownCount = 0;
        _charTimer  = 0f;
        _blinkTimer = 0f;
        _shownBuilder.Clear();

        // 文字送りを行わない設定なら最初から全文を出す
        if (charInterval <= MinCharInterval) CompleteText();
        else ApplyBodyContent();
    }

    /// <summary>
    /// 本文を最後まで一気に表示する（送り入力による全文表示に使う）。
    /// </summary>
    public void CompleteText()
    {
        if (IsTextComplete) return;

        // 未表示ぶんをまとめて連結する
        for (int i = _shownCount; i < _textElements.Count; i++)
            _shownBuilder.Append(_textElements[i]);

        _shownCount = _textElements.Count;
        _charTimer  = 0f;
        ApplyBodyContent();
    }

    // ── 内部処理 ────────────────────────────────────────────

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

        if (balloonSprite   is { } balloon   && balloon.IsValid)
            balloon.Color = _balloonBaseColor.WithAlpha(_balloonBaseColor.a * alphaScale);
        if (nameplateSprite is { } nameplate && nameplate.IsValid)
            nameplate.Color = _nameplateBaseColor.WithAlpha(_nameplateBaseColor.a * alphaScale);
        if (speakerText     is { } speaker   && speaker.IsValid)
            speaker.Color = _speakerBaseColor.WithAlpha(_speakerBaseColor.a * alphaScale);
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
        if (IsTextComplete) return;
        if (charInterval <= MinCharInterval) { CompleteText(); return; }

        _charTimer += deltaTime;

        // 端数時間から「今フレームに出す文字数」を求める
        int advance = 0;
        while (_charTimer >= charInterval
               && advance < MaxCharsPerFrame
               && _shownCount + advance < _textElements.Count)
        {
            _charTimer -= charInterval;
            advance++;
        }
        if (advance <= 0) return;

        for (int i = 0; i < advance; i++)
            _shownBuilder.Append(_textElements[_shownCount + i]);
        _shownCount += advance;

        ApplyBodyContent();
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
            body.Content = _shownBuilder.ToString();
    }
}
