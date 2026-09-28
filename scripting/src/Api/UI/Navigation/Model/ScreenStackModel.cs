using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ScreenStackModel.cs — 画面のスタックの積み下ろしと状態（W2-7。純粋な計算・エンジンに触れない）
//
//  ScreenStack（エンジンの部品）の「どの画面が何段目にいて、見せるか・作り直すか」を決める本体。
//  操作（SetRoot・Push・Pop・Replace・PopToRoot）は並びを変え、出入りの計画（ScreenChange）を返す。
//  部品は計画どおりに実体（プレハブ）を作り・動かし、動きが終わったら Settle で落ち着いた状態を受け取る。
//
//  【見せる・隠す（Settle）】いちばん上の画面は見せる。上から下へ、不透明（Opaque）でない画面の下の画面も見せる
//  （透ける画面の後ろは描く）。それより下は隠す（描かない・当たり判定に出ない＝入力も受けない）。
//  【状態を保つか（KeepState）】隠した画面のうち KeepState = false のものは実体を手放す（戻ってきたら作り直す）。
//  KeepState = true（既定）は隠したまま持つ（スクロールの位置・入力の途中が残る）。
//  【根】いちばん下の画面（根）は Pop で下ろせない（戻るはタブ・アプリへ回る）。
// ============================================================

/// <summary>画面ごとの指定（積むときに渡す）。</summary>
public sealed class ScreenOptions
{
    /// <summary>出入りの種類（null = スタックの既定）。</summary>
    public NavTransition? Transition { get; init; }
    /// <summary>不透明か（true なら落ち着いた後、下の画面を隠して描かない）。</summary>
    public bool Opaque { get; init; } = true;
    /// <summary>覆われている間も実体を保つか（false なら手放し、戻ってきたら作り直す）。</summary>
    public bool KeepState { get; init; } = true;
    /// <summary>戻る（Escape・Android の戻る）を無視するか（鳴動の画面など。画面のスクリプトの OnBackPressed より先に効く）。</summary>
    public bool IgnoreBack { get; init; }
    /// <summary>画面を安全領域の内側に置くか（false ならプレハブが自分で安全領域を扱う。背景は常に画面の端まで）。</summary>
    public bool SafeArea { get; init; } = true;

    /// <summary>既定の指定。</summary>
    public static ScreenOptions Default { get; } = new();
}

/// <summary>画面の 1 段（スタックの要素）。</summary>
public sealed class ScreenEntry
{
    /// <summary>スタックの中で一意の番号（1 から）。</summary>
    public int Id { get; }
    /// <summary>画面のプレハブ（assets:// の .actor）。</summary>
    public string Prefab { get; }
    /// <summary>指定。</summary>
    public ScreenOptions Options { get; }
    /// <summary>画面へ渡す値（UiScreen.OnScreenEnter に届く）。</summary>
    public object? Args { get; }
    /// <summary>実体があるか（作った・作り直した = true、手放した = false）。</summary>
    public bool HasInstance { get; internal set; }
    /// <summary>見せているか（落ち着いた状態で）。</summary>
    public bool Visible { get; internal set; }
    /// <summary>スタックから外れたか（下ろした・置き換えた・根からやり直した）。</summary>
    public bool Removed { get; internal set; }
    /// <summary>下ろしたときの結果（Pop の引数）。</summary>
    public object? Result { get; internal set; }

    internal ScreenEntry(int id, string prefab, ScreenOptions options, object? args)
    {
        Id = id;
        Prefab = prefab;
        Options = options;
        Args = args;
    }

    /// <inheritdoc />
    public override string ToString() => $"#{Id}({Prefab})";
}

/// <summary>操作の種類。</summary>
public enum ScreenOpKind
{
    /// <summary>根からやり直す（すべて外して新しい根）。</summary>
    SetRoot = 0,
    /// <summary>積む。</summary>
    Push = 1,
    /// <summary>1 つ下ろす。</summary>
    Pop = 2,
    /// <summary>いちばん上を置き換える。</summary>
    Replace = 3,
    /// <summary>根まで下ろす。</summary>
    PopToRoot = 4,
}

