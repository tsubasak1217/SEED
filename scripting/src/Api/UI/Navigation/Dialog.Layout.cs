using System;
using System.Collections.Generic;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  Dialog.Layout.cs — ダイアログの札の割り付け（題・進捗・本文・選択肢の一覧・入力欄・ボタンの行。W2 の手直し P2-1・2026-10-02）
//
//  手順（Layout。開くとき・本文を変えたとき・覆う領域の高さが変わったとき）:
//    1. 区画の中身の高さ: 題・本文は見積もり（DialogLayout）、進捗の行 = max(スピナー, 本文)、選択肢 = 行の数 × size.dialog_item_height、
//       入力欄 = size.field_height、ボタンの行 = 横なら size.dialog_button_height・入らなければ縦に積んだ高さ（DialogActionsLayout）
//    2. 区画と間隔（DialogMetrics.Sections）→ 高さの上限に合わせて縮める（DialogMetrics.Fit。選択肢の一覧 → 本文の順に、1 行まで）
//    3. 区画の枠の高さ（中身 ＋ 下の間隔）と中身のノードの高さ・窓のスクロールの有無・札の余白と大きさを書く
//  選択肢の一覧のダイアログでは、行の押した重ね色を札の左右の端まで出すため、札の左右の余白を 0 にして、ほかの区画を中の幅で
//  真ん中に置く（CanvasLayoutItem.AlignSelf = Center）。一覧の区画だけ札の幅いっぱい（Stretch）。ふつうのダイアログは従来どおり
//  札の余白 size.dialog_padding の中に並べる（AlignSelf は Auto のまま）。
// ============================================================

public sealed partial class Dialog
{
    /// <summary>進捗の区画（2026-10-02）。</summary>
    private const string ProgressChild = "Card/Progress";
    /// <summary>進捗の行（スピナーと本文。区画の中でちょうどの高さ）。</summary>
    private const string ProgressRowChild = "Card/Progress/Row";
    /// <summary>進捗の行の本文。</summary>
    private const string ProgressLabelChild = "Card/Progress/Row/Label";
    /// <summary>本文の区画（2026-10-02。中に切り抜く窓）。</summary>
    private const string BodyChild = "Card/Body";
    /// <summary>本文の窓（CanvasClip・CanvasScroll。本文が札に入りきらなければスクロール）。</summary>
    private const string BodyViewportChild = "Card/Body/Viewport";
    /// <summary>本文の文字。</summary>
    private const string BodyMessageChild = "Card/Body/Viewport/Message";
    /// <summary>古いプレハブ（2026-10-02 より前）の本文の文字（札の直接の子。スクロールしない）。</summary>
    private const string LegacyMessageChild = "Card/Message";
    /// <summary>ボタンの文字の左右の余白（ボタンの幅 = 文字の幅 + 余白）。</summary>
    private const float ButtonTextPaddingEm = 1.5f;
    /// <summary>ボタンの文字の左右（余白を掛ける数）。</summary>
    private const float ButtonTextPaddingSides = 2f;
    /// <summary>札の CanvasStack の等間隔の間隔（区画ごとの間隔は上の区画の枠が持つので使わない）。</summary>
    private const float CardStackSpacing = 0f;
    /// <summary>選択肢の一覧のダイアログの札の左右の余白（行を札の端まで。ほかの区画は真ん中に置く）。</summary>
    private const float MenuHorizontalPadding = 0f;
    /// <summary>覆う領域の高さの上限が変わったとみなす差（キャンバスの単位）。</summary>
    private const float AreaEpsilon = 0.5f;
    /// <summary>本文を縮める下限の行の数（スクロールの窓は 1 行より低くしない）。</summary>
    private const int MinScrollLines = 1;

    /// <summary>区画のノード（ResolveSections で引く。古いプレハブには無いものがある）。</summary>
    private GameObject _title, _progress, _progressRow, _body, _bodyViewport, _messageText, _messageSlot, _buttonsNode;
    /// <summary>本文が新しい作り（Body → Viewport → Message。スクロールできる）か。</summary>
    private bool _scrollableMessage;
    /// <summary>プレハブのボタンの行の並べ方（横のときに戻す値）。</summary>
    private LayoutDirection _buttonsDirection = LayoutDirection.Horizontal;
    private MainAlign _buttonsMainAlign = MainAlign.End;
    private CrossAlign _buttonsCrossAlign = CrossAlign.Center;
    private float _buttonsSpacing;
    /// <summary>最後の割り付けで使った札の高さの上限（覆う領域の高さが変わったら割り付け直す）。</summary>
    private float _appliedMaxHeight = float.NaN;

