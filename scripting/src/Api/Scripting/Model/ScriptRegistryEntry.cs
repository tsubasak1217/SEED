using System.Collections.Generic;

namespace SEED.Scripting;

// ============================================================
//  ScriptRegistryEntry.cs — スクリプトの登録簿の 1 件（インスタンス・生成の番号・持ち主）
//
//  生成の番号（Sequence）は登録簿が生成を知らされた順に振る単調増加の数で、
//    - 同じアクタの中の並び（スロット順）
//    - 全体の一覧（Instances）の並び（生成順）
//  の両方の基準になる。ランタイムはスロットを足すその場で CLR のインスタンスを作る（シーンの読み込み・Instantiate・
//  AddScript・ホットリロードのどれもスロットの順に作る）ので、生成の番号の順はアクタの中のスロットの順と一致する。
// ============================================================

/// <summary>スクリプトの登録簿の 1 件。</summary>
/// <typeparam name="TInstance">インスタンスの型（エンジンでは IScriptComponent、テストでは偽の型）。</typeparam>
internal sealed class ScriptRegistryEntry<TInstance> where TInstance : class
{
    /// <summary>生成の番号の昇順に並べる比較（同じアクタの一覧・型の一覧・まとめた結果の並べ替えに使う）。</summary>
    internal static readonly IComparer<ScriptRegistryEntry<TInstance>> BySequence =
        Comparer<ScriptRegistryEntry<TInstance>>.Create((a, b) => a.Sequence.CompareTo(b.Sequence));

    /// <summary>記録を作る（持ち主はまだ決まっていない）。</summary>
    /// <param name="instance">スクリプトのインスタンス。</param>
    /// <param name="sequence">生成の番号（登録簿の中で一意・単調増加）。</param>
    internal ScriptRegistryEntry(TInstance instance, long sequence)
    {
        Instance = instance;
        Sequence = sequence;
    }

    /// <summary>スクリプトのインスタンス（登録簿が外すまで強い参照で握る。弱参照は使わない）。</summary>
    internal TInstance Instance { get; }

    /// <summary>生成の番号（小さいほど先に作られた）。</summary>
    internal long Sequence { get; }

    /// <summary>
    /// 持ち主のアクタ（null = まだ決まっていない）。
    /// エンジンでは、そのスクリプトの OnStart の直前（参照フィールドの解決・OnStart の呼び出し）で決まる。
    /// </summary>
    internal ScriptOwnerKey? Owner { get; set; }

    /// <summary>持ち主が決まっているか（＝ OnStart を迎えた・迎えている最中のスクリプトか）。</summary>
    internal bool IsBound => Owner.HasValue;
}
