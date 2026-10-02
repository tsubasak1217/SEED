using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ListViewNavSource.cs — 方向キーの候補にする一覧（ListView）の行の出所（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  ListView はスクリプトではない（持ち主のスクリプトが作って Update を呼ぶ）ので、作られたときにここへ弱く登録する
//  （持ち主が捨てたら GC で外れる。スクロールの窓のノードが消えた一覧も飛ばす）。候補を集めるときに、見せている行
//  （ListView.ShownRows）ごとに行のアダプタ（ListRowNav）を返す。アダプタは行のノードごとに 1 つ（使い回しの行は
//  別の番号のデータに変わっても同じノード＝同じアダプタ。今のフォーカスはノードを指したまま）。
// ============================================================

/// <summary>一覧の行の出所。</summary>
internal static class ListViewNavSource
{
    /// <summary>作られた一覧（弱く持つ）。</summary>
    private static readonly List<WeakReference<ListView>> Lists = new();
    /// <summary>行のノード → アダプタ。</summary>
    private static readonly Dictionary<(uint, uint), ListRowNav> Rows = new();

    /// <summary>一覧を登録する（ListView の作り手から）。</summary>
    /// <param name="list">一覧。</param>
    public static void Register(ListView list)
    {
        Prune();
        Lists.Add(new WeakReference<ListView>(list));
    }

    /// <summary>見せている行のアダプタを足す（消えた行のアダプタは捨てる）。</summary>
    /// <param name="into">足す先。</param>
    public static void Collect(List<IUiNavigable> into)
    {
        Prune();
        foreach (var weak in Lists)
        {
            if (!weak.TryGetTarget(out var list) || !list.ScrollObject.IsValid) continue;
            foreach (var row in list.ShownRows())
            {
                if (!row.IsValid) continue;
                var key = NavNode.Key(row);
                if (!Rows.TryGetValue(key, out var nav))
                {
                    nav = new ListRowNav(row);
                    Rows[key] = nav;
                }
                into.Add(nav);
            }
        }
    }

    /// <summary>行のノードのアダプタ（集めたことのある行だけ。無ければ null）。</summary>
    /// <param name="row">行の根のノード。</param>
    public static IUiNavigable? Find(GameObject row)
        => row.IsValid && Rows.TryGetValue(NavNode.Key(row), out var nav) ? nav : null;

    /// <summary>すべて忘れる（スクリプトの読み直し）。</summary>
    public static void Reset()
    {
        Lists.Clear();
        Rows.Clear();
    }

    /// <summary>捨てられた一覧と消えた行のアダプタを外す。</summary>
    private static void Prune()
    {
        Lists.RemoveAll(w => !w.TryGetTarget(out var list) || !list.ScrollObject.IsValid);
        List<(uint, uint)>? dead = null;
        foreach (var pair in Rows)
            if (!pair.Value.NavNode.IsValid) (dead ??= new List<(uint, uint)>()).Add(pair.Key);
        if (dead is null) return;
        foreach (var key in dead) Rows.Remove(key);
    }
}