    /// <summary>区画のノードを引き、プレハブのボタンの行の並べ方を覚える（OnPlaneStart の始めに 1 回）。</summary>
    private void ResolveSections()
    {
        _title = gameObject.FindChild(TitleChild);
        _progress = gameObject.FindChild(ProgressChild);
        _progressRow = gameObject.FindChild(ProgressRowChild);
        _body = gameObject.FindChild(BodyChild);
        _bodyViewport = gameObject.FindChild(BodyViewportChild);
        // 本文: 新しい作り（Body の窓の中）か、古い作り（札の直接の子の Text）
        _scrollableMessage = _body.IsValid && _bodyViewport.IsValid;
        _messageText = gameObject.FindChild(_scrollableMessage ? BodyMessageChild : LegacyMessageChild);
        _messageSlot = _scrollableMessage ? _body : _messageText;
        _items = gameObject.FindChild(ItemsChild);
        _itemsViewport = gameObject.FindChild(ItemsViewportChild);
        _buttonsNode = gameObject.FindChild(ButtonsChild);
        if (_buttonsNode.GetComponent<CanvasStack>() is { } buttons)
        {
            _buttonsDirection = buttons.Direction;
            _buttonsMainAlign = buttons.MainAlign;
            _buttonsCrossAlign = buttons.CrossAlign;
            _buttonsSpacing = buttons.Spacing;
        }
    }

