using System.Collections.Generic;

namespace SEED.Localization;

// ============================================================
//  LocalizedRegistry.cs — 多言語の文字のスクリプト（LocalizedText・LocalizedLabel）の登録簿
//
//  スクリプトどうしは GameObject から相手のスクリプトを引く API を持たない（SEED.UI の UiRegistry と同じ事情）。
//  LocalizedBinding は OnStart で自分のアクタに登録し OnDestroy で外す。ほかのスクリプトは
//  LocalizedBinding.Of<LocalizedText>(actor) で引いて、実行中の値（SetArg・SetCount）を渡す。
// ============================================================

/// <summary>多言語の文字のスクリプトの登録簿。</summary>
internal static class LocalizedRegistry
{
    /// <summary>アクタの entity（index, generation）→ そのアクタのスクリプト。</summary>
    private static readonly Dictionary<(uint, uint), List<LocalizedBinding>> ByActor = new();

    /// <summary>登録している数（診断用）。</summary>
    internal static int Count
    {
        get
        {
            int count = 0;
            foreach (var list in ByActor.Values) count += list.Count;
            return count;
        }
    }

    /// <summary>登録する。</summary>
    /// <param name="binding">スクリプト。</param>
    /// <param name="actor">アクタ。</param>
    internal static void Register(LocalizedBinding binding, Entity actor)
    {
        var key = (actor.Index, actor.Generation);
        if (!ByActor.TryGetValue(key, out var list)) ByActor[key] = list = new List<LocalizedBinding>();
        if (!list.Contains(binding)) list.Add(binding);
    }

    /// <summary>外す。</summary>
    /// <param name="binding">スクリプト。</param>
    /// <param name="actor">アクタ。</param>
    internal static void Unregister(LocalizedBinding binding, Entity actor)
    {
        var key = (actor.Index, actor.Generation);
        if (ByActor.TryGetValue(key, out var list) && list.Remove(binding) && list.Count == 0) ByActor.Remove(key);
    }

    /// <summary>アクタのスクリプトのうち型 T の最初のもの。</summary>
    /// <param name="actor">アクタ。</param>
    /// <returns>スクリプト（無ければ null。相手の OnStart の前も null）。</returns>
    internal static T? Find<T>(GameObject actor) where T : LocalizedBinding
    {
        if (!actor.IsValid) return null;
        if (!ByActor.TryGetValue((actor.Entity.Index, actor.Entity.Generation), out var list)) return null;
        foreach (var binding in list)
            if (binding is T typed) return typed;
        return null;
    }
}
