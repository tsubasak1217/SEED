using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  SegmentedControl.cs — セグメント（横に並んだボタンから 1 つ選ぶ。W2-4。docs/ui_components.md §3）
//
//  【プレハブ】templates/ui/prefabs/segmented.actor
//      SegmentedControl（Sprite = 台〈角丸〉・このスクリプト）
//      ├─ Seg0（SelectItem の作り: Sprite・CanvasGesture・SEED.UI.SelectItem〈Index 0〉・Label）
//      └─ …
//  必ず 1 つ選ぶ（選んだ項目をもう一度押しても変わらない）。台は面の変種の色、選んだ項目は選択の色で塗る。
//  項目の集め方・選択の状態・見た目の当て方は SelectionGroup（共通）。
// ============================================================

/// <summary>セグメント（横に並んだボタンから 1 つ選ぶ。必ず 1 つ）。</summary>
public sealed class SegmentedControl : SelectionGroup
{
    /// <inheritdoc />
    protected override SelectionMode Mode => SelectionMode.Single;
    /// <inheritdoc />
    protected override SelectionLookKind LookKind => SelectionLookKind.Segment;

    /// <inheritdoc />
    protected override void ApplyLook()
    {
        // 台（自分の Sprite）: 面の変種の色・セグメントの角丸
        if (SpriteOf() is { } track)
        {
            track.Color = Theme.Color(UiTokens.ColorSurfaceVariant);
            track.CornerRadius = Theme.Number(UiTokens.RadiusSegment);
        }
        base.ApplyLook();
    }
}
