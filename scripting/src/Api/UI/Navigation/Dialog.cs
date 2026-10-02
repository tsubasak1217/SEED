using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  Dialog.cs — ダイアログ（確認・選択肢の一覧・進捗の札。題・本文・ボタン 0〜3・幕。W2-7。docs/ui_navigation.md §3.2）
//
//  【プレハブ】templates/ui/prefabs/dialog.actor（ModalHost が Dialogs の帯の下に作る。2026-10-02 の形）:
//      Dialog（Canvas・親に合わせる・CanvasStack〈縦・中央・中央〉・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる〈並べない〉・CanvasGesture〈タップ〉・GestureRelay）
//      └─ Card（Canvas・Sprite〈面・角丸〉・受けるジェスチャーの無い CanvasGesture〈遮る板〉・CanvasStack〈縦・余白・間隔 0〉・大きさの指定）
//         ├─ Title（Text）
//         ├─ Progress（Canvas・縦の Stack）└─ Row（Canvas・横の Stack〈縦の真ん中〉）├─ Spinner（ProgressSpinner）└─ Label（Text）
//         ├─ Body（Canvas・縦の Stack）└─ Viewport（Canvas・CanvasClip・CanvasScroll〈縦〉・縦の Stack）└─ Message（Text〈折り返し〉）
//         ├─ Items（Canvas・縦の Stack）└─ Viewport（Canvas・CanvasClip・CanvasScroll〈縦〉・縦の Stack〈Stretch〉）└─ 行（dialog_item.actor）
//         ├─ Input（入力欄の枠）
//         └─ Buttons（CanvasStack〈横・右寄せ。入らなければ縦〉）└─ Neutral・Negative・Positive（SEED.UI.Button）
//  Card は Scrim の兄弟（Scrim の子にすると Card の上のタップが幕のタップになる。W2-2 の遮り R3 は祖先を遮らない）。
//  区画（Progress・Body・Items）は「区画の枠（中身 ＋ 下の間隔。縦の Stack で上へ詰める）→ 中身のノード（ちょうどの高さ）」の 2 段。
//  切り抜く窓（Viewport）に下の間隔を入れない（スクロールした本文が間隔の所に見えない）ため。
//  【古いプレハブ】2026-10-02 より前の dialog.actor（Card/Message が直接の Text。Progress・Body・Items が無い）でも動く:
//  本文はスクロールしない・進捗の札のスピナーは出ない（本文だけ）・選択肢の一覧は出せない（警告）。新しい機能はプレハブを取り込み直して使う。
//  【開き方】ModalHost.Current.ShowDialog(new DialogOptions { … }) → DialogHandle（ResultAsync / Completed）。
//    Dialog.Show(options)・Dialog.ShowMenu(題, 項目…)・Dialog.ShowProgress(本文) は、シーンの ModalHost で開く近道。
//  【大きさ】（Layout。Dialog.Layout.cs。W2 の手直し P2-1・2026-10-02）札の幅は size.dialog_width。題・本文の行の数は見積もり（DialogLayout。
//    Text.Measure は W2-6c）で、題・本文の Text の行間を DialogLayout.LineHeightEm にして、描く行送りと見積もりの行の高さを一致させる。
//    札の高さ = 余白 ＋ 区画の枠の和 ＋ 余白（DialogMetrics。出さない区画とその間隔は数えない）を、札の CanvasLayoutItem と背景の Sprite の
//    両方へ書く。札が画面より高くなるなら、選択肢の一覧・本文の窓を縮めてスクロールにする（DialogMetrics.Fit）。ボタンが札の中の幅に
//    入らなければ縦に積む（DialogActionsLayout）。
//  【動き】幕の濃さ 0 → opacity.dialog_scrim、札の大きさ ratio.dialog_scale_from → 1（motion.dialog・motion.dialog_curve）。出るときは逆。
//    札の大きさは実行中の見た目の倍率（CanvasLayoutItem.VisualScale。札の矩形の中心の周りに、背景・題・本文・ボタンが一体で縮む）へ
//    「開き具合の倍率 × 予測型の戻るのプレビューの倍率」を書く（W2 の手直し P2-1）。部分木の透明度が無いので札そのものはフェードしない。
//    選択肢の一覧は行（プレハブ）ができあがって行のスクリプトが始まるまで見せずに待ち（上限 ContentSettleGate.MaxWaitFrames）、その後に入る。
// ============================================================

