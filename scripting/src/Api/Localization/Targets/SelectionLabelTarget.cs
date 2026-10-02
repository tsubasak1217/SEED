using System.Globalization;
using SEED.UI;

namespace SEED.Localization;

// ============================================================
//  SelectionLabelTarget.cs — 選択のグループ（SEED.UI.SegmentedControl・ChipGroup・RadioGroup）の項目の文字へ当てる当てる先
//
//  項目（直下の子の SEED.UI.SelectItem）ごとに、子「Label」の Text へ「キー.番号」（番号は SelectItem.Index）の文を当てる。
//  言語の表では配列で書ける: "theme_mode": ["端末に合わせる", "明るい", "暗い"] → theme_mode.0・theme_mode.1・theme_mode.2。
//  項目は公開の API（UiRegistry.ChildrenOf・SelectItem.Index・UiWidget.Owner）で引く。項目の OnStart がまだなら
//  その項目は当たらないので、LocalizedLabel は登録簿（UiRegistry.Version）が変わるたびに当て直す。
// ============================================================

/// <summary>選択のグループの項目の文字へ当てる。</summary>
public sealed class SelectionLabelTarget : ILocalizedTarget
{
    /// <summary>表の種類の名前。</summary>
    public const string Kind = "SelectionGroup";

    /// <summary>当てるグループ。</summary>
    private readonly SelectionGroup _group;

    /// <summary>当てる先を作る。</summary>
    /// <param name="group">当てるグループ。</param>
    public SelectionLabelTarget(SelectionGroup group)
    {
        _group = group;
    }

    /// <inheritdoc />
    public string KindName => Kind;

    /// <inheritdoc />
    public bool IsAlive => _group.Owner.IsValid && ReferenceEquals(UiWidget.Of<SelectionGroup>(_group.Owner), _group);

    /// <summary>
    /// 項目ごとに文字を当てる。項目がまだ 1 つも登録簿に無ければ（項目の OnStart の前）当てる物が無いだけなので true
    /// （LocalizedLabel が登録簿の変化で当て直す）。項目があるのにどれも文字の子を持たなければ false。
    /// </summary>
    public bool Apply(LocalizedRequest request)
    {
        if (!_group.Owner.IsValid) return false;
        int items = 0;
        int applied = 0;
        foreach (var item in UiRegistry.ChildrenOf<SelectItem>(_group.Owner))
        {
            items++;
            if (item.Owner.FindChild(LocalizedTargetTable.LabelChild).GetComponent<Text>() is not { } label) continue;
            label.Content = request.ResolveChild(item.Index.ToString(CultureInfo.InvariantCulture));
            applied++;
        }
        return items == 0 || applied > 0;
    }
}
