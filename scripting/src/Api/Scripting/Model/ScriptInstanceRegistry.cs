using System;
using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  ScriptInstanceRegistry.cs — スクリプトのインスタンスの登録簿（エンジン非依存の本体。状態と足し引き）
//
//  GameObject.GetScript<T>() / SEEDScript.Instances<T>() などの「スクリプトのインスタンスを型で引く」API の土台。
//  ランタイムは変えず、CLR 側だけで次の 3 つの表を持つ:
//    1. インスタンス → 記録（生成の番号・持ち主）          … 生成を知らされてから外れるまでの全部（持ち主が未定のものも）
//    2. 持ち主のアクタ → スクリプトの一覧（生成の番号の順） … 持ち主が決まったものだけ
//    3. 具体的な型 → インスタンスの一覧（生成の番号の順）   … 持ち主が決まったものだけ（Instances / FindInstance の元）
//  ほかに「持ち主が未定の記録の型 → 数」を持ち、アクタを指定して引くときにランタイムへ問い合わせる型の候補にする
//  （ScriptSlotProbe。OnStart の前のスクリプトも GetScript で引けるようにするため）。
//
//  寿命（エンジンの窓口 ScriptRegistry がこの順に呼ぶ）:
//    NoteCreated … ScriptBridge.CreateComponent（インスタンスができた。持ち主はまだ分からない）
//    Bind        … ScriptBridge.ResolveReferenceFields / OnStart（持ち主のアクタが分かった＝ OnStart の直前）
//    Remove      … ScriptBridge.OnDestroy の後・DestroyComponent（OnStart 前の破棄も DestroyComponent で必ず外れる）
//    Clear       … スクリプトの読み直し（CompileScripts / LoadPrecompiledScripts*）
//  インスタンスは強い参照で握る（弱参照は使わない）。破棄で必ず外し、読み直しで全消去するので、
//  旧アセンブリ（アンロードできる AssemblyLoadContext）の型・インスタンスを握り続けることはない。
//
//  CLR のメインスレッド専用（スクリプトの API と同じ）。排他は持たない。
// ============================================================

/// <summary>スクリプトのインスタンスの登録簿（エンジン非依存）。</summary>
/// <typeparam name="TInstance">インスタンスの型（エンジンでは IScriptComponent）。</typeparam>
internal sealed partial class ScriptInstanceRegistry<TInstance> where TInstance : class
{
    /// <summary>最初に振る生成の番号。</summary>
    private const long FirstSequence = 1;

    /// <summary>次に振る生成の番号（全消去しても戻さない＝番号はプロセスの中で一意）。</summary>
    private long _nextSequence = FirstSequence;

    /// <summary>
    /// インスタンス → 記録。スクリプトが Equals / GetHashCode を上書きしていても同一性で引けるよう、参照の等しさで比べる。
    /// </summary>
    private readonly Dictionary<TInstance, ScriptRegistryEntry<TInstance>> _entries =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>持ち主のアクタ → スクリプトの一覧（生成の番号の昇順＝スロットの順）。空になった一覧は消す。</summary>
    private readonly Dictionary<ScriptOwnerKey, List<ScriptRegistryEntry<TInstance>>> _byOwner = new();

    /// <summary>具体的な型 → 持ち主の決まったインスタンスの一覧（生成の番号の昇順）。空になった一覧は消す（型を握り続けない）。</summary>
    private readonly Dictionary<Type, List<ScriptRegistryEntry<TInstance>>> _byType = new();

    /// <summary>持ち主が未定の記録の具体的な型 → 数（アクタを指定して引くときの問い合わせの候補）。0 になった型は消す。</summary>
    private readonly Dictionary<Type, int> _unboundCountByType = new();

    /// <summary>生成を知らされ、まだ外れていないインスタンスの数（持ち主が未定のものも含む）。</summary>
    internal int Count => _entries.Count;

    /// <summary>持ち主の決まったインスタンスの数（Instances に載る数）。</summary>
    internal int BoundCount { get; private set; }

    /// <summary>診断用: スクリプトを持つアクタの数（外し漏れで空の一覧が残っていないかのテストに使う）。</summary>
    internal int OwnerCount => _byOwner.Count;

    /// <summary>診断用: 持ち主の決まったインスタンスの具体的な型の数（空の型の一覧が残っていないかのテストに使う）。</summary>
    internal int TypeCount => _byType.Count;

    /// <summary>診断用: 持ち主が未定の記録の具体的な型の数。</summary>
    internal int UnboundTypeCount => _unboundCountByType.Count;

    /// <summary>
    /// インスタンスができたことを記録し、生成の番号を振る（持ち主はまだ決めない）。
    /// 既に記録があれば何もしない。
    /// </summary>
    /// <param name="instance">できたインスタンス。</param>
    internal void NoteCreated(TInstance instance)
    {
        if (_entries.ContainsKey(instance)) return;
        var entry = new ScriptRegistryEntry<TInstance>(instance, _nextSequence++);
        _entries.Add(instance, entry);
        // 持ち主が決まるまでは「アクタを指定して引くときの問い合わせの候補」として型を数える
        AddUnboundType(instance.GetType());
    }