/// <summary>1 回の操作の出入りの計画。</summary>
public sealed class ScreenChange
{
    /// <summary>操作。</summary>
    public ScreenOpKind Kind { get; init; }
    /// <summary>出入りの種類（動かさないなら None）。</summary>
    public NavTransition Transition { get; init; }
    /// <summary>向き（Pop・PopToRoot は戻る）。</summary>
    public NavDirection Direction { get; init; }
    /// <summary>終わったときに上にいる画面（実体が無ければ作る・作り直す）。</summary>
    public ScreenEntry? Incoming { get; init; }
    /// <summary>動きの間に退く画面（Push では覆われて残る、Pop・Replace では外れる）。</summary>
    public ScreenEntry? Outgoing { get; init; }
    /// <summary>動かさずに外す画面（PopToRoot の途中の段・SetRoot の旧い段）。</summary>
    public IReadOnlyList<ScreenEntry> Removed { get; init; } = Array.Empty<ScreenEntry>();
    /// <summary>退く画面がスタックから外れるか（Pop・Replace・PopToRoot・SetRoot）。</summary>
    public bool OutgoingRemoved => Outgoing is { Removed: true };
}

/// <summary>落ち着いた状態（動きの後に部品が当てる）。</summary>
public sealed class ScreenSettle
{
    /// <summary>見せる画面（下から上の順）。</summary>
    public IReadOnlyList<ScreenEntry> Visible { get; init; } = Array.Empty<ScreenEntry>();
    /// <summary>隠す画面（実体は保つ）。</summary>
    public IReadOnlyList<ScreenEntry> Hidden { get; init; } = Array.Empty<ScreenEntry>();
    /// <summary>実体を手放す画面（隠して KeepState = false）。</summary>
    public IReadOnlyList<ScreenEntry> Release { get; init; } = Array.Empty<ScreenEntry>();
}

/// <summary>画面のスタックの積み下ろしと状態。</summary>
public sealed class ScreenStackModel
{
    /// <summary>画面の段（添字 0 = 根）。</summary>
    private readonly List<ScreenEntry> _entries = new();
    /// <summary>次に振る番号。</summary>
    private int _nextId = 1;

    /// <summary>段の数。</summary>
    public int Count => _entries.Count;
    /// <summary>いちばん上（無ければ null）。</summary>
    public ScreenEntry? Top => _entries.Count > 0 ? _entries[^1] : null;
    /// <summary>根（無ければ null）。</summary>
    public ScreenEntry? Root => _entries.Count > 0 ? _entries[0] : null;
    /// <summary>下から上の順の段。</summary>
    public IReadOnlyList<ScreenEntry> Entries => _entries;
    /// <summary>下ろせるか（根より上に段がある）。</summary>
    public bool CanPop => _entries.Count > 1;

    /// <summary>番号から段を引く（無ければ null）。</summary>
    public ScreenEntry? Find(int id) => _entries.Find(e => e.Id == id);

    /// <summary>段の添字（無ければ −1。0 = 根）。</summary>
    public int IndexOf(ScreenEntry entry) => _entries.IndexOf(entry);

    /// <summary>根からやり直す（旧い段はすべて外す）。</summary>
    public ScreenChange SetRoot(string prefab, ScreenOptions? options = null, object? args = null, NavTransition transition = NavTransition.None)
    {
        var removed = new List<ScreenEntry>(_entries);
        foreach (var e in removed) e.Removed = true;
        _entries.Clear();
        var root = NewEntry(prefab, options, args);
        _entries.Add(root);
        // 旧いいちばん上を「退く画面」にして動かし（フェードなど）、残りは動かさずに外す
        ScreenEntry? outgoing = removed.Count > 0 ? removed[^1] : null;
        if (outgoing is not null) removed.RemoveAt(removed.Count - 1);
        return new ScreenChange
        {
            Kind = ScreenOpKind.SetRoot,
            Transition = outgoing is null ? NavTransition.None : transition,
            Direction = NavDirection.Forward,
            Incoming = root,
            Outgoing = outgoing,
            Removed = removed,
        };
    }

