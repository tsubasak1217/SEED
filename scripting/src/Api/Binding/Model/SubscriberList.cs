using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  SubscriberList.cs — 購読者の並び（観測値・一覧の知らせの配り先。内部用）
//
//  【約束】
//    - 登録の順に呼ぶ。
//    - 配っている途中に足した購読は、その知らせでは呼ばない（次の知らせから）。
//    - 配っている途中に外した購読は、まだ呼んでいなくても呼ばない（外れた結び付けが死んだ部品へ書かないように。
//      SEED.Events は写しのまま呼ぶが、ここは結び付けの安全を優先する）。
//    - 1 つの購読の例外はエラーログへ出して握り、残りの購読は続ける（SEED.Events と同じ）。
//    - 最後の購読が外れたら OnEmpty を呼ぶ（Computed・Bind.Deferred が依存元の購読を外す合図）。
//  スレッド: スクリプトのフェーズ（メインスレッド）だけから使う前提で、鍵は掛けない。
// ============================================================

/// <summary>購読者の並び（登録の順に呼ぶ・途中の付け外しに強い・例外を隔離）。</summary>
/// <typeparam name="TArg">知らせの引数の型。</typeparam>
internal sealed class SubscriberList<TArg>
{
    /// <summary>購読 1 件（解除の口を兼ねる）。</summary>
    private sealed class Entry : IDisposable, IBindingHandle
    {
        /// <summary>呼ぶ処理（解除したら null）。</summary>
        internal Action<TArg>? Handler;

        /// <summary>属する並び（解除したら null）。</summary>
        internal SubscriberList<TArg>? Owner;

        /// <summary>購読を作る。</summary>
        internal Entry(Action<TArg> handler, SubscriberList<TArg> owner)
        {
            Handler = handler;
            Owner = owner;
        }

        /// <inheritdoc />
        public bool IsDisposed => Handler is null;

        /// <summary>購読を外す（二重の解除は無害）。</summary>
        public void Dispose() => Owner?.Remove(this);
    }

    /// <summary>解除済みの購読（null 以外を返す約束のための代わり。何もしない）。</summary>
    private sealed class DeadSubscription : IDisposable, IBindingHandle
    {
        /// <summary>共有の 1 つ。</summary>
        internal static readonly DeadSubscription Instance = new();

        /// <inheritdoc />
        public bool IsDisposed => true;

        /// <summary>何もしない。</summary>
        public void Dispose() { }
    }

    /// <summary>購読（登録の順。途中で外した分は配り終えるまで null の印のまま残す）。</summary>
    private readonly List<Entry> _entries = new();

    /// <summary>最後の購読が外れたときに呼ぶ処理（無ければ null）。</summary>
    private readonly Action? _onEmpty;

    /// <summary>配っている途中の入れ子の数（0 = 配っていない）。</summary>
    private int _dispatching;

    /// <summary>配っている途中に外した購読があり、配り終えたら並びを詰める必要があるか。</summary>
    private bool _needsCompact;

    /// <summary>並びを作る。</summary>
    /// <param name="onEmpty">最後の購読が外れたときに呼ぶ処理（無ければ null）。</param>
    internal SubscriberList(Action? onEmpty = null)
    {
        _onEmpty = onEmpty;
    }

    /// <summary>今の購読の数（外したものは数えない）。</summary>
    internal int Count { get; private set; }

    /// <summary>購読を足す（handler が null なら何もせず解除済みの口を返す）。</summary>
    /// <param name="handler">知らせを受け取る処理。</param>
    /// <returns>解除の口（null は返さない）。</returns>
    internal IDisposable Add(Action<TArg>? handler)
    {
        if (handler is null)
        {
            BindingLog.Warn("購読の処理が null です。登録しません。");
            return DeadSubscription.Instance;
        }
        var entry = new Entry(handler, this);
        _entries.Add(entry);
        Count++;
        return entry;
    }

    /// <summary>購読を外す（配っている途中なら印だけ付け、配り終えてから詰める）。</summary>
    /// <param name="entry">外す購読。</param>
    private void Remove(Entry entry)
    {
        if (entry.Handler is null) return;

        // ── 印を付ける（配っている途中でも、この後はもう呼ばれない）──
        entry.Handler = null;
        entry.Owner = null;
        Count--;

        // ── 並びから外す（配っている途中は添字を動かさないよう後で詰める）──
        if (_dispatching > 0) _needsCompact = true;
        else _entries.Remove(entry);

        // ── 最後の 1 つが外れた: 持ち主へ知らせる（依存元の購読を外すなど）──
        if (Count == 0) _onEmpty?.Invoke();
    }

    /// <summary>全部の購読へ知らせる（登録の順。途中で足した分は呼ばず、途中で外した分も呼ばない）。</summary>
    /// <param name="arg">知らせの引数。</param>
    internal void Invoke(TArg arg)
    {
        // 配り始めた時点の数だけ呼ぶ（途中で足した購読は末尾に付くので、ここで切れば呼ばない）
        int count = _entries.Count;
        _dispatching++;
        try
        {
            for (int i = 0; i < count; i++)
            {
                // 途中で外した購読は Handler が null（呼ばない）
                var handler = _entries[i].Handler;
                if (handler is null) continue;
                try
                {
                    handler(arg);
                }
                catch (Exception ex)
                {
                    // 1 つの購読の例外で他の購読を巻き添えにしない（隔離してログだけ）
                    BindingLog.Error($"購読の処理で例外が起きました（残りの購読は続けます）: {ex}");
                }
            }
        }
        finally
        {
            // 例外の経路でも入れ子の数を戻し、外れた購読の印を詰める
            _dispatching--;
            if (_dispatching == 0 && _needsCompact)
            {
                _entries.RemoveAll(entry => entry.Handler is null);
                _needsCompact = false;
            }
        }
    }
}
