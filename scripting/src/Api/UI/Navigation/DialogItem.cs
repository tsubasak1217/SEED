using System;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  DialogItem.cs — 選択肢の一覧のダイアログの 1 行（2026-10-02。Material の SimpleDialogOption 相当。docs/ui_navigation.md §3.2）
//
//  【プレハブ】templates/ui/prefabs/dialog_item.actor（Dialog が札の Card/Items/Viewport の下に項目の数だけ作る）:
//      DialogItem（Sprite = 行の背景〈押下の重ね色。ふだん透明〉・CanvasGesture〈タップ・押下〉・CanvasLayoutItem〈高さ〉・このスクリプト）
//      ├─ Icon（Sprite。先頭のアイコン〈図形か画像〉。項目にアイコンが無ければ隠す）
//      └─ Label（Text。1 行・縦の真ん中）
//  行は札の幅いっぱい（窓の縦の CanvasStack の Stretch）で、中身は左右に size.dialog_padding の余白を取る（題と文字の左端がそろう）。
//  見た目（DialogItemLooks）: 文字 color.on_surface・アイコンの既定 color.on_surface_muted、危険（DialogButtonKind.Danger）は color.error、
//  選べない項目（DialogMenuItem.Enabled = false）は color.on_disabled で押せない。押している間は color.state_layer を重ねる。
//  行はタップ・押下を受けるだけで、選んだ後のこと（閉じる・結果）は Dialog が決める（Tapped）。スクロールの中なので、指を動かすと
//  押下はスクロールに負けて取り消される（W2-2。タップにならない）。
// ============================================================

/// <summary>選択肢の一覧のダイアログの 1 行。</summary>
public sealed class DialogItem : UiWidget
{
    /// <summary>子のアイコンの名前。</summary>
    private const string IconChild = "Icon";
    /// <summary>子の文字の名前。</summary>
    private const string LabelChild = "Label";
    /// <summary>Text の揃え（左）。</summary>
    private const string AlignLeft = "left";
    /// <summary>Text の縦の揃え（真ん中）。</summary>
    private const string AlignMiddle = "middle";
    /// <summary>行の幅は窓の Stack が決める（大きさの指定の幅 0 = 中身。Stretch で札の幅になる）。</summary>
    private const float StretchedWidth = 0f;

    /// <summary>タップで端末を震わせるか（Android の端末だけ）。</summary>
    [SerializeField(Label = "触感")]
    public bool Haptic = true;

    /// <summary>項目の番号（DialogOptions.Items の添字。Dialog が当てるまで DialogModel.NoSelection）。</summary>
    public int Index { get; private set; } = DialogModel.NoSelection;

    /// <summary>項目（Dialog が当てるまで null）。</summary>
    public DialogMenuItem? Item { get; private set; }

    /// <summary>押している（押下の見た目）。</summary>
    public bool IsPressed { get; private set; }

    /// <summary>押された（選べる項目のタップ）。</summary>
    public event Action<DialogItem>? Tapped;

    /// <summary>行の幅（札の幅）・高さ。</summary>
    private float _rowWidth, _rowHeight;
    /// <summary>アイコン欄を取るか（一覧のどれかにアイコンがある）。</summary>
    private bool _iconColumn;

    /// <inheritdoc />
    /// <remarks>押せて、項目が選べる（Enabled）とき。</remarks>
    public override bool IsEnabled => Interactable && (Item?.Enabled ?? true);

    /// <summary>
    /// 項目と大きさを当てる（Dialog から。見た目も作り直す）。
    /// </summary>
    /// <param name="index">項目の番号。</param>
    /// <param name="item">項目。</param>
    /// <param name="rowWidth">行の幅（札の幅）。</param>
    /// <param name="rowHeight">行の高さ（size.dialog_item_height）。</param>
    /// <param name="iconColumn">アイコン欄を取るか。</param>
    internal void Configure(int index, DialogMenuItem item, float rowWidth, float rowHeight, bool iconColumn)
    {
        Index = index;
        Item = item;
        _rowWidth = rowWidth;
        _rowHeight = rowHeight;
        _iconColumn = iconColumn;
        Refresh();
    }

    /// <inheritdoc />
    public override void OnGesturePressDown(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsPressed = true;
        Refresh();
    }

    /// <inheritdoc />
    public override void OnGesturePressCancel(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGesturePressUp(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled) return;
        if (Haptic) SEED.Platform.Haptics.Tap();
        Tapped?.Invoke(this);
    }

    /// <summary>押下を終える。</summary>
    private void EndPress()
    {
        if (!IsPressed) return;
        IsPressed = false;
        Refresh();
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (Item is not { } item) return;
        var look = DialogItemLooks.Resolve(item.Kind, IsPressed, !IsEnabled, Theme);
        // 行の背景（押下の重ね色）と高さ（幅は窓の Stack が札の幅に伸ばす）
        if (SpriteOf() is { } background)
        {
            background.Color = look.Background;
            background.Size = new Vector2(_rowWidth, _rowHeight);
        }
        if (gameObject.GetComponent<CanvasLayoutItem>() is { } layoutItem) layoutItem.PreferredSize = new Vector2(StretchedWidth, _rowHeight);

        // 先頭のアイコン（アイコン欄は size.icon の幅。どの行も同じ幅で文字の左端をそろえる）と文字の置き場
        var placement = DialogItemLooks.Place(_rowWidth, _rowHeight, Theme.Number(NavTokens.SizeDialogPadding), _iconColumn,
            Theme.Number(UiTokens.SizeIcon), Theme.Number(UiTokens.SizeIconGap));
        var icon = UiIconLooks.Resolve(item.Icon, Theme, look.IconColor, look.IconFade);
        UiIconView.Apply(gameObject.FindChild(IconChild), icon, new Vector2(placement.IconX, DialogItemLooks.CenterTop(_rowHeight, icon.Size)));
        if (TextOf(LabelChild) is { } label)
        {
            label.Content = item.Text;
            label.Color = look.Label;
            UiTextStyle.Apply(label, Theme, UiTokens.TextBody);
            label.Align = AlignLeft;
            label.VerticalAlign = AlignMiddle;
            label.Wrap = false;
            label.BoxWidth = placement.LabelWidth;
            label.BoxHeight = placement.LabelHeight;
        }
        if (TransformOf(LabelChild) is { } labelCt) labelCt.Position = new Vector2(placement.LabelX, 0f);
    }
}
