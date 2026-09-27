using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  SwipeGroup.cs — スワイプの操作の組（開いている行を 1 つにする・スクロールが始まったら閉じる。W2-3）
//
//  一覧の行（SwipeActions）を同じ組に入れると、ある行のドラッグが始まったときに他の開いている行が閉じる。
//  一覧のスクロールが始まったら持ち主のスクリプトが CloseAll を呼ぶ（OnScrollStart から）。
//  行のスクリプト同士が同じ組を引けるよう、持ち主（一覧のノード）ごとの組を `For` で引ける。
// ============================================================

/// <summary>スワイプの操作の組（開いている行を 1 つにする）。</summary>
public sealed class SwipeGroup
{
    /// <summary>持ち主（一覧のノードのエンティティ）ごとの組。</summary>
    private static readonly Dictionary<(uint Index, uint Generation), SwipeGroup> ByOwner = new();

    /// <summary>組の行。</summary>
    private readonly List<SwipeActions> _members = new();

    /// <summary>持ち主（一覧のノード）の組を引く（無ければ作る）。</summary>
    public static SwipeGroup For(GameObject owner)
    {
        var key = (owner.Entity.Index, owner.Entity.Generation);
        if (!ByOwner.TryGetValue(key, out var group))
        {
            group = new SwipeGroup();
            ByOwner[key] = group;
        }
        return group;
    }

    /// <summary>持ち主の組を捨てる（一覧を破棄したとき）。</summary>
    public static void Release(GameObject owner) => ByOwner.Remove((owner.Entity.Index, owner.Entity.Generation));

    /// <summary>組の行の数。</summary>
    public int Count => _members.Count;

    /// <summary>開いている（開く途中を含む）行。無ければ null。</summary>
    public SwipeActions? OpenMember => _members.Find(m => m.IsOpen);

    /// <summary>行を加える（SwipeActions.Group の set から呼ばれる）。</summary>
    public void Add(SwipeActions member)
    {
        if (!_members.Contains(member)) _members.Add(member);
    }

    /// <summary>行を外す。</summary>
    public void Remove(SwipeActions member) => _members.Remove(member);

    /// <summary>指定の行以外の開いている行を閉じる。</summary>
    public void CloseOthers(SwipeActions keep, bool animated = true)
    {
        foreach (var m in _members)
        {
            if (!ReferenceEquals(m, keep) && m.IsOpen) m.Close(animated);
        }
    }

    /// <summary>すべての開いている行を閉じる（一覧のスクロールが始まったとき）。</summary>
    public void CloseAll(bool animated = true)
    {
        foreach (var m in _members)
        {
            if (m.IsOpen) m.Close(animated);
        }
    }
}
