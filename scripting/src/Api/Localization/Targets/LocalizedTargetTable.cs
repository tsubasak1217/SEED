using System;
using System.Collections.Generic;
using SEED.UI;

namespace SEED.Localization;

// ============================================================
//  LocalizedTargetTable.cs — アクタ → 文字を当てる先（ILocalizedTarget）の判定の表（ここ 1 か所）
//
//  【表の引き方】上の行から順にアクタへ問い、最初に当てる先を返した行を使う（部品が先、ただの Text は最後）。
//    1. Button          … SEED.UI.Button（Button.SetText）
//    2. SelectionGroup  … SEED.UI.SegmentedControl・ChipGroup・RadioGroup（項目ごとに「キー.番号」）
//    3. Toggle          … SEED.UI.Toggle の子 Label の Text
//    4. Checkbox        … SEED.UI.Checkbox の子 Label の Text
//    5. Text            … アクタ自身の Text コンポーネント
//  部品（UiWidget）は OnStart で登録簿へ載るまで引けないので、LocalizedLabel は登録簿が変わるたびに引き直す。
//  <b>種類を足すときはこの表に 1 行足す</b>（当てる先のクラスを Targets/ に足し、部品には手を入れない）。
// ============================================================

/// <summary>アクタ → 文字を当てる先の判定の表。</summary>
public static class LocalizedTargetTable
{
    /// <summary>
    /// 部品の文字の子の名前（Button・SelectItem の Label と同じ約束。Toggle・Checkbox はこの名前の子の Text へ当てる）。
    /// </summary>
    public const string LabelChild = "Label";

    /// <summary>Toggle の表の名前。</summary>
    private const string ToggleKind = "Toggle";
    /// <summary>Checkbox の表の名前。</summary>
    private const string CheckboxKind = "Checkbox";

    /// <summary>表の行（種類の名前と、アクタから当てる先を作る問い。当てはまらなければ null）。</summary>
    private static readonly (string Kind, Func<GameObject, ILocalizedTarget?> Probe)[] Rows =
    {
        (ButtonLabelTarget.Kind, actor => UiWidget.Of<Button>(actor) is { } button ? new ButtonLabelTarget(button) : null),
        (SelectionLabelTarget.Kind, actor => UiWidget.Of<SelectionGroup>(actor) is { } group ? new SelectionLabelTarget(group) : null),
        (ToggleKind, actor => UiWidget.Of<Toggle>(actor) is { } toggle ? new ChildLabelTarget<Toggle>(toggle, ToggleKind) : null),
        (CheckboxKind, actor => UiWidget.Of<Checkbox>(actor) is { } checkbox ? new ChildLabelTarget<Checkbox>(checkbox, CheckboxKind) : null),
        (TextLabelTarget.Kind, TextOf),
    };

    /// <summary>表の種類の名前（上から順。ログ・docs 用）。</summary>
    public static IReadOnlyList<string> KindNames
    {
        get
        {
            var names = new string[Rows.Length];
            for (int i = 0; i < Rows.Length; i++) names[i] = Rows[i].Kind;
            return names;
        }
    }

    /// <summary>アクタの当てる先を表の上から探す（LocalizedLabel）。</summary>
    /// <param name="actor">アクタ。</param>
    /// <returns>当てる先（どの行にも当てはまらなければ null）。</returns>
    public static ILocalizedTarget? Find(GameObject actor)
    {
        if (!actor.IsValid) return null;
        foreach (var (_, probe) in Rows)
        {
            if (probe(actor) is { } target) return target;
        }
        return null;
    }

    /// <summary>アクタ自身の Text の当てる先（LocalizedText。Text が無ければ null）。</summary>
    /// <param name="actor">アクタ。</param>
    /// <returns>当てる先か null。</returns>
    public static ILocalizedTarget? TextOf(GameObject actor) =>
        actor.IsValid && actor.GetComponent<Text>() is { } text ? new TextLabelTarget(text) : null;
}
