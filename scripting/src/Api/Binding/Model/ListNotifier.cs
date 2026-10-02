using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  ListNotifier.cs — 一覧の変化の知らせ方（ObservableList の内部用）
//
//  【即時】変化はその場で全部の購読へ知らせる。
//  【順序】購読の中で一覧を変えたら（再入）、入れ子に呼ばず待ち行列に積み、今の変化を全員へ配り終えてから起きた順に配る。
//    値と違って変化はまとめられない（1 件ずつ当てる写しが壊れる）ので、順を守ることを優先する。
//    → どの購読も、変化を起きた順に受け取る。
//  【深さ】最初の変化を 1 段目として、その購読の中で起きた変化を 2 段目…と数え、BindingLimits.LastRound 段を超えた変化は
//    一覧には入ったまま知らせず、警告を 1 回だけ出す（購読が変化のたびに一覧を変え続ける無限の連鎖の保険）。
//  正典は docs/ui_binding.md §5。
// ============================================================

/// <summary>一覧の変化の知らせ方（即時・再入は順を守って後で配る・深さの上限）。</summary>
/// <typeparam name="T">項目の型。</typeparam>
internal sealed class ListNotifier<T>
{
    /// <summary>購読者。</summary>
    private readonly SubscriberList<ListChange<T>> _subscribers = new();

    /// <summary>配る順を待っている変化（変化と、その段）。</summary>
    private readonly Queue<(ListChange<T> Change, int Depth)> _pending = new();

    /// <summary>今配っている変化の段（BindingLimits.IdleRound = 配っていない）。</summary>
    private int _depth = BindingLimits.IdleRound;

    /// <summary>深さの上限の警告を出したか（同じ一覧では 1 回だけ）。</summary>
    private bool _warnedDepth;

    /// <summary>今の購読の数。</summary>
    internal int SubscriberCount => _subscribers.Count;

    /// <summary>購読を足す。</summary>
    /// <param name="handler">変化を受け取る処理。</param>
    /// <returns>解除の口。</returns>
    internal IDisposable Subscribe(Action<ListChange<T>> handler) => _subscribers.Add(handler);

    /// <summary>変化を知らせる（配っていなければその場で、配っている途中なら今の変化の後に順に配る）。</summary>
    /// <param name="change">変化。</param>
    internal void Publish(ListChange<T> change)
    {
        // ── 配っている途中（購読の中で一覧が変わった＝再入）: 段を 1 つ深くして待ち行列へ ──
        if (_depth != BindingLimits.IdleRound)
        {
            int depth = _depth + 1;
            if (depth > BindingLimits.LastRound)
            {
                // 上限を超えた変化は知らせない（一覧には入っている）
                WarnDepthOnce(change);
                return;
            }
            _pending.Enqueue((change, depth));
            return;
        }

        // ── 配る: この変化から始め、配っている間に積まれた変化を起きた順に配る ──
        _pending.Enqueue((change, BindingLimits.FirstRound));
        try
        {
            while (_pending.TryDequeue(out var next))
            {
                _depth = next.Depth;
                _subscribers.Invoke(next.Change);
            }
        }
        finally
        {
            // 例外の経路でも「配っていない」へ戻し、配れなかった変化を捨てる（購読の例外は SubscriberList が握るので通常は来ない）
            _depth = BindingLimits.IdleRound;
            _pending.Clear();
        }
    }

    /// <summary>深さの上限を超えた警告を 1 回だけ出す。</summary>
    /// <param name="change">知らせなかった変化。</param>
    private void WarnDepthOnce(ListChange<T> change)
    {
        if (_warnedDepth) return;
        _warnedDepth = true;
        BindingLog.Warn(
            $"ObservableList の購読の中で一覧を変える再入が上限（{BindingLimits.MaxReentrantDepth} 段）を超えました。" +
            $"変化 {change} は一覧に入っていますが、購読へは知らせていません（購読の中で一覧を変え続けていないか確かめてください）。");
    }
}
