namespace SEED.UI;

// ============================================================
//  SelectionGroupNav.cs — セグメント・ラジオ（1 つ選ぶ SelectionGroup）の方向キーのアダプタ（2026-10-03。L3-6）
//
//  グループそのものを 1 つの移り先にし、左右 = 選択を前・後ろの選べる項目へ動かす（SelectionGroup.NavStep。選んだら SelectionChanged）。
//  左右はいつも受ける（端でもフォーカスを移さない）。上下はフォーカスを移す（縦に並べたラジオでも左右で選ぶ）。決定は何もしない。
//  チップ（ChipGroup。複数選ぶ）はグループではなく札ごとに移る（SelectItemNav）ので、NavAdapters はチップにこれを被せない。
// ============================================================

/// <summary>セグメント・ラジオのアダプタ。</summary>
internal sealed class SelectionGroupNav : WidgetNav<SelectionGroup>
{
    /// <summary>グループに被せる。</summary>
    /// <param name="group">セグメントかラジオ。</param>
    public SelectionGroupNav(SelectionGroup group) : base(group) { }

    /// <inheritdoc />
    public override bool IsNavigable => Widget.Count > 0 && base.IsNavigable;

    /// <inheritdoc />
    public override NavAxis AdjustAxis => NavAxis.Horizontal;

    /// <inheritdoc />
    public override void OnNavSubmit() { }

    /// <inheritdoc />
    public override bool OnNavAdjust(int direction)
    {
        Widget.NavStep(direction);
        return true;
    }
}
