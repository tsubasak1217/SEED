// ============================================================
//  WaitClock.cs — 待ち合わせの時計（本番は実時間。単体テストは Delay で進む仮の時計に差し替える）
//
//  エミュレータの起動待ち（AndroidDeviceProvisioner）の「経過時間」と「次の確認までの待ち」を 1 か所にまとめる。
//  単体テストは派生クラスで DelayAsync を「時計を進めるだけ」にして、300 秒の時間切れも一瞬で確かめる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Android.Emulator;

/// <summary>待ち合わせの時計（作った時点から測る）。</summary>
public class WaitClock
{
    /// <summary>実時間の計測。</summary>
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    /// <summary>作ってからの経過時間。</summary>
    public virtual TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>
    /// 待つ（中断の合図で OperationCanceledException）。
    /// </summary>
    /// <param name="delay">待つ長さ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>完了。</returns>
    public virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
