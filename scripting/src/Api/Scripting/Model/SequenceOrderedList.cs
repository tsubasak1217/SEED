using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  SequenceOrderedList.cs — 生成の番号の昇順を保ったまま記録を足し引きする補助
//
//  登録簿の「アクタ → スクリプトの一覧」「型 → インスタンスの一覧」はどちらも生成の番号の昇順に並べておく
//  （持ち主が決まる順は OnStart の順＝ランタイムの走査順で、生成の順とは限らないため、足すときに位置を探して差し込む）。
//  番号は登録簿の中で一意なので、二分探索で位置が 1 つに決まる。
// ============================================================

/// <summary>生成の番号の昇順の一覧へ記録を足し引きする補助。</summary>
internal static class SequenceOrderedList
{
    /// <summary>番号の順を保つ位置へ記録を差し込む（同じ番号の記録が既にあれば何もしない）。</summary>
    /// <typeparam name="TInstance">インスタンスの型。</typeparam>
    /// <param name="list">生成の番号の昇順に並んだ一覧。</param>
    /// <param name="entry">足す記録。</param>
    internal static void Insert<TInstance>(List<ScriptRegistryEntry<TInstance>> list, ScriptRegistryEntry<TInstance> entry)
        where TInstance : class
    {
        int found = list.BinarySearch(entry, ScriptRegistryEntry<TInstance>.BySequence);
        // 見つかった（0 以上）＝同じ番号＝同じ記録が既にある。二重に入れない
        if (found >= 0) return;
        // 見つからないときの戻り値は「差し込む位置」のビット反転（List<T>.BinarySearch の規約）
        list.Insert(~found, entry);
    }

    /// <summary>記録を外す。</summary>
    /// <typeparam name="TInstance">インスタンスの型。</typeparam>
    /// <param name="list">生成の番号の昇順に並んだ一覧。</param>
    /// <param name="entry">外す記録。</param>
    /// <returns>外せたら true（一覧に無ければ false）。</returns>
    internal static bool Remove<TInstance>(List<ScriptRegistryEntry<TInstance>> list, ScriptRegistryEntry<TInstance> entry)
        where TInstance : class
    {
        int found = list.BinarySearch(entry, ScriptRegistryEntry<TInstance>.BySequence);
        if (found < 0) return false;
        list.RemoveAt(found);
        return true;
    }
}
