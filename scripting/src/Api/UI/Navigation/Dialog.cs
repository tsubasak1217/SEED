using System;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  Dialog.cs — ダイアログ（確認。題・本文・ボタン 1〜3・幕。W2-7。docs/ui_navigation.md §3.2）
//
//  【プレハブ】templates/ui/prefabs/dialog.actor（ModalHost が Dialogs の帯の下に作る）:
//      Dialog（Canvas・親に合わせる・CanvasStack〈縦・中央・中央〉・このスクリプト）
//      ├─ Scrim（Sprite = 幕・親に合わせる〈並べない〉・CanvasGesture〈タップ〉・GestureRelay）
//      └─ Card（Canvas・Sprite〈面・角丸〉・受けるジェスチャーの無い CanvasGesture〈遮る板〉・CanvasStack〈縦・余白〉・幅の指定）
//         ├─ Title（Text）・Message（Text〈折り返し〉）
//         └─ Buttons（CanvasStack〈横・右寄せ〉）└─ Neutral・Negative・Positive（SEED.UI.Button）
//  Card は Scrim の兄弟（Scrim の子にすると Card の上のタップが幕のタップになる。W2-2 の遮り R3 は祖先を遮らない）。
//  【開き方】ModalHost.Current.ShowDialog(new DialogOptions { … }) → DialogHandle（ResultAsync / Completed）。
//  【動き】幕の濃さ 0 → opacity.dialog_scrim、札の大きさ ratio.dialog_scale_from → 1（motion.dialog・motion.dialog_curve）。
//  出るときは逆。部分木の透明度が無いので札そのものはフェードしない（docs/backlog.md）。
// ============================================================

/// <summary>ダイアログ。</summary>
public sealed class Dialog : ModalPlane
{
    /// <summary>子の名前。</summary>
    private const string ScrimChild = "Scrim";
    private const string CardChild = "Card";
    private const string TitleChild = "Card/Title";
    private const string MessageChild = "Card/Message";
    private const string NeutralChild = "Card/Buttons/Neutral";
    private const string NegativeChild = "Card/Buttons/Negative";
    private const string PositiveChild = "Card/Buttons/Positive";
    /// <summary>ボタンの文字の左右の余白（ボタンの幅 = 文字の幅 + 余白）。</summary>
    private const float ButtonTextPaddingEm = 1.5f;
    /// <summary>ダイアログの札の縮み（出入りの動き）の中心（札の真ん中）。</summary>
    private static readonly Vector2 CardPivot = new(0.5f, 0.5f);

    /// <inheritdoc />
    public override ModalKind Kind => ModalKind.Dialog;

    /// <summary>シーンの ModalHost でダイアログを開く（ModalHost が無ければ null・警告）。</summary>
    public static DialogHandle? Show(DialogOptions options)
    {
        if (ModalHost.Current is { } host) return host.ShowDialog(options);
        Debug.LogWarning($"{LogPrefix} ModalHost がシーンにありません");
        return null;
    }

    /// <summary>決め方の中身。</summary>
    private DialogOptions _options = new();
    /// <summary>結果を 1 回だけ受ける留め金。</summary>
    private readonly DialogResultLatch _latch = new();
    /// <summary>幕・札のノード。</summary>
    private GameObject _scrim, _card;
    /// <summary>つないだ部品（幕のタップ・ボタン）の登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>つないだ幕のタップ。</summary>
    private GestureRelay? _scrimRelay;
    /// <summary>つないだボタン（結果 → ボタン）。</summary>
    private readonly System.Collections.Generic.Dictionary<DialogResult, Button> _buttons = new();
    /// <summary>出入りの動き（0 = 閉じた・1 = 開いた）。</summary>
    private UiTween _open = UiTween.At(0f);

    /// <inheritdoc />
    protected override void OnPlaneStart()
    {
        _options = Options as DialogOptions ?? new DialogOptions();
        _scrim = gameObject.FindChild(ScrimChild);
        _card = gameObject.FindChild(CardChild);
        Layout();
        ApplyOpen(0f);
        NavNode.SetVisible(Owner, true);
        BeginEnter();
        _open.Retarget(1f, Theme.Number(NavTokens.MotionDialog));
    }