    /// <summary>積む。</summary>
    /// <param name="prefab">画面のプレハブ。</param>
    /// <param name="options">指定（null = 既定）。</param>
    /// <param name="args">画面へ渡す値。</param>
    /// <param name="defaultTransition">指定に出入りの種類が無いときの種類（スタックの既定）。</param>
    public ScreenChange Push(string prefab, ScreenOptions? options, object? args, NavTransition defaultTransition)
    {
        var outgoing = Top;
        var entry = NewEntry(prefab, options, args);
        _entries.Add(entry);
        return new ScreenChange
        {
            Kind = _entries.Count == 1 ? ScreenOpKind.SetRoot : ScreenOpKind.Push,
            Transition = outgoing is null ? NavTransition.None : entry.Options.Transition ?? defaultTransition,
            Direction = NavDirection.Forward,
            Incoming = entry,
            Outgoing = outgoing,
        };
    }

    /// <summary>1 つ下ろす（根だけなら null＝何もしない）。出入りの種類は下ろす画面が積まれたときと同じ。</summary>
    /// <param name="result">下ろす画面の結果。</param>
    /// <param name="defaultTransition">下ろす画面に種類の指定が無いときの種類。</param>
    public ScreenChange? Pop(object? result, NavTransition defaultTransition)
    {
        if (!CanPop) return null;
        var outgoing = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        outgoing.Removed = true;
        outgoing.Result = result;
        return new ScreenChange
        {
            Kind = ScreenOpKind.Pop,
            Transition = outgoing.Options.Transition ?? defaultTransition,
            Direction = NavDirection.Backward,
            Incoming = Top,
            Outgoing = outgoing,
        };
    }

    /// <summary>いちばん上を置き換える（空なら積むのと同じ）。</summary>
    public ScreenChange Replace(string prefab, ScreenOptions? options, object? args, NavTransition defaultTransition)
    {
        if (_entries.Count == 0) return Push(prefab, options, args, defaultTransition);
        var outgoing = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        outgoing.Removed = true;
        var entry = NewEntry(prefab, options, args);
        _entries.Add(entry);
        return new ScreenChange
        {
            Kind = ScreenOpKind.Replace,
            Transition = entry.Options.Transition ?? defaultTransition,
            Direction = NavDirection.Forward,
            Incoming = entry,
            Outgoing = outgoing,
        };
    }

    /// <summary>根まで下ろす（根だけなら null）。途中の段は動かさずに外し、いちばん上だけを戻る向きに動かす。</summary>
    public ScreenChange? PopToRoot(NavTransition defaultTransition)
    {
        if (!CanPop) return null;
        var outgoing = _entries[^1];
        var removed = _entries.GetRange(1, _entries.Count - 2);
        _entries.RemoveRange(1, _entries.Count - 1);
        outgoing.Removed = true;
        foreach (var e in removed) e.Removed = true;
        return new ScreenChange
        {
            Kind = ScreenOpKind.PopToRoot,
            Transition = outgoing.Options.Transition ?? defaultTransition,
            Direction = NavDirection.Backward,
            Incoming = Top,
            Outgoing = outgoing,
            Removed = removed,
        };
    }

    /// <summary>
    /// 落ち着いた状態を決めて段へ書く（見せる・隠す・手放す）。動きの後に 1 回呼ぶ。
    /// </summary>
    public ScreenSettle Settle()
    {
        var visible = new List<ScreenEntry>();
        var hidden = new List<ScreenEntry>();
        var release = new List<ScreenEntry>();
        bool covered = false;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!covered)
            {
                e.Visible = true;
                visible.Insert(0, e);
                // 不透明な画面より下は隠れる（透ける画面の下は見せ続ける）
                covered = e.Options.Opaque;
                continue;
            }
            e.Visible = false;
            hidden.Insert(0, e);
            if (!e.Options.KeepState && e.HasInstance)
            {
                e.HasInstance = false;
                release.Insert(0, e);
            }
        }
        return new ScreenSettle { Visible = visible, Hidden = hidden, Release = release };
    }

    /// <summary>実体を作った（作り直した）ことを記す。</summary>
    public static void MarkInstantiated(ScreenEntry entry) => entry.HasInstance = true;

    /// <summary>新しい段を作る（番号を振る）。</summary>
    private ScreenEntry NewEntry(string prefab, ScreenOptions? options, object? args)
        => new(_nextId++, prefab, options ?? ScreenOptions.Default, args);
}
