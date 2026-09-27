using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  ChipGroup.cs — チップ（丸い札。複数選ぶ／1 つ選ぶ。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/chip_group.actor
//      ChipGroup（このスクリプト。並べるなら CanvasWrap〈折り返し〉と組み合わせる）
//      ├─ Chip0（SelectItem の作り: Sprite〈角丸・枠〉・CanvasGesture・SEED.UI.SelectItem〈Index 0〉・Label）
//      └─ …
//  Multiple = true（既定）で複数選ぶ（曜日など）、false で 1 つ選び、選んだ札をもう一度押すと外れる。
//  項目の集め方・選択の状態・見た目の当て方は SelectionGroup（共通）。
// ============================================================

/// <summary>チップ（丸い札。既定は複数選ぶ）。</summary>
public sealed class ChipGroup : SelectionGroup
{
    /// <summary>複数選べるか（false なら 1 つ選び、選んだ札をもう一度押すと外れる）。</summary>
    [SerializeField(Label = "複数選ぶ")]
    public bool Multiple = true;

    /// <inheritdoc />
    protected override SelectionMode Mode => Multiple ? SelectionMode.Multiple : SelectionMode.SingleOptional;
    /// <inheritdoc />
    protected override SelectionLookKind LookKind => SelectionLookKind.Chip;
}