    /// <summary>題・本文・ボタンを当て、文字の大きさから札の中の高さを見積もる。</summary>
    private void Layout()
    {
        float width = Theme.Number(NavTokens.SizeDialogWidth);
        float padding = Theme.Number(NavTokens.SizeDialogPadding);
        float inner = Math.Max(0f, width - 2f * padding);
        if (_card.GetComponent<CanvasLayoutItem>() is { } cardItem) cardItem.PreferredSize = new Vector2(width, 0f);
        if (_card.GetComponent<CanvasStack>() is { } stack) stack.Padding = CanvasPadding.All(padding);
        if (_card.GetComponent<CanvasTransform>() is { } ct) ct.Pivot = CardPivot;

        SetText(TitleChild, _options.Title, UiTokens.TextTitle, inner, wrap: false);
        SetText(MessageChild, _options.Message, UiTokens.TextBody, inner, wrap: true);

        var shown = DialogModel.Buttons(_options);
        float labelSize = Theme.Number(UiTokens.TextLabel);
        float height = Theme.Number(NavTokens.SizeDialogButtonHeight);
        foreach (var (result, path) in new[] { (DialogResult.Neutral, NeutralChild), (DialogResult.Negative, NegativeChild), (DialogResult.Positive, PositiveChild) })
        {
            var node = gameObject.FindChild(path);
            bool visible = shown.Contains(result);
            node.Visible = visible;
            if (!visible) continue;
            string text = DialogModel.ButtonText(_options, result);
            if (node.FindChild("Label").GetComponent<Text>() is { } label) label.Content = text;
            float w = Math.Max(DialogLayout.EstimateWidth(text, labelSize) + ButtonTextPaddingEm * 2f * labelSize, height);
            if (node.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(w, height);
            // ボタンの背景は指定の大きさで描かれる（コンテナが伸ばさない軸はスプライトの大きさのまま）ので合わせる
            if (node.GetComponent<Sprite>() is { } bg) bg.Size = new Vector2(w, height);
        }
    }

    /// <summary>文字の子へ本文と枠を当てる（空なら隠す）。高さは行の数の見積もり（Text.Measure は W2-6c）。</summary>
    private void SetText(string path, string content, string sizeToken, float boxWidth, bool wrap)
    {
        var node = gameObject.FindChild(path);
        bool visible = content.Length > 0;
        node.Visible = visible;
        if (!visible || node.GetComponent<Text>() is not { } text) return;
        float size = Theme.Number(sizeToken, text.FontSize);
        float height = wrap ? DialogLayout.EstimateHeight(content, size, boxWidth) : size * DialogLayout.LineHeightEm;
        text.Content = content;
        text.FontSize = size;
        text.BoxWidth = boxWidth;
        text.BoxHeight = height;
        text.Wrap = wrap;
        if (node.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(boxWidth, height);
    }

    /// <summary>幕のタップ・ボタンをつなぐ（部品の OnStart の順は決まっていないので、登録簿が変わるたびに引き直す）。</summary>
    private void Bind()
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        if (_scrimRelay is null && Of<GestureRelay>(_scrim) is { } relay)
        {
            _scrimRelay = relay;
            relay.Tapped += (_, _) => OnScrimTapped();
        }
        foreach (var (result, path) in new[] { (DialogResult.Neutral, NeutralChild), (DialogResult.Negative, NegativeChild), (DialogResult.Positive, PositiveChild) })
        {
            if (_buttons.ContainsKey(result) || Of<Button>(gameObject.FindChild(path)) is not { } button) continue;
            _buttons[result] = button;
            button.Clicked += _ => Choose(result);
        }
    }

    /// <summary>ボタン・幕・戻るで結果を決めて閉じる（1 回だけ）。</summary>
    private void Choose(DialogResult result)
    {
        if (Phase is ModalPhase.Exiting or ModalPhase.Closed) return;
        if (!_latch.TryComplete(result)) return;
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
    protected override void OnPlaneUpdate(float dt)
    {
        Bind();
        if (!_open.IsRunning) return;
        ApplyOpen(_open.Advance(dt));
        Redraw.KeepAlive(_open.Remaining);
        if (_open.IsRunning) return;
        if (Phase == ModalPhase.Entering) EndEnter();
        else if (Phase == ModalPhase.Exiting) FinishClose();
    }

    /// <inheritdoc />
    protected override void OnBeginExit() => _open.Retarget(0f, Theme.Number(NavTokens.MotionDialog));

    /// <summary>開き具合（0〜1）→ 幕の濃さと札の大きさ（曲線を通す）。</summary>
    private void ApplyOpen(float linear)
    {
        float p = UiCurve.FromTheme(Theme, NavTokens.MotionDialogCurve, UiCurve.Decelerate).Evaluate(linear);
        var scrim = Theme.Color(NavTokens.ColorScrim);
        NavNode.SetSpriteColor(_scrim, scrim.WithAlpha(Theme.Number(NavTokens.OpacityDialogScrim) * p));
        float from = Theme.Number(NavTokens.RatioDialogScaleFrom, 1f);
        float s = from + (1f - from) * p;
        NavNode.SetScale(_card, new Vector2(s, s));
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
        if (gameObject.FindChild(TitleChild).GetComponent<Text>() is { } title) title.Color = Theme.Color(UiTokens.ColorOnSurface);
        if (gameObject.FindChild(MessageChild).GetComponent<Text>() is { } message) message.Color = Theme.Color(UiTokens.ColorOnSurfaceMuted);
    }
}
