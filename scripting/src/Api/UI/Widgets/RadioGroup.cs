using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  RadioGroup.cs — ラジオ（丸い輪と点。1 つ選ぶ。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/radio_group.actor
//      RadioGroup（このスクリプト）
//      ├─ Radio0（SelectItem の作り: Sprite〈楕円の輪〉・CanvasGesture〈最小 48 dp〉・SEED.UI.SelectItem〈Index 0〉・Dot・Label）
//      └─ …
//  必ず 1 つ選ぶ。選んだ項目は輪を主の色にして中の点を出す。
//  項目の集め方・選択の状態・見た目の当て方は SelectionGroup（共通）。
// ============================================================

/// <summary>ラジオ（丸い輪と点。1 つ選ぶ）。</summary>
public sealed class RadioGroup : SelectionGroup
{
    /// <inheritdoc />
    protected override SelectionMode Mode => SelectionMode.Single;
    /// <inheritdoc />
    protected override SelectionLookKind LookKind => SelectionLookKind.Radio;
}
