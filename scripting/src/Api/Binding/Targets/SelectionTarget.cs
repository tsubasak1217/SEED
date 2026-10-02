using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  SelectionTarget.cs — 選択のグループ（SegmentedControl・RadioGroup・ChipGroup）を双方向の当てる先にする（Bind.Selection）
//
//  値は「選んでいる項目の並びの番号」（SelectionGroup.SelectedIndex と同じ。−1 = 何も選んでいない）。
//  値 → 部品: Select(番号)（1 つ選ぶグループでは他が外れる）。−1 は選んでいる項目を全部外す。範囲の外は書かず、警告を 1 回出す。
//  部品 → 値: SelectionGroup.SelectionChanged（タップ。Select も鳴るが TwoWayBinding の留め金で止まる）。
//  用意: グループは子の項目を最初の Update で集めるので、項目が 1 つ以上集まるまで待つ（集める前の Select は効かないため）。
//  複数を選ぶ ChipGroup（Multiple = true）には向かない（番号 1 つでは表せない。最初の選択の番号だけを結ぶ）。
// ============================================================

/// <summary>選択のグループを双方向の当てる先にする。</summary>
internal sealed class SelectionTarget : WidgetTarget<SelectionGroup, int>
{
    /// <summary>何も選んでいないことを表す番号（SelectionGroup.SelectedIndex と同じ）。</summary>
    private const int NoSelection = -1;

    /// <summary>範囲の外の警告を出したか（1 つの結び付けで 1 回だけ）。</summary>
    private bool _warnedOutOfRange;

    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">選択のグループの引き当て。</param>
    internal SelectionTarget(WidgetRef<SelectionGroup> widgetRef) : base(widgetRef) { }

    /// <inheritdoc />
    protected override bool IsWidgetReady(SelectionGroup widget) => widget.Count > 0;

    /// <inheritdoc />
    protected override void WriteTo(SelectionGroup widget, int value)
    {
        // −1（など負の番号）: 選んでいる項目を全部外す（SelectedIndices は呼ぶたびに新しい写しを返すので、外しながら回してよい）
        if (value <= NoSelection)
        {
            foreach (int selected in widget.SelectedIndices) widget.Select(selected, selected: false);
            return;
        }

        // 範囲の外: 書かない（部品の選択は前のまま）
        if (value >= widget.Count)
        {
            if (!_warnedOutOfRange)
            {
                _warnedOutOfRange = true;
                BindingLog.Warn($"Bind.Selection: 番号 {value} は項目の数 {widget.Count} の外です。選択は変えません。");
            }
            return;
        }
        widget.Select(value);
    }

    /// <inheritdoc />
    protected override IDisposable ListenTo(SelectionGroup widget, Action<int> handler)
    {
        Action<SelectionGroup> onChanged = group => handler(group.SelectedIndex);
        widget.SelectionChanged += onChanged;
        return new DisposableAction(() => widget.SelectionChanged -= onChanged);
    }
}