    /// <summary>
    /// 題・進捗・本文・選択肢・入力欄・ボタンを当て、札の縦の割り付け（区画の枠の高さ・札の高さ。DialogMetrics）を決めて書く。
    /// </summary>
    private void Layout()
    {
        float width = Theme.Number(NavTokens.SizeDialogWidth);
        float padding = Theme.Number(NavTokens.SizeDialogPadding);
        float inner = DialogMetrics.InnerWidth(width, padding);
        bool menu = _rows.Count > 0;

        // 1. 区画の中身の高さ
        float titleHeight = SetText(_title, _options.Title, UiTokens.TextTitle, inner, wrap: false);
        float progressHeight = LayoutProgress(inner);
        // 進捗の札（Progress の区画があるとき）は本文を進捗の行に出すので、本文の区画は出さない
        float messageHeight = progressHeight > 0f
            ? SetText(_messageText, string.Empty, UiTokens.TextBody, inner, wrap: true)
            : SetText(_messageText, _message, UiTokens.TextBody, inner, wrap: true);
        float rowHeight = Theme.Number(NavTokens.SizeDialogItemHeight);
        float itemsHeight = menu ? _rows.Count * rowHeight : 0f;
        float inputHeight = _inputNode.IsValid ? Theme.Number(TextFieldTokens.SizeFieldHeight) : 0f;
        var actions = LayoutButtons(inner, Theme.Number(NavTokens.SizeDialogButtonHeight));

        // 2. 区画と間隔 → 高さの上限に合わせて縮める（選択肢の一覧 → 本文。縮めた区画はスクロール）
        var sections = DialogMetrics.Sections(
            new DialogContentHeights(titleHeight, progressHeight, messageHeight, itemsHeight, inputHeight, actions.RowHeight),
            new DialogSpacing(Theme.Number(NavTokens.SizeDialogTitleGap), Theme.Number(NavTokens.SizeDialogActionsGap),
                Theme.Number(NavTokens.SizeDialogItemsInset)));
        float maxHeight = CurrentMaxCardHeight();
        _appliedMaxHeight = maxHeight;
        var shrink = new List<DialogShrink>();
        if (menu) shrink.Add(new DialogShrink(DialogMetrics.ItemsSection, rowHeight));
        if (_scrollableMessage)
            shrink.Add(new DialogShrink(DialogMetrics.MessageSection, MinScrollLines * Theme.Number(UiTokens.TextBody) * DialogLayout.LineHeightEm));
        var fit = DialogMetrics.Fit(padding, sections, maxHeight, shrink);
        var layout = DialogMetrics.Arrange(padding, fit.Sections);

        // 3. 区画の枠（中身 ＋ 下の間隔）と中身の高さ・窓のスクロール
        SetSlot(_title, inner, layout.SlotHeights[DialogMetrics.TitleSection]);
        ApplySection(_progress, _progressRow, inner, layout.SlotHeights[DialogMetrics.ProgressSection],
            fit.Sections[DialogMetrics.ProgressSection].Height, scrolls: false);
        if (_scrollableMessage)
            ApplySection(_body, _bodyViewport, inner, layout.SlotHeights[DialogMetrics.MessageSection],
                fit.Sections[DialogMetrics.MessageSection].Height, fit.Scrolls[DialogMetrics.MessageSection]);
        else
            SetSlot(_messageSlot, inner, layout.SlotHeights[DialogMetrics.MessageSection]);
        ApplySection(_items, _itemsViewport, width, layout.SlotHeights[DialogMetrics.ItemsSection],
            fit.Sections[DialogMetrics.ItemsSection].Height, fit.Scrolls[DialogMetrics.ItemsSection]);
        SetSlot(gameObject.FindChild(InputChild), inner, layout.SlotHeights[DialogMetrics.InputSection]);
        SetSlot(_buttonsNode, inner, layout.SlotHeights[DialogMetrics.ButtonsSection]);
        ApplyInputSize(inner, inputHeight);
        ConfigureRows(width, rowHeight);
        ApplyAlignment(menu);

        // 札: 並べ方（余白・等間隔の間隔 0）と大きさ。背景のスプライトも同じ大きさにする
        // （ボタンの背景と同じ。コンテナが伸ばさない軸はスプライトの大きさのまま描かれる）
        if (_card.GetComponent<CanvasStack>() is { } stack)
        {
            float side = menu ? MenuHorizontalPadding : padding;
            stack.Padding = new CanvasPadding(side, layout.PaddingTop, side, layout.PaddingBottom);
            stack.Spacing = CardStackSpacing;
        }
        var cardSize = new Vector2(width, layout.CardHeight);
        if (_card.GetComponent<CanvasLayoutItem>() is { } cardItem) cardItem.PreferredSize = cardSize;
        if (_card.GetComponent<Sprite>() is { } cardBackground) cardBackground.Size = cardSize;
        Redraw.Request();
    }

    /// <summary>
    /// 文字の子へ中身・大きさ・行間・枠を当てる（空なら隠す）。戻り値は中身の高さ（行の数の見積もり × 行の高さ。隠したら 0）。
    /// </summary>
    /// <param name="node">文字のノード。</param>
    /// <param name="content">文字。</param>
    /// <param name="sizeToken">大きさのトークン。</param>
    /// <param name="boxWidth">枠の幅（札の中の幅）。</param>
    /// <param name="wrap">枠の幅で折り返すか（題は折り返さない）。</param>
    private float SetText(GameObject node, string content, string sizeToken, float boxWidth, bool wrap)
    {
        var text = node.GetComponent<Text>();
        bool visible = content.Length > 0 && text is not null;
        NavNode.SetVisible(node, visible);
        if (!visible || text is not { } shown) return 0f;
        float size = Theme.Number(sizeToken, shown.FontSize);
        float height = DialogLayout.EstimateHeight(content, size, wrap ? boxWidth : DialogLayout.NoWrapWidth);
        shown.Content = content;
        shown.FontSize = size;
        // 描く行送り（大きさ × 行間）を見積もりの行の高さにそろえる（行送りの余白は行の上下へ半分ずつ）
        shown.LineSpacing = DialogLayout.LineHeightEm;
        shown.BoxWidth = boxWidth;
        shown.BoxHeight = height;
        shown.Wrap = wrap;
        return height;
    }

