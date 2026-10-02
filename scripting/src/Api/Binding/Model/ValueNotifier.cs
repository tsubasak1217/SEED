using System;
using System.Collections.Generic;

namespace SEED.Binding;

// ============================================================
//  ValueNotifier.cs — 値の知らせ方（Observable・Computed・Bind.Deferred の共通。内部用）
//
//  【即時】値が変わったら、その場で全部の購読へ今の値を知らせる（フレームを待たない）。
//  【再入】購読の中で値が変わったら（再入）、入れ子に呼ばず「今の周を全員へ配り終えてから、最新の値でもう 1 周」配る。
//    - 周の中で何度変わっても次の周は最新の値 1 回だけ（まとめる）。最後に配った値へ戻っただけなら次の周は無い（等しければ知らせない）。
//    - 次の周は BindingLimits.LastRound 周目まで。それを超えて変わったら値は入ったまま知らせず、警告を 1 回だけ出す。
//    - 入れ子にしないので、どの購読も「後から古い値で上書きされる」ことが無い（全員が最後に受け取るのは同じ最新の値）。
//  正典は docs/ui_binding.md §5。
// ============================================================

/// <summary>値の知らせ方（即時・再入は周をまとめて配り直す・深さの上限）。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class ValueNotifier<T>
{
    /// <summary>購読者。</summary>
    private readonly SubscriberList<T> _subscribers;

    /// <summary>配るときに今の値を読む（持ち主の値）。</summary>
    private readonly Func<T> _read;

    /// <summary>値の比べ方（最後に配った値へ戻っただけかの判定）。</summary>
    private readonly IEqualityComparer<T> _comparer;

    /// <summary>警告に出す持ち主の種類（"Observable" など）。</summary>
    private readonly string _kind;

    /// <summary>今配っている周（BindingLimits.IdleRound = 配っていない）。</summary>
    private int _round = BindingLimits.IdleRound;

    /// <summary>今の周の途中で値が変わったか（配り終えたらもう 1 周）。</summary>
    private bool _changedDuringRound;

    /// <summary>今の周の途中で「同じ値でも知らせる」（Observable.Notify）が来たか。</summary>
    private bool _forcedDuringRound;

    /// <summary>深さの上限の警告を出したか（同じ持ち主では 1 回だけ）。</summary>
    private bool _warnedDepth;

    /// <summary>知らせ方を作る。</summary>
    /// <param name="read">配るときに今の値を読む処理。</param>
    /// <param name="comparer">値の比べ方。</param>
    /// <param name="kind">警告に出す持ち主の種類。</param>
    /// <param name="onEmpty">最後の購読が外れたときに呼ぶ処理（無ければ null）。</param>
    internal ValueNotifier(Func<T> read, IEqualityComparer<T> comparer, string kind, Action? onEmpty = null)
    {
        _read = read;
        _comparer = comparer;
        _kind = kind;
        _subscribers = new SubscriberList<T>(onEmpty);
    }

    /// <summary>今の購読の数。</summary>
    internal int SubscriberCount => _subscribers.Count;

    /// <summary>今配っている途中か。</summary>
    internal bool IsDispatching => _round != BindingLimits.IdleRound;

    /// <summary>購読を足す。</summary>
    /// <param name="handler">新しい値を受け取る処理。</param>
    /// <returns>解除の口。</returns>
    internal IDisposable Subscribe(Action<T> handler) => _subscribers.Add(handler);

    /// <summary>
    /// 値が変わったことを知らせる（配っていなければその場で配る。配っている途中なら、配り終えた後の周に回す）。
    /// </summary>
    /// <param name="force">最後に配った値と同じでも配るか（Observable.Notify。中身を書き換えた参照型など）。</param>
    internal void Publish(bool force = false)
    {
        // ── 配っている途中（購読の中で値が変わった＝再入）: 今の周を配り終えてから配り直す ──
        if (_round != BindingLimits.IdleRound)
        {
            _changedDuringRound = true;
            _forcedDuringRound |= force;
            return;
        }

        // ── 配る: 1 周目から始め、周の途中で変わったら上限まで周を重ねる ──
        _round = BindingLimits.FirstRound;
        try
        {
            while (true)
            {
                _changedDuringRound = false;
                _forcedDuringRound = false;
                T delivered = _read();
                _subscribers.Invoke(delivered);

                // 周の途中で何も変わらなかった: 終わり
                if (!_changedDuringRound) break;

                // 最後に配った値へ戻っただけ（強制でもない）: 知らせ直す必要は無い
                if (!_forcedDuringRound && _comparer.Equals(_read(), delivered)) break;

                // 上限の周まで配った: 値は入ったまま知らせず、警告して終える
                if (_round >= BindingLimits.LastRound)
                {
                    WarnDepthOnce();
                    break;
                }
                _round++;
            }
        }
        finally
        {
            // 例外の経路でも「配っていない」へ戻す（購読の例外は SubscriberList が握るので通常は来ない）
            _round = BindingLimits.IdleRound;
            _changedDuringRound = false;
            _forcedDuringRound = false;
        }
    }

    /// <summary>深さの上限を超えた警告を 1 回だけ出す。</summary>
    private void WarnDepthOnce()
    {
        if (_warnedDepth) return;
        _warnedDepth = true;
        BindingLog.Warn(
            $"{_kind} の購読の中で値を変える再入が上限（{BindingLimits.MaxReentrantDepth} 段）を超えました。" +
            $"最後の値 {_read()} は入っていますが、購読へは知らせていません（購読の中で同じ値を変え続けていないか確かめてください）。");
    }
}
