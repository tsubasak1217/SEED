using System;
using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  ScriptInstanceRegistry.Queries.cs — スクリプトの登録簿の問い合わせ（アクタを指定して引く・全体から引く）
//
//  アクタを指定して引く（GetScript 系の元）:
//    1. 持ち主の決まった記録（そのアクタの一覧。生成の番号の順＝スロットの順）から型の合うものを拾う
//    2. 持ち主が未定の記録（まだ OnStart を迎えていないスクリプト）の型のうち T に当たるものごとに、
//       ScriptSlotProbe でそのアクタのスロットを問い合わせ、居れば拾う（同じ型の名前は先頭の 1 つだけ引ける）
//    3. 生成の番号の順にまとめる
//  全体から引く（Instances / FindInstance の元）: 持ち主の決まったものだけ（型の一覧を T に当たる型だけ集める）。
//
//  返す配列はどれもその時点の写し（受け取った側が列挙の途中でスクリプトを破棄しても壊れない）。
//  T には基底の型・インターフェースも渡せる（具体的な型が T に代入できれば当たる＝派生型も当たる）。
// ============================================================

internal sealed partial class ScriptInstanceRegistry<TInstance> where TInstance : class
{
    /// <summary>
    /// アクタのスクリプトのうち、T に当たる最初のもの（生成の番号の順＝スロットの順）。
    /// </summary>
    /// <typeparam name="T">探す型（基底の型・インターフェースも可。派生型も当たる）。</typeparam>
    /// <param name="owner">アクタ。</param>
    /// <param name="probe">まだ持ち主の決まっていないスクリプトをランタイムへ問い合わせる口（null なら問い合わせない）。</param>
    /// <returns>見つかったインスタンス。無ければ null。</returns>
    internal T? FirstOnOwner<T>(ScriptOwnerKey owner, ScriptSlotProbe<TInstance>? probe) where T : class
    {
        ScriptRegistryEntry<TInstance>? best = null;

        // 1. 持ち主の決まったもの（一覧は生成の番号の順なので、最初に当たったものが最小の番号）
        if (_byOwner.TryGetValue(owner, out var bound))
        {
            foreach (var entry in bound)
            {
                if (entry.Instance is not T) continue;
                best = entry;
                break;
            }
        }

        // 2. まだ OnStart を迎えていないもの（スロットの先頭側に居れば、こちらが先になる）
        var probed = ProbeUnbound<T>(owner, probe);
        if (probed is not null)
        {
            foreach (var entry in probed)
            {
                if (best is null || entry.Sequence < best.Sequence) best = entry;
            }
        }

        return best is null ? null : (T)(object)best.Instance;
    }

    /// <summary>
    /// アクタのスクリプトのうち、T に当たるもの全部（生成の番号の順＝スロットの順。その時点の写し）。
    /// </summary>
    /// <typeparam name="T">探す型（基底の型・インターフェースも可。派生型も当たる）。</typeparam>
    /// <param name="owner">アクタ。</param>
    /// <param name="probe">まだ持ち主の決まっていないスクリプトをランタイムへ問い合わせる口（null なら問い合わせない）。</param>
    /// <returns>見つかったインスタンスの配列（無ければ空の配列）。</returns>
    internal T[] AllOnOwner<T>(ScriptOwnerKey owner, ScriptSlotProbe<TInstance>? probe) where T : class
    {
        var found = new List<ScriptRegistryEntry<TInstance>>();

        // 1. 持ち主の決まったもの（生成の番号の順に並んでいる）
        if (_byOwner.TryGetValue(owner, out var bound))
        {
            foreach (var entry in bound)
            {
                if (entry.Instance is T) found.Add(entry);
            }
        }

        // 2. まだ OnStart を迎えていないもの。混ざったら生成の番号の順へ並べ直す
        var probed = ProbeUnbound<T>(owner, probe);
        if (probed is not null)
        {
            found.AddRange(probed);
            found.Sort(ScriptRegistryEntry<TInstance>.BySequence);
        }

        return ToArray<T>(found);
    }