    /// <summary>区画の枠（CanvasLayoutItem の大きさ = 幅 × 〈中身 ＋ 下の間隔〉）を当てる（出さない区画〈0〉は書かない）。</summary>
    private static void SetSlot(GameObject node, float width, float height)
    {
        if (!(height > 0f)) return;
        if (node.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(width, height);
    }

    /// <summary>
    /// 2 段の区画（区画の枠 → 中身のノード）を当てる: 出さないなら区画を隠す。出すなら枠の高さ（中身 ＋ 下の間隔）と中身の高さを書き、
    /// 中身が窓（CanvasScroll・CanvasClip）なら縮めたときだけスクロールと切り抜きを有効にする（縮めていない窓は指を取らない・位置 0・
    /// 切り抜かない＝行末の句読点のぶら下げ〈札の中の幅から最大 2 文字〉が従来どおり余白へ出る）。
    /// </summary>
    /// <param name="section">区画のノード（縦の Stack で中身を上へ詰める）。</param>
    /// <param name="content">中身のノード（ちょうどの高さ）。</param>
    /// <param name="width">幅。</param>
    /// <param name="slotHeight">枠の高さ（中身 ＋ 下の間隔。0 = 出さない）。</param>
    /// <param name="contentHeight">中身の高さ（縮めた窓の高さ）。</param>
    /// <param name="scrolls">窓をスクロールにするか。</param>
    private static void ApplySection(GameObject section, GameObject content, float width, float slotHeight, float contentHeight, bool scrolls)
    {
        bool shown = slotHeight > 0f && section.IsValid;
        NavNode.SetVisible(section, shown);
        if (!shown) return;
        SetSlot(section, width, slotHeight);
        SetSlot(content, width, contentHeight);
        if (content.GetComponent<CanvasScroll>() is { } scroll && scroll.Enabled != scrolls) scroll.Enabled = scrolls;
        if (content.GetComponent<CanvasClip>() is { } clip && clip.Enabled != scrolls) clip.Enabled = scrolls;
    }

    /// <summary>
    /// 進捗の行（スピナーと本文。2026-10-02）を当て、行の高さ（スピナーと本文の高い方）を返す。進捗の札でない・区画の無い古いプレハブは 0（出さない）。
    /// </summary>
    /// <param name="inner">札の中の幅。</param>
    private float LayoutProgress(float inner)
    {
        if (!_options.Progress || !_progress.IsValid || !_progressRow.IsValid)
        {
            NavNode.SetVisible(_progress, false);
            return 0f;
        }
        // スピナーの大きさ（テーマの size.spinner。プレハブの Spinner は Size = 0）と本文の間（size.icon_gap）
        float spinner = Theme.Number(UiTokens.SizeSpinner);
        float gap = Theme.Number(UiTokens.SizeIconGap);
        float labelWidth = Math.Max(0f, inner - spinner - gap);
        float labelHeight = SetText(gameObject.FindChild(ProgressLabelChild), _message, UiTokens.TextBody, labelWidth, wrap: true);
        if (_progressRow.GetComponent<CanvasStack>() is { } row && row.Spacing != gap) row.Spacing = gap;
        return Math.Max(spinner, labelHeight);
    }

    /// <summary>
    /// 出すボタンへ文字と大きさ（幅 = 文字の幅の見積もり ＋ 左右の余白。高さより狭くしない）を当て、出さないボタンを隠し、横に並べるか
    /// 縦に積むかを決めて行の並べ方を当てる（DialogActionsLayout。2026-10-02）。ボタンが無ければ行を隠す。
    /// </summary>
    /// <param name="inner">札の中の幅。</param>
    /// <param name="height">ボタンの高さ。</param>
    private DialogActionsArrangement LayoutButtons(float inner, float height)
    {
        var shown = DialogModel.Buttons(_options);
        float labelSize = Theme.Number(UiTokens.TextLabel);
        var widths = new List<float>();
        var nodes = new List<GameObject>();
        foreach (var (result, path) in ButtonPaths)
        {
            var node = gameObject.FindChild(path);
            bool visible = shown.Contains(result);
            NavNode.SetVisible(node, visible);
            if (!visible) continue;
            string text = DialogModel.ButtonText(_options, result);
            if (node.FindChild(ButtonLabelChild).GetComponent<Text>() is { } label) label.Content = text;
            widths.Add(Math.Max(DialogLayout.EstimateWidth(text, labelSize) + ButtonTextPaddingEm * ButtonTextPaddingSides * labelSize, height));
            nodes.Add(node);
            // 危険のボタンの色の役割（ボタンのスクリプトが始まっていなければ Bind が当てる）
            if (_buttons.TryGetValue(result, out var button)) button.SetTone(DialogModel.ToneOf(DialogModel.ButtonKind(_options, result)));
        }
        var arrangement = DialogActionsLayout.Arrange(widths, _buttonsSpacing, inner,
            height, Theme.Number(NavTokens.SizeDialogActionsOverflowGap));
        for (int i = 0; i < nodes.Count; i++)
        {
            var size = new Vector2(arrangement.Widths[i], height);
            if (nodes[i].GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = size;
            // ボタンの背景は指定の大きさで描かれる（コンテナが伸ばさない軸はスプライトの大きさのまま）ので合わせる
            if (nodes[i].GetComponent<Sprite>() is { } bg) bg.Size = size;
        }
        NavNode.SetVisible(_buttonsNode, nodes.Count > 0);
        if (_buttonsNode.GetComponent<CanvasStack>() is { } stack)
        {
            // 縦に積む: 上から左と同じ順・右寄せ・間は size.dialog_actions_overflow_gap（Flutter の OverflowBar）。横はプレハブの並べ方へ戻す
            var direction = arrangement.Stacked ? LayoutDirection.Vertical : _buttonsDirection;
            var main = arrangement.Stacked ? MainAlign.Start : _buttonsMainAlign;
            var cross = arrangement.Stacked ? CrossAlign.End : _buttonsCrossAlign;
            float spacing = arrangement.Stacked ? Theme.Number(NavTokens.SizeDialogActionsOverflowGap) : _buttonsSpacing;
            if (stack.Direction != direction) stack.Direction = direction;
            if (stack.MainAlign != main) stack.MainAlign = main;
            if (stack.CrossAlign != cross) stack.CrossAlign = cross;
            if (stack.Spacing != spacing) stack.Spacing = spacing;
        }
        return arrangement;
    }

    /// <summary>
    /// 区画の横の置き方（2026-10-02）: 選択肢の一覧のダイアログでは札の左右の余白を 0 にするので、ほかの区画は中の幅で真ん中
    /// （Center）、一覧の区画は札の幅いっぱい（Stretch）。ふつうのダイアログは Auto（札の CanvasStack の Stretch。従来どおり）。
    /// </summary>
    /// <param name="menu">選択肢の一覧のダイアログか。</param>
    private void ApplyAlignment(bool menu)
    {
        var inset = menu ? ItemAlign.Center : ItemAlign.Auto;
        foreach (var node in new[] { _title, _progress, _messageSlot, gameObject.FindChild(InputChild), _buttonsNode })
            SetAlignSelf(node, inset);
        SetAlignSelf(_items, menu ? ItemAlign.Stretch : ItemAlign.Auto);
    }

    /// <summary>区画の交差軸の揃えの上書きを当てる（同じなら書かない）。</summary>
    private static void SetAlignSelf(GameObject node, ItemAlign align)
    {
        if (node.GetComponent<CanvasLayoutItem>() is { } item && item.AlignSelf != align) item.AlignSelf = align;
    }

    /// <summary>
    /// 札の高さの上限（覆う領域〈ダイアログの帯〉の高さ − 上下の安全領域 − size.dialog_margin × 2。領域の高さが分からなければ上限なし）。
    /// </summary>
    private float CurrentMaxCardHeight()
    {
        var area = Owner.Parent;
        float height = area.GetComponent<CanvasTransform>() is { HasLayout: true } t ? t.LayoutSize.y : float.NaN;
        return DialogMetrics.MaxCardHeight(height, SafeInsets.TopUnits(), SafeInsets.BottomUnits(), Theme.Number(NavTokens.SizeDialogMargin));
    }

    /// <summary>覆う領域の高さ・安全領域が変わって札の高さの上限が変わったら、割り付け直す（回転・窓の大きさの変化）。</summary>
    private void WatchArea()
    {
        float max = CurrentMaxCardHeight();
        bool same = float.IsPositiveInfinity(max) && float.IsPositiveInfinity(_appliedMaxHeight)
                    || MathF.Abs(max - _appliedMaxHeight) <= AreaEpsilon;
        if (!same) Layout();
    }
}