    /// <summary>
    /// インスタンスの持ち主のアクタを決める（以後、そのアクタから引け、全体の一覧にも載る）。
    ///
    /// 同じ持ち主で何度呼んでも 1 回と同じ。別の持ち主で呼ぶと、古いアクタから外して新しいアクタへ移す
    /// （型の一覧には 1 回だけ載ったまま）。生成を知らされていないインスタンスは、ここで生成の番号を振って記録する。
    /// </summary>
    /// <param name="instance">インスタンス。</param>
    /// <param name="owner">持ち主のアクタ。</param>
    /// <returns>表が変わったら true（既に同じ持ち主なら false）。</returns>
    internal bool Bind(TInstance instance, ScriptOwnerKey owner)
    {
        if (!_entries.TryGetValue(instance, out var entry))
        {
            // 生成を知らされていない（読み直しの全消去より前に作られたものなど）: 今の時点を生成の順として記録する
            NoteCreated(instance);
            entry = _entries[instance];
        }

        // 同じ持ち主なら何もしない（参照の解決と OnStart の両方から呼ばれる）
        if (entry.Owner == owner) return false;

        if (entry.Owner is { } previous)
        {
            // 持ち主が変わった: 古いアクタの一覧から外す（型の一覧には載ったまま）
            RemoveFromOwner(previous, entry);
        }
        else
        {
            // 初めて持ち主が決まった: 問い合わせの候補から外し、型の一覧（全体の一覧の元）へ載せる
            RemoveUnboundType(instance.GetType());
            AddToType(entry);
            BoundCount++;
        }

        entry.Owner = owner;
        AddToOwner(owner, entry);
        return true;
    }

    /// <summary>
    /// インスタンスを登録簿から外す（破棄。OnDestroy の後・DestroyComponent）。二重に呼んでも無害。
    /// </summary>
    /// <param name="instance">インスタンス。</param>
    /// <returns>外したら true（記録が無ければ false）。</returns>
    internal bool Remove(TInstance instance)
    {
        if (!_entries.Remove(instance, out var entry)) return false;

        if (entry.Owner is { } owner)
        {
            // 持ち主の決まったもの: アクタの一覧と型の一覧の両方から外す
            RemoveFromOwner(owner, entry);
            RemoveFromType(entry);
            BoundCount--;
        }
        else
        {
            // 持ち主が未定のまま破棄された（OnStart の前の破棄・編集タブのアクタなど）: 問い合わせの候補から外す
            RemoveUnboundType(instance.GetType());
        }
        return true;
    }

    /// <summary>全部を外す（スクリプトの読み直し）。生成の番号は戻さない。</summary>
    internal void Clear()
    {
        _entries.Clear();
        _byOwner.Clear();
        _byType.Clear();
        _unboundCountByType.Clear();
        BoundCount = 0;
    }

    // ── 表の足し引き（内部用）──────────────────────────────

    /// <summary>持ち主のアクタの一覧へ、生成の番号の順を保って足す。</summary>
    private void AddToOwner(ScriptOwnerKey owner, ScriptRegistryEntry<TInstance> entry)
    {
        if (!_byOwner.TryGetValue(owner, out var list))
        {
            list = new List<ScriptRegistryEntry<TInstance>>();
            _byOwner.Add(owner, list);
        }
        SequenceOrderedList.Insert(list, entry);
    }

    /// <summary>持ち主のアクタの一覧から外す（空になったら一覧ごと消す）。</summary>
    private void RemoveFromOwner(ScriptOwnerKey owner, ScriptRegistryEntry<TInstance> entry)
    {
        if (!_byOwner.TryGetValue(owner, out var list)) return;
        SequenceOrderedList.Remove(list, entry);
        if (list.Count == 0) _byOwner.Remove(owner);
    }

    /// <summary>具体的な型の一覧へ、生成の番号の順を保って足す。</summary>
    private void AddToType(ScriptRegistryEntry<TInstance> entry)
    {
        var type = entry.Instance.GetType();
        if (!_byType.TryGetValue(type, out var list))
        {
            list = new List<ScriptRegistryEntry<TInstance>>();
            _byType.Add(type, list);
        }
        SequenceOrderedList.Insert(list, entry);
    }

    /// <summary>具体的な型の一覧から外す（空になったら型ごと消す＝旧アセンブリの型を握り続けない）。</summary>
    private void RemoveFromType(ScriptRegistryEntry<TInstance> entry)
    {
        var type = entry.Instance.GetType();
        if (!_byType.TryGetValue(type, out var list)) return;
        SequenceOrderedList.Remove(list, entry);
        if (list.Count == 0) _byType.Remove(type);
    }

    /// <summary>持ち主が未定の記録の型を 1 つ数える。</summary>
    private void AddUnboundType(Type type)
    {
        _unboundCountByType.TryGetValue(type, out int count);
        _unboundCountByType[type] = count + 1;
    }

    /// <summary>持ち主が未定の記録の型を 1 つ減らす（0 になったら型ごと消す）。</summary>
    private void RemoveUnboundType(Type type)
    {
        if (!_unboundCountByType.TryGetValue(type, out int count)) return;
        if (count <= 1) _unboundCountByType.Remove(type);
        else _unboundCountByType[type] = count - 1;
    }
}
