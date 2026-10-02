using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  SelectionGroup.cs — 選択のグループ（セグメント・チップ・ラジオの共通。W2-4）
//
//  直下の子の SelectItem（項目）を登録簿から集め（Index の順）、選択の状態（SelectionModel）を持ち、
//  項目のタップで選ぶ・外す。項目ごとの見た目（背景・文字・枠・ラジオの点）を SelectionLooks で決めて当てる。
//  子の項目は別のスクリプトなので、登録簿が変わったら集め直す（項目の OnStart がグループより後でもよい）。
//  選び方・見た目の種類は派生（SegmentedControl・ChipGroup・RadioGroup）が決める。
// ============================================================

/// <summary>選択のグループ（共通）。</summary>
public abstract class SelectionGroup : UiWidget
{
    /// <summary>子の文字の名前。</summary>
    private const string LabelChild = "Label";
    /// <summary>子のラジオの点の名前。</summary>
    private const string DotChild = "Dot";

    /// <summary>最初に選ぶ項目の番号（Index）。空なら選ばない（1 つ選ぶ選び方は先頭）。</summary>
    [SerializeField(Label = "最初の選択")]
    public int[] InitialSelection = Array.Empty<int>();

    /// <summary>
    /// 項目の文字の大きさ（2026-10-02。テーマのトークンの名前か数〈"18"〉。既定 text.label。空ならプレハブの大きさのまま。読み方は UiTextSize）。
    /// 以前は text.label に固定で、チップ・ラジオごとに変えられなかった（Wake or Pay の W3-1 (2)）。
    /// </summary>
    [SerializeField(Label = "文字の大きさ")]
    public string LabelSize = UiTokens.TextLabel;

    /// <summary>選択が変わった。</summary>
    public event Action<SelectionGroup>? SelectionChanged;

    /// <summary>選択の状態。</summary>
    protected readonly SelectionModel Model = new();

    /// <summary>項目（Index の順）。</summary>
    private readonly List<SelectItem> _items = new();
    /// <summary>集め直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>最初の選択を当てたか。</summary>
    private bool _initialApplied;

    /// <summary>選び方。</summary>
    protected abstract SelectionMode Mode { get; }

    /// <summary>項目の見た目の種類。</summary>
    protected abstract SelectionLookKind LookKind { get; }

    /// <summary>項目の数。</summary>
    public int Count => _items.Count;

    /// <summary>最初に選んでいる項目の並びの番号（無ければ -1）。</summary>
    public int SelectedIndex => Model.SelectedIndex;

    /// <summary>選んでいる項目の並びの番号。</summary>
    public IReadOnlyList<int> SelectedIndices => Model.SelectedIndices;

    /// <summary>並びの番号 i を選ぶ・外す（プログラムから）。</summary>
    public void Select(int i, bool selected = true)
    {
        if (Model.Set(i, selected)) OnSelectionChanged();
    }

    /// <summary>項目の文字の大きさを変える（2026-10-02。トークンの名前か数。見た目も変える）。</summary>
    /// <param name="labelSize">大きさの指定（空ならプレハブの大きさのまま）。</param>
    public void SetLabelSize(string labelSize)
    {
        labelSize ??= string.Empty;
        if (LabelSize == labelSize) return;
        LabelSize = labelSize;
        Refresh();
    }

    /// <summary>項目が押された（SelectItem から）。</summary>
    internal void OnItemTapped(SelectItem item)
    {
        int i = _items.IndexOf(item);
        if (i < 0 || !IsEnabled) return;
        SyncDisabled();
        if (Model.Tap(i)) OnSelectionChanged();
    }

    /// <summary>項目の「選べない」を選択の状態へ写す（項目の Disabled・Interactable はスクリプトから変わり得る）。</summary>
    private void SyncDisabled()
    {
        for (int i = 0; i < _items.Count; i++) Model.SetDisabled(i, _items[i].Disabled || !_items[i].Interactable);
    }

    /// <summary>項目の見た目を作り直す（SelectItem の押下から）。</summary>
    internal void RefreshItems()
    {
        ApplyLook();
        Redraw.Request();
    }

    /// <inheritdoc />
    protected override void OnWidgetStart() => Model.Mode = Mode;

    /// <inheritdoc />
    protected override void OnWidgetUpdate(float dt)
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        Collect();
    }

    /// <summary>子の項目を集め直す（選択は並びの番号で引き継ぐ）。</summary>
    private void Collect()
    {
        var found = UiRegistry.ChildrenOf<SelectItem>(gameObject).OrderBy(x => x.Index).ToList();
        if (found.SequenceEqual(_items)) return;
        _items.Clear();
        _items.AddRange(found);
        Model.Resize(_items.Count);
        if (!_initialApplied && _items.Count > 0)
        {
            _initialApplied = true;
            foreach (var index in InitialSelection)
            {
                int i = _items.FindIndex(x => x.Index == index);
                if (i >= 0) Model.Set(i, true);
            }
            if (Mode == SelectionMode.Single && Model.SelectedIndex < 0) Model.Set(0, true);
        }
        Refresh();
    }

    /// <summary>選択が変わった: 見た目を作り直して知らせる。</summary>
    private void OnSelectionChanged()
    {
        Refresh();
        SelectionChanged?.Invoke(this);
    }

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        SyncDisabled();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            bool disabled = !IsEnabled || item.Disabled || !item.Interactable;
            var look = SelectionLooks.Resolve(LookKind, Model.IsSelected(i), item.IsPressed, disabled, Theme);
            var node = item.Node;
            if (node.GetComponent<Sprite>() is { } bg)
            {
                bg.Color = look.Background;
                bg.BorderColor = look.Border;
                bg.BorderWidth = look.BorderWidth;
                if (LookKind == SelectionLookKind.Radio) bg.Shape = SpriteShapeKind.Ellipse;
                else bg.CornerRadius = look.CornerRadius;
            }
            if (node.FindChild(LabelChild).GetComponent<Text>() is { } label)
            {
                label.Color = look.Label;
                // 大きさは部品ごとの指定（既定 text.label。2026-10-02）
                UiTextStyle.Apply(label, Theme, LabelSize);
            }
            var dot = node.FindChild(DotChild);
            if (dot.IsValid)
            {
                dot.Visible = look.IndicatorVisible;
                if (dot.GetComponent<Sprite>() is { } dotSprite)
                {
                    dotSprite.Shape = SpriteShapeKind.Ellipse;
                    dotSprite.Color = look.Indicator;
                }
            }
        }
    }
}
