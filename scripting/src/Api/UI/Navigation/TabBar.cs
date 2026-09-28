using System;
using System.Collections.Generic;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  TabBar.cs — 下のタブのバー（W2-7。docs/ui_navigation.md §4）
//
//  【プレハブ】templates/ui/prefabs/tab_host.actor の TabBar:
//      TabBar（Canvas・Sprite〈面〉・CanvasStack〈横・項目を伸ばす〉・CanvasLayoutItem〈高さ〉・このスクリプト）
//      └─ Item0..（SEED.UI.TabItem）
//  - 項目（TabItem）を登録簿から集め（Index の順）、選択の見た目を TabLooks で当てる
//  - 項目のタップを ItemTapped で知らせる（TabHost が選択・もう一度押したら根へ、を決める。バーは選択を自分では変えない）
//  - 高さ = size.tab_bar + 下の安全領域（背景はジェスチャーバーの下まで、項目はバーの上）。安全領域が変わったら当て直す
// ============================================================

/// <summary>下のタブのバー。</summary>
public sealed class TabBar : UiWidget
{
    /// <summary>子の名前。</summary>
    private const string IndicatorChild = "Indicator";
    private const string IconChild = "Icon";
    private const string LabelChild = "Label";
    /// <summary>余白が変わったとみなす差（キャンバスの単位）。</summary>
    private const float InsetEpsilon = 0.5f;

    /// <summary>タップで端末を震わせるか（Android の端末だけ）。</summary>
    [SEEDEditor.Scripting.SerializeField(Label = "触感")]
    public bool Haptic = true;

    /// <summary>項目が押された（項目の番号 = TabItem.Index）。</summary>
    public event Action<TabBar, int>? ItemTapped;

    /// <summary>項目（Index の順）。</summary>
    private readonly List<TabItem> _items = new();
    /// <summary>集め直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>選んでいる項目の番号（TabHost が決める）。</summary>
    private int _selected;
    /// <summary>当てた下の余白（−1 = まだ）。</summary>
    private float _bottomInset = -1f;

    /// <summary>項目の数。</summary>
    public int Count => _items.Count;

    /// <summary>選んでいる項目の番号（TabItem.Index。見た目だけを変える。選択の決まりは TabHost）。</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Refresh();
        }
    }

    /// <summary>項目が押された（TabItem から）。</summary>
    internal void OnItemTapped(TabItem item)
    {
        if (!IsEnabled) return;
        if (Haptic) SEED.Platform.Haptics.Tap();
        ItemTapped?.Invoke(this, item.Index);
    }

    /// <summary>項目の見た目を作り直す（TabItem の押下から）。</summary>
    internal void RefreshItems()
    {
        ApplyLook();
        Redraw.Request();
    }

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        ApplyInset();
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        var found = UiRegistry.ChildrenOf<TabItem>(gameObject).OrderBy(x => x.Index).ToList();
        if (found.SequenceEqual(_items)) return;
        _items.Clear();
        _items.AddRange(found);
        Refresh();
    }

    /// <summary>高さと下の余白（安全領域）を当てる。</summary>
    private void ApplyInset()
    {
        float inset = SafeInsets.BottomUnits();
        if (MathF.Abs(inset - _bottomInset) < InsetEpsilon) return;
        _bottomInset = inset;
        float height = Theme.Number(NavTokens.SizeTabBar) + inset;
        if (gameObject.GetComponent<CanvasLayoutItem>() is { } item) item.PreferredSize = new Vector2(0f, height);
        // 高さはコンテナが伸ばさない軸なので、背景のスプライトの高さも合わせる（ジェスチャーバーの下まで塗る）
        if (SpriteOf() is { } bg) bg.Height = height;
        if (gameObject.GetComponent<CanvasStack>() is { } stack) stack.Padding = new CanvasPadding(0f, 0f, 0f, inset);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        if (SpriteOf() is { } bg) bg.Color = Theme.Color(UiTokens.ColorSurface);
        _bottomInset = -1f;
        foreach (var item in _items)
        {
            bool disabled = !IsEnabled || !item.Interactable;
            var look = TabLooks.Resolve(item.Index == _selected, item.IsPressed, disabled, Theme);
            var node = item.Node;
            var indicator = node.FindChild(IndicatorChild);
            if (indicator.IsValid)
            {
                indicator.Visible = look.IndicatorVisible;
                if (indicator.GetComponent<Sprite>() is { } s)
                {
                    s.Color = look.Indicator;
                    s.Size = look.IndicatorSize;
                    s.CornerRadius = look.IndicatorRadius;
                }
            }
            if (node.FindChild(IconChild).GetComponent<Sprite>() is { } icon) icon.Color = look.Content;
            if (node.FindChild(LabelChild).GetComponent<Text>() is { } label)
            {
                label.Color = look.Content;
                label.FontSize = Theme.Number(UiTokens.TextCaption, label.FontSize);
            }
        }
    }
}
