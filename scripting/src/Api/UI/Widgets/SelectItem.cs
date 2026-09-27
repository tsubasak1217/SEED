using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  SelectItem.cs — 選択の項目（セグメント・チップ・ラジオの 1 つ。W2-4）
//
//  【プレハブ】選択のグループ（SegmentedControl・ChipGroup・RadioGroup）の直下の子:
//      Item（Sprite = 項目の背景〈ラジオでは輪〉・CanvasGesture〈タップ・押下の見た目・最小 48 dp〉・このスクリプト）
//      ├─ Label（Text）
//      └─ Dot（Sprite = ラジオの点〈楕円〉。ラジオだけ）
//  項目はタップ・押下をグループへ渡すだけ。見た目はグループが決めて当てる（SelectionLooks）。
//  並びは Index（小さい順）。Disabled で選べない項目（灰色）になる。
// ============================================================

/// <summary>選択の項目。</summary>
public sealed class SelectItem : UiWidget
{
    /// <summary>グループの中の番号（小さい順に並ぶ）。</summary>
    [SerializeField(Label = "番号")]
    public int Index;

    /// <summary>選べない項目か。</summary>
    [SerializeField(Label = "選べない")]
    public bool Disabled;

    /// <summary>押している。</summary>
    public bool IsPressed { get; private set; }

    /// <summary>この項目のグループ（親のアクターの SelectionGroup。まだ無ければ null）。</summary>
    public SelectionGroup? Group => Of<SelectionGroup>(gameObject.Parent);

    /// <inheritdoc />
    public override bool IsEnabled => Interactable && !Disabled && (Group?.IsEnabled ?? true);

    /// <summary>項目の GameObject（グループが見た目を当てる）。</summary>
    internal GameObject Node => gameObject;

    /// <inheritdoc />
    public override void OnGesturePressDown(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsPressed = true;
        Group?.RefreshItems();
    }

    /// <inheritdoc />
    public override void OnGesturePressCancel(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGesturePressUp(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled) return;
        Group?.OnItemTapped(this);
    }

    /// <summary>押下を終える。</summary>
    private void EndPress()
    {
        if (!IsPressed) return;
        IsPressed = false;
        Group?.RefreshItems();
    }

    /// <inheritdoc />
    protected override void ApplyLook() => Group?.RefreshItems();
}