    /// <summary>
    /// 全体（持ち主の決まったもの）のうち、T に当たる最初のもの（生成の番号の順）。
    /// </summary>
    /// <typeparam name="T">探す型（基底の型・インターフェースも可。派生型も当たる）。</typeparam>
    /// <returns>見つかったインスタンス。無ければ null。</returns>
    internal T? FirstInstance<T>() where T : class
    {
        ScriptRegistryEntry<TInstance>? best = null;
        foreach (var (type, list) in _byType)
        {
            // 型ごとの一覧は生成の番号の順なので、先頭だけ比べればよい
            if (list.Count == 0 || !typeof(T).IsAssignableFrom(type)) continue;
            var head = list[0];
            if (best is null || head.Sequence < best.Sequence) best = head;
        }
        return best is null ? null : (T)(object)best.Instance;
    }

    /// <summary>
    /// 全体（持ち主の決まったもの）のうち、T に当たるもの全部（生成の番号の順。その時点の写し）。
    /// </summary>
    /// <typeparam name="T">探す型（基底の型・インターフェースも可。派生型も当たる）。</typeparam>
    /// <returns>インスタンスの配列（無ければ空の配列）。</returns>
    internal T[] AllInstances<T>() where T : class
    {
        List<ScriptRegistryEntry<TInstance>>? found = null;
        int matchedTypes = 0;
        foreach (var (type, list) in _byType)
        {
            if (!typeof(T).IsAssignableFrom(type)) continue;
            (found ??= new List<ScriptRegistryEntry<TInstance>>()).AddRange(list);
            matchedTypes++;
        }
        if (found is null) return Array.Empty<T>();

        // 型が 1 つならその一覧は既に生成の番号の順。複数の型が混ざったときだけ並べ直す
        if (matchedTypes > 1) found.Sort(ScriptRegistryEntry<TInstance>.BySequence);
        return ToArray<T>(found);
    }

    // ── 内部用 ──────────────────────────────────────────────

    /// <summary>
    /// 持ち主が未定の記録のうち T に当たる型ごとに、そのアクタのスロットをランタイムへ問い合わせ、
    /// 居たもの（まだ持ち主の決まっていない記録）を返す。候補が無い・何も居なければ null。
    /// </summary>
    /// <typeparam name="T">探す型。</typeparam>
    /// <param name="owner">アクタ。</param>
    /// <param name="probe">問い合わせる口（null なら問い合わせない）。</param>
    private List<ScriptRegistryEntry<TInstance>>? ProbeUnbound<T>(ScriptOwnerKey owner, ScriptSlotProbe<TInstance>? probe)
        where T : class
    {
        if (probe is null || _unboundCountByType.Count == 0) return null;

        // 候補の型を先に写す（問い合わせの途中で登録簿が変わっても、表の列挙が壊れないように）
        List<Type>? candidates = null;
        foreach (var type in _unboundCountByType.Keys)
        {
            if (typeof(T).IsAssignableFrom(type)) (candidates ??= new List<Type>()).Add(type);
        }
        if (candidates is null) return null;

        List<ScriptRegistryEntry<TInstance>>? probed = null;
        foreach (var type in candidates)
        {
            var instance = probe(owner, type);
            // ランタイムは型の名前で照合するので、名前だけ同じ別の型が返ることがある。T に当たるかを確かめる
            if (instance is not T) continue;
            // 持ち主の決まったもの（1. で数えた）・登録簿の知らないものは数えない
            if (!_entries.TryGetValue(instance, out var entry) || entry.IsBound) continue;
            // 名前の同じ別の型の候補から同じインスタンスが返ったときに二重に数えない
            if (probed is not null && probed.Contains(entry)) continue;
            (probed ??= new List<ScriptRegistryEntry<TInstance>>()).Add(entry);
        }
        return probed;
    }

    /// <summary>記録の一覧をインスタンスの配列へ写す（呼び出し側で T に当たるものだけに絞ってあること）。</summary>
    private static T[] ToArray<T>(List<ScriptRegistryEntry<TInstance>> entries) where T : class
    {
        if (entries.Count == 0) return Array.Empty<T>();
        var result = new T[entries.Count];
        for (int i = 0; i < entries.Count; i++) result[i] = (T)(object)entries[i].Instance;
        return result;
    }
}
