using SEED.UI;

namespace SEED.Localization;

// ============================================================
//  ChildLabelTarget.cs — 文字の API を持たない部品（SEED.UI.Toggle・Checkbox）の子の文字（Label の Text）へ当てる当てる先
//
//  templates/ui の toggle.actor・checkbox.actor は文字の子を持たない（台とつまみ・箱と印だけ）。部品の横に文字を出すには
//  ①部品のアクタに子「Label」（Text）を足してこの当てる先で当てる（Button・SelectItem の Label と同じ名前の約束）か、
//  ②別の Text のアクタに LocalizedText を付ける。子が無ければ Apply は false（LocalizedLabel が 1 度だけ警告する）。
//  T は部品の型（生きているかを登録簿で確かめるため。UiWidget.Of<T> が同じ部品を返す間は生きている）。
// ============================================================

/// <summary>部品の子の文字（Label の Text）へ当てる。</summary>
/// <typeparam name="T">部品の型（Toggle・Checkbox など）。</typeparam>
public sealed class ChildLabelTarget<T> : ILocalizedTarget where T : UiWidget
{
    /// <summary>当てる部品。</summary>
    private readonly T _widget;

    /// <summary>当てる先を作る。</summary>
    /// <param name="widget">当てる部品。</param>
    /// <param name="kindName">表の種類の名前（"Toggle" など）。</param>
    public ChildLabelTarget(T widget, string kindName)
    {
        _widget = widget;
        KindName = kindName;
    }

    /// <inheritdoc />
    public string KindName { get; }

    /// <inheritdoc />
    public bool IsAlive => _widget.Owner.IsValid && ReferenceEquals(UiWidget.Of<T>(_widget.Owner), _widget);

    /// <inheritdoc />
    public bool Apply(LocalizedRequest request)
    {
        if (!_widget.Owner.IsValid) return false;
        if (_widget.Owner.FindChild(LocalizedTargetTable.LabelChild).GetComponent<Text>() is not { } label) return false;
        label.Content = request.Resolve();
        return true;
    }
}
