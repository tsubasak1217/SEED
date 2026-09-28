using System.Collections.Generic;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  UiRegistry.cs — 部品のスクリプト（UiWidget）の登録簿（W2-4）
//
//  スクリプトどうしは GameObject から相手のスクリプトを引く API を持たないので、部品は OnStart で自分の GameObject
//  （アクターの entity）に登録し、OnDestroy で外す。組み合わせる側（数値欄のボタン・選択の項目とグループ・画面のスクリプト）は
//  `UiWidget.Of<T>(gameObject)` で相手を引く。スクリプトの OnStart の順は決まっていないので、引く側は見つかるまで
//  毎フレーム引き直してよい（`Version` が変わったときだけ引き直せば安い）。
//  W2-9: テーマの切り替え（UiTheme.Apply・明暗の変化・色の補間）は、ここの全部品の見た目をその場で当て直す（Snapshot）。
// ============================================================

/// <summary>部品のスクリプトの登録簿。</summary>
public static class UiRegistry
{
    /// <summary>アクターの entity（index, generation）→ そのアクターの部品（1 アクターに複数あり得る）。</summary>
    private static readonly Dictionary<(uint, uint), List<UiWidget>> ByActor = new();

    /// <summary>登録・解除のたびに増える番号（引く側が引き直す合図）。</summary>
    public static int Version { get; private set; }

    /// <summary>登録する。</summary>
    internal static void Register(UiWidget widget, Entity actor)
    {
        var key = (actor.Index, actor.Generation);
        if (!ByActor.TryGetValue(key, out var list)) ByActor[key] = list = new List<UiWidget>();
        if (!list.Contains(widget)) list.Add(widget);
        Version++;
    }

    /// <summary>解除する。</summary>
    internal static void Unregister(UiWidget widget, Entity actor)
    {
        var key = (actor.Index, actor.Generation);
        if (ByActor.TryGetValue(key, out var list) && list.Remove(widget))
        {
            if (list.Count == 0) ByActor.Remove(key);
            Version++;
        }
    }

    /// <summary>登録している部品の数（診断用）。</summary>
    public static int Count
    {
        get
        {
            int count = 0;
            foreach (var list in ByActor.Values) count += list.Count;
            return count;
        }
    }

    /// <summary>
    /// 登録しているすべての部品の写し（テーマの当て直し用。当て直しの途中で登録・解除が起きても壊れないように写す）。
    /// </summary>
    internal static List<UiWidget> Snapshot()
    {
        var all = new List<UiWidget>();
        foreach (var list in ByActor.Values) all.AddRange(list);
        return all;
    }

    /// <summary>アクターの部品のうち型 T の最初のもの（無ければ null）。</summary>
    public static T? Find<T>(GameObject actor) where T : UiWidget
    {
        if (!actor.IsValid) return null;
        return ByActor.TryGetValue((actor.Entity.Index, actor.Entity.Generation), out var list) ? list.OfType<T>().FirstOrDefault() : null;
    }

    /// <summary>親のアクターが <paramref name="parent"/> の、型 T の部品（登録の順）。</summary>
    public static IEnumerable<T> ChildrenOf<T>(GameObject parent) where T : UiWidget
    {
        if (!parent.IsValid) yield break;
        foreach (var list in ByActor.Values)
            foreach (var w in list)
                if (w is T t && w.ParentKey == (parent.Entity.Index, parent.Entity.Generation))
                    yield return t;
    }
}
