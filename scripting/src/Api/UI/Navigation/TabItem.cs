using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  TabItem.cs — 下のタブの項目 1 つ（W2-7）
//
//  【プレハブ】TabBar の直下の子（templates/ui/prefabs/tab_host.actor の TabBar/Item0..）:
//      Item（Sprite〈透明・当たりの板〉・CanvasGesture〈タップ〉・CanvasLayoutItem〈伸ばす〉・このスクリプト）
//      ├─ Indicator（Sprite = 選択の印〈丸い帯〉）
//      ├─ Icon（Sprite。任意）
//      └─ Label（Text）
//  押下・タップを TabBar へ渡すだけで、見た目は TabBar が TabLooks で決めて当てる（SelectItem と同じ考え方）。
// ============================================================

/// <summary>下のタブの項目。</summary>
public sealed class TabItem : UiWidget
{
    /// <summary>項目の番号（TabBar はこの順に並べ、TabHost のタブの番号と対応させる）。</summary>
    [SerializeField(Label = "番号")]
    public int Index;

    /// <summary>文字（空ならプレハブの Label のまま）。</summary>
    [SerializeField(Label = "文字")]
    public string Text = "";

    /// <summary>押している（押下の見た目）。</summary>
    public bool IsPressed { get; private set; }

    /// <summary>この項目の TabBar（親のアクター。まだ無ければ null）。</summary>
    public TabBar? Bar => Of<TabBar>(gameObject.Parent);

    /// <summary>項目のノード（見た目を当てる相手）。</summary>
    internal GameObject Node => gameObject;

    /// <inheritdoc />
    protected override void OnWidgetStart()
    {
        if (Text.Length > 0 && TextOf("Label") is { } label) label.Content = Text;
    }

    /// <inheritdoc />
    public override void OnGesturePressDown(GestureEvent e)
    {
        if (!IsEnabled) return;
        IsPressed = true;
        Bar?.RefreshItems();
    }

    /// <inheritdoc />
    public override void OnGesturePressCancel(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGesturePressUp(GestureEvent e) => EndPress();

    /// <inheritdoc />
    public override void OnGestureTap(GestureEvent e)
    {
        if (!IsEnabled) return;
        Bar?.OnItemTapped(this);
    }

    /// <summary>押下を終える。</summary>
    private void EndPress()
    {
        if (!IsPressed) return;
        IsPressed = false;
        Bar?.RefreshItems();
    }

    /// <inheritdoc />
    protected override void ApplyLook() => Bar?.RefreshItems();
}
