// ============================================================
//  AndroidDeviceSelector.cs — 実行先の端末を 1 台に決める（純粋な処理）
//
//  【決め方（従来の build_and_run.ps1 と同じ）】
//    - シリアルの指定があれば、その端末（無い・使えない状態なら理由付きのエラー）
//    - 指定が無ければ、使える端末がちょうど 1 台のときだけそれ。0 台・2 台以上はエラー
//      （2 台以上で勝手に選ぶと、私物の実機へ入れてしまう事故になるため。前回の実行先は候補として示すだけ）
//
//  WPF に依存しない（単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Adb;

/// <summary>端末選びの結果。</summary>
/// <param name="Device">選んだ端末（失敗時は null）。</param>
/// <param name="Error">選べなかった理由（成功時は null）。</param>
public sealed record AndroidDeviceSelection(AdbDevice? Device, string? Error);

/// <summary>実行先の端末を 1 台に決める。</summary>
public static class AndroidDeviceSelector
{
    /// <summary>
    /// 端末を選ぶ。
    /// </summary>
    /// <param name="devices">adb devices -l の一覧。</param>
    /// <param name="requestedSerial">指定されたシリアル（null / 空なら自動）。</param>
    /// <param name="lastUsedSerial">前回の実行先（2 台以上のときにエラーの説明へ添えるだけ）。</param>
    /// <returns>選んだ端末か、選べなかった理由。</returns>
    public static AndroidDeviceSelection Select(
        IReadOnlyList<AdbDevice> devices, string? requestedSerial, string? lastUsedSerial = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedSerial))
        {
            var serial = requestedSerial.Trim();
            var found = devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));
            if (found is null)
            {
                return Fail($"指定した端末 {serial} が adb につながっていません。{DescribeList(devices)}");
            }
            return found.IsReady ? new AndroidDeviceSelection(found, null) : Fail(DescribeNotReady(found));
        }

        var ready = devices.Where(d => d.IsReady).ToList();
        if (ready.Count == 1) return new AndroidDeviceSelection(ready[0], null);
        if (ready.Count == 0)
        {
            var notReady = devices.Where(d => !d.IsReady).Select(DescribeNotReady).ToList();
            return Fail(notReady.Count == 0
                ? "adb で端末が見つかりません（実機の USB デバッグ、またはエミュレータの起動を確認してください）。"
                : "使える端末がありません。" + string.Join(" ", notReady));
        }

        var hint = lastUsedSerial is not null && ready.Any(d => d.Serial == lastUsedSerial)
            ? $"（前回の実行先は {lastUsedSerial}）"
            : string.Empty;
        return Fail($"端末が {ready.Count} 台つながっています。シリアルで対象を指定してください{hint}:\n" +
                    string.Join("\n", ready.Select(d => $"  {d.Serial}  {d.KindLabel}  {d.Model ?? "-"}")));
    }

    /// <summary>使えない状態の端末の説明（状態ごとの対処つき）。</summary>
    /// <param name="device">端末。</param>
    /// <returns>説明。</returns>
    public static string DescribeNotReady(AdbDevice device) => device.State switch
    {
        AdbDeviceState.Unauthorized =>
            $"端末 {device.Serial} は USB デバッグが許可されていません（端末の画面に出ている「USB デバッグを許可しますか？」を許可してください）。",
        AdbDeviceState.Offline =>
            $"端末 {device.Serial} が応答しません（offline。エミュレータなら起動の完了を待ち、実機なら USB を挿し直してください）。",
        AdbDeviceState.NoPermissions =>
            $"端末 {device.Serial} に触る権限がありません（{device.StateText}）。",
        AdbDeviceState.Authorizing or AdbDeviceState.Connecting =>
            $"端末 {device.Serial} はまだ接続の途中です（{device.StateText}）。少し待ってからやり直してください。",
        _ => $"端末 {device.Serial} はアプリを入れられる状態ではありません（{device.StateText}）。",
    };

    /// <summary>つながっている端末の一覧の説明（エラー文の後ろに添える）。</summary>
    /// <param name="devices">端末の一覧。</param>
    /// <returns>説明。</returns>
    private static string DescribeList(IReadOnlyList<AdbDevice> devices) =>
        devices.Count == 0
            ? "つながっている端末はありません。"
            : "つながっている端末: " + string.Join(", ", devices.Select(d => $"{d.Serial}（{d.StateText}）"));

    /// <summary>失敗の結果を作る。</summary>
    /// <param name="error">理由。</param>
    /// <returns>結果。</returns>
    private static AndroidDeviceSelection Fail(string error) => new(null, error);
}