/// <summary>ダイアログ。</summary>
/// <remarks>
/// 【1 行の入力（W2-6b）】DialogOptions.Input があれば、札の Input の枠（dialog.actor の Card/Input。既定は隠す）へ入力欄
/// （templates/ui/prefabs/text_field.actor）を作って本文とボタンの行の間に並べ、開いたらフォーカスを当てる（キーボードが出る）。
/// Positive を選ぶと入力欄の文字（TrimResult なら前後の空白を落とす）を DialogHandle.InputText へ置いてから閉じる。
/// キーボードの完了（SubmitOnDone）でも Positive。キーボードが札に重なるときは札を持ち上げる（IKeyboardInsetTarget。
/// 札の上端は安全領域の上端 ＋ size.keyboard_gap より上へは行かない）。入力欄の幅は札の中の幅に合わせる（2026-10-02。Dialog.Input.cs）。
/// </remarks>
public sealed partial class Dialog : ModalPlane, IKeyboardInsetTarget
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string CardChild = "Card";
    private const string TitleChild = "Card/Title";
    private const string ButtonsChild = "Card/Buttons";
    private const string NeutralChild = "Card/Buttons/Neutral";
    private const string NegativeChild = "Card/Buttons/Negative";
    private const string PositiveChild = "Card/Buttons/Positive";
    /// <summary>ボタンの文字の子の名前。</summary>
    private const string ButtonLabelChild = "Label";
    /// <summary>倍率なし（開いた・プレビューなし）。</summary>
    private const float NoScale = 1f;

    /// <summary>ボタンの結果と子の道（左から中立・いいえ・はい）。</summary>
    private static readonly (DialogResult Result, string Path)[] ButtonPaths =
    {
        (DialogResult.Neutral, NeutralChild), (DialogResult.Negative, NegativeChild), (DialogResult.Positive, PositiveChild),
    };

    /// <inheritdoc />
    public override ModalKind Kind => ModalKind.Dialog;

    /// <summary>シーンの ModalHost でダイアログを開く（ModalHost が無ければ null・警告）。</summary>
    public static DialogHandle? Show(DialogOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowDialog(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>
    /// 選択肢の一覧のダイアログを開く（2026-10-02。Material の SimpleDialog 相当。長押しのメニュー）。押した項目で閉じ、結果は
    /// <see cref="DialogResult.Selected"/> と <see cref="DialogHandle.SelectedIndex"/>。幕のタップ・戻るは Dismissed。
    /// </summary>
    /// <param name="title">題（空なら出さない）。</param>
    /// <param name="items">項目（上から）。</param>
    public static DialogHandle? ShowMenu(string title, params DialogMenuItem[] items) => Show(DialogOptions.Menu(title, items));

    /// <summary>
    /// 進捗の札を開く（2026-10-02。スピナーと本文・ボタンなし・幕のタップと戻るでは閉じない）。終わったら手札の Close / Dismiss で閉じる。
    /// 本文は <see cref="DialogHandle.SetMessage"/> で変えられる。
    /// </summary>
    /// <param name="message">本文（スピナーの右）。</param>
    /// <param name="title">題（空なら出さない）。</param>
    public static DialogHandle? ShowProgress(string message, string title = "") => Show(DialogOptions.ProgressCard(message, title));

    /// <summary>決め方の中身。</summary>
    private DialogOptions _options = new();
    /// <summary>今の本文（開いた後に DialogHandle.SetMessage で変わる）。</summary>
    private string _message = string.Empty;
    /// <summary>結果を 1 回だけ受ける留め金。</summary>
    private readonly DialogResultLatch _latch = new();
    /// <summary>幕・札のノード。</summary>
    private GameObject _scrim, _card;
    /// <summary>つないだ部品（幕のタップ・ボタン・入力欄・選択肢の行）の登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>つないだ幕のタップ。</summary>
    private GestureRelay? _scrimRelay;
    /// <summary>つないだボタン（結果 → ボタン）。</summary>
    private readonly Dictionary<DialogResult, Button> _buttons = new();
    /// <summary>出入りの動き（0 = 閉じた・1 = 開いた）。</summary>
    private UiTween _open = UiTween.At(0f);
    /// <summary>出入りの動きの 1 フレームの進め（入る動きを始めたフレームは数えない・上限。遷移の時計の直し）。</summary>
    private MotionStep _step;
    /// <summary>開き具合から決めた札の倍率（出入りの動き。1 = 開いた）。</summary>
    private float _openScale = NoScale;
    /// <summary>予測型の戻るのプレビューの倍率（W2 の手直し 3b。1 = プレビューなし）。確定した後も出終わるまで保つ。</summary>
    private float _previewScale = NoScale;
    /// <summary>準備（選択肢の行を待つ）で数えたフレーム（ContentSettleGate.MaxWaitFrames で待つのをやめる）。</summary>
    private int _prepareFrames;

    /// <summary>キーボードを避ける入れ物の根（ダイアログの根）。</summary>
    public GameObject InsetOwner => Owner;

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as DialogOptions ?? new DialogOptions();
        // 本文: 開く前に手札の SetMessage で変えていればそれ（進捗の札を開いてすぐ進みを書いたとき）、無ければ DialogOptions.Message
        _message = (Handle as DialogHandle)?.TakePendingMessage() ?? _options.Message ?? string.Empty;
        _scrim = gameObject.FindChild(ScrimChild);
        _card = gameObject.FindChild(CardChild);
        ResolveSections();
        CreateInput();
        CreateItems();
        KeyboardInsets.Register(this);
        ApplyOpen(0f);
        // 選択肢の行（プレハブ）を作ったときは、行ができあがってスクリプトが始まるまで見せずに待つ（OnPlaneUpdate の PrepareRows）
        if (_rows.Count > 0) return;
        StartEnter();
        // 入る動きを始めたフレーム（OnStart と同じフレームの Update で最初に進める）の経過は数えない。そこには前のフレームの
        // ダイアログの組み立ての時間が入っている（docs/ui_navigation.md §2「出入りの時計」）
        _step.SkipNext();
    }

    /// <summary>札を割り付け、見せて、入る動きを始める。</summary>
    private void StartEnter()
    {
        Layout();
        ApplyOpen(0f);
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        _open.Retarget(1f, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>
    /// 本文を変える（2026-10-02。進捗の札の「ダウンロード中 40%」など。開いた後は札を割り付け直す）。DialogHandle.SetMessage から。
    /// </summary>
    /// <param name="message">本文。</param>
    internal void SetMessage(string message)
    {
        message ??= string.Empty;
        if (_message == message) return;
        _message = message;
        if (Phase is ModalPhase.Entering or ModalPhase.Open or ModalPhase.Exiting) Layout();
        Redraw.Request();
    }

    /// <summary>幕のタップ・ボタン・入力欄・選択肢の行をつなぐ（部品の OnStart の順は決まっていないので、登録簿が変わるたびに引き直す）。</summary>
    private void Bind()
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindInput();
        BindItems();
        if (_scrimRelay is null && Of<GestureRelay>(_scrim) is { } relay)
        {
            _scrimRelay = relay;
            relay.Tapped += (_, _) => OnScrimTapped();
        }
        foreach (var (result, path) in ButtonPaths)
        {
            if (_buttons.ContainsKey(result) || Of<Button>(gameObject.FindChild(path)) is not { } button) continue;
            _buttons[result] = button;
            // 危険のボタン（2026-10-02）: 種類を色の役割へ（Positive は塗り、ほかは文字のボタンのまま色だけ color.error）
            button.SetTone(DialogModel.ToneOf(DialogModel.ButtonKind(_options, result)));
            button.Clicked += _ => Choose(result);
        }
    }

    /// <summary>ボタン・幕・戻る・選択肢で結果を決めて閉じる（1 回だけ）。Positive なら入力欄の文字を手札へ置いてから閉じる（W2-6b）。</summary>
    /// <param name="result">結果。</param>
    /// <param name="selectedIndex">選んだ項目の番号（Selected のときだけ）。</param>
    private void Choose(DialogResult result, int selectedIndex = DialogModel.NoSelection)
    {
        if (Phase is ModalPhase.Exiting or ModalPhase.Closed) return;
        if (!_latch.TryComplete(result, selectedIndex)) return;
        if (Handle is DialogHandle handle && result == DialogResult.Selected) handle.SetSelectedIndex(_latch.SelectedIndex);
        if (_input is { } field)
        {
            // フォーカスを外して変換中の文字を確定扱いにし、キーボードを隠してから文字を読む
            field.Unfocus();
            if (result == DialogResult.Positive && _options.Input is { } spec && Handle is DialogHandle inputHandle)
                inputHandle.SetInputText(spec.Finish(field.Text));
        }
        ClearKeyboardLift();
        KeyboardInsets.Unregister(this);
        RequestClose(result);
    }

    /// <summary>幕のタップ。</summary>
    private void OnScrimTapped()
    {
        if (DialogModel.OnScrimTap(_options) is { } result) Choose(result);
    }

    /// <inheritdoc />
    internal override bool HandleBack()
    {
        var (consumed, result) = DialogModel.OnBack(_options);
        if (result is { } r) Choose(r);
        return consumed;
    }

    /// <inheritdoc />
    /// <remarks>予測型の戻るのプレビュー（3b）で縮めるのは札（真ん中の周り。幕はそのまま）。</remarks>
    protected override GameObject BackPreviewNode => _card;

    /// <inheritdoc />
    /// <remarks>戻るで閉じない（CancelableByBack = false）ダイアログは縮めない（戻るは受けるが何も起きない）。</remarks>
    protected override bool ClosesOnBack => _options.CancelableByBack;

    /// <inheritdoc />
    /// <remarks>
    /// 札の倍率は出入りの動きと同じ欄（VisualScale）なので、プレビューの倍率を覚えて開き具合の倍率との積を書く。
    /// 確定した戻るの後はプレビューの倍率を保ったまま出る動きが進む（縮めた姿勢のまま出る。ClearBackPreview は出終わってから来る）。
    /// </remarks>
    protected override void ApplyBackPreviewPose(BackPreviewPose pose)
    {
        _previewScale = pose.Scale;
        ApplyCardScale();
    }

    /// <inheritdoc />
    protected override void OnPlaneUpdate(float dt)
    {
        Bind();
        if (Phase == ModalPhase.Preparing)
        {
            PrepareRows();
            return;
        }
        // 覆う領域の高さ・安全領域が変わったら（回転・窓の大きさ）札を割り付け直す（高さの上限が変わる）
        if (Phase is ModalPhase.Entering or ModalPhase.Open) WatchArea();
        if (!_open.IsRunning) return;
        // 1 フレームで進める時間は上限まで（重いフレームで出入りが飛ばない。入る動きの始めのフレームは 0）
        ApplyOpen(_open.Advance(_step.Next(dt)));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else if (Phase == ModalPhase.Exiting) FinishClose();
    }

    /// <summary>
    /// 準備: 選択肢の行ができあがって行のスクリプトが始まったら（待つ上限 ContentSettleGate.MaxWaitFrames の後も）見せて入る。
    /// 入る動きは次のフレームから進める（TopSheet と同じ。このフレームの経過は数えない）。
    /// </summary>
    private void PrepareRows()
    {
        Redraw.Request();
        if (!RowsReady() && ++_prepareFrames <= ContentSettleGate.MaxWaitFrames) return;
        if (!RowsReady()) Debug.LogWarning($"{LogPrefix} 選択肢の行のスクリプトが始まりません（{ItemPrefabPath}）。そのまま開きます");
        BindItems();
        StartEnter();
    }

    /// <inheritdoc />
    /// <remarks>入力欄のフォーカスとキーボードの持ち上げもここで手放す（ボタンを通らずに閉じたとき〈Dismiss・シーンの切り替え〉も）。</remarks>
    protected override void OnBeginExit()
    {
        _input?.Unfocus();
        ClearKeyboardLift();
        KeyboardInsets.Unregister(this);
        _open.Retarget(0f, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>開き具合（0〜1）→ 幕の濃さと札の大きさ（曲線を通す）。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionDialogCurve, UiCurve.Decelerate).Evaluate(linear);
        var scrim = Theme.Color(NavTokens.ColorScrim);
        NavNode.SetSpriteColor(_scrim, scrim.WithAlpha(Theme.Number(NavTokens.OpacityDialogScrim) * p));
        _openScale = DialogMetrics.OpenScale(p, Theme.Number(NavTokens.RatioDialogScaleFrom, NoScale));
        ApplyCardScale();
    }

    /// <summary>札へ見た目の倍率（開き具合 × プレビュー。札の矩形の中心の周りに、背景と中身が一体で縮む）を書く。</summary>
    private void ApplyCardScale()
    {
        float s = DialogMetrics.CardScale(_openScale, _previewScale);
        NavNode.SetVisualScale(_card, new Vector2(s, s));
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        // 札の面の色・角丸（テーマの差し替えに追従）
        if (_card.GetComponent<Sprite>() is { } card)
        {
            card.Color = Theme.Color(UiTokens.ColorSurface);
            card.CornerRadius = Theme.Number(NavTokens.RadiusDialog);
        }
        if (gameObject.FindChild(TitleChild).GetComponent<Text>() is { } title)
        {
            title.Color = Theme.Color(UiTokens.ColorOnSurface);
            // 書体と見出しの太さ（W2-9。大きさは Layout が文字の量と一緒に決める）
            UiTextStyle.ApplyFont(title, Theme, UiTokens.FontWeightTitle);
        }
        if (_messageText.GetComponent<Text>() is { } message)
        {
            message.Color = Theme.Color(UiTokens.ColorOnSurfaceMuted);
            UiTextStyle.ApplyFont(message, Theme);
        }
        // 進捗の札の文字（スピナーの右。面の上の文字の色）
        if (gameObject.FindChild(ProgressLabelChild).GetComponent<Text>() is { } progress)
        {
            progress.Color = Theme.Color(UiTokens.ColorOnSurface);
            UiTextStyle.ApplyFont(progress, Theme);
        }
    }
}
