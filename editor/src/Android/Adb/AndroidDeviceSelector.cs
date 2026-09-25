// ============================================================
//  AndroidDeviceSelector.cs — 実行先の端末を 1 台に決める（純粋な処理）
//
//  【決め方（Select。従来の build_and_run.ps1 と同じ）】
//    - シリアルの指定があれば、その端末（無い・使えない状態なら理由付きのエラー）
//    - 指定が無ければ、使える端末がちょうど 1 台のときだけそれ。0 台・2 台以上はエラー
//      （2 台以上で勝手に選ぶと、私物の実機へ入れてしまう事故になるため。前回の実行先は候補として示すだけ）
//
//  【実行の決め方（DecideForRun。段階C-3。AndroidDeviceTarget の 4 つの決め方ごと）】
//    Single / Exact  … Select と同じ
//    ExactOrEmulator … 指定の端末が使えればそれ。起動の途中のエミュレータなら完了を待つ。使えない状態の実機はエラー
//                      （つながっているので、許可すれば使える）。**見えなければ**「実機 X が見えないためエミュレータで
//                      実行します」と警告して、下のエミュレータの規則へ
//    Auto            … 使える実機（前回使ったものを優先。前回のものが無く 2 台以上なら選べないのでエラー）→
//                      下のエミュレータの規則（使えない状態の実機は警告して飛ばす）
//    エミュレータの規則 … 使えるエミュレータ（前回使ったものを優先、無ければ一覧の先頭）→ 起動の途中のエミュレータ
//                      （offline・connecting・authorizing。完了を待つ）→ どれも無ければ AVD を起動
//  実機を 2 台以上から勝手に選ばない（私物の端末へ入れない）のは Select と同じ考え方。エミュレータはこの PC の中の
//  ものなので先頭を選んでよい。
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
    // ── 実行の決め方（DecideForRun）の文言 ────────────────────────

    /// <summary>選んだ実機が見えないときの警告の書式（{0}=シリアル）。</summary>
    public const string MissingPhysicalFallbackFormat = "実機 {0} が見えないためエミュレータで実行します。";

    /// <summary>選んだエミュレータが見えないときの警告の書式（{0}=シリアル）。</summary>
    public const string MissingEmulatorFallbackFormat = "エミュレータ {0} が見えないため、別のエミュレータで実行します。";

    /// <summary>自動で実機を選んだときの説明の書式（{0}=端末の表示名、{1}=理由）。</summary>
    private const string AutoPhysicalNoteFormat = "実機 {0} で実行します（{1}）。";

    /// <summary>前回使った実機を選んだ理由。</summary>
    private const string LastUsedReason = "前回使った実機";

    /// <summary>ただ 1 台の実機を選んだ理由。</summary>
    private const string OnlyPhysicalReason = "つながっている実機";

    /// <summary>実機が 2 台以上で選べないときのエラーの書式（{0}=台数、{1}=一覧）。</summary>
    private const string AmbiguousPhysicalFormat =
        "実機が {0} 台つながっていて、どれで実行するか決められません。実行先セレクタ（SeedAndroid は --serial）で端末を選んでください:\n{1}";

    /// <summary>使えない状態の実機を飛ばしたときの警告の書式（{0}=理由）。</summary>
    private const string SkippedPhysicalFormat = "使えない状態の実機は使いません: {0}";

    /// <summary>使えない状態のエミュレータを飛ばしたときの警告の書式（{0}=理由）。</summary>
    private const string SkippedEmulatorFormat = "使えない状態のエミュレータは使いません: {0}";

    /// <summary>実機が無いのでエミュレータへ回る説明。</summary>
    private const string NoPhysicalNote = "つながっている実機が無いため、エミュレータで実行します。";

    /// <summary>起動中のエミュレータを使う説明の書式（{0}=シリアル）。</summary>
    private const string RunningEmulatorNoteFormat = "起動中のエミュレータ {0} で実行します。";

    /// <summary>起動の途中のエミュレータを待つ説明の書式（{0}=シリアル、{1}=adb の状態）。</summary>
    private const string BootingEmulatorNoteFormat = "起動の途中のエミュレータ {0}（{1}）の起動の完了を待ちます。";

    /// <summary>エミュレータを起動する説明。</summary>
    private const string LaunchEmulatorNote = "使える端末が無いため、AVD からエミュレータを起動します。";

    /// <summary>
    /// 実行のために端末をどうするかを決める（エミュレータの起動・待ち合わせは呼び出し側。Emulator/AndroidDeviceProvisioner）。
    /// </summary>
    /// <param name="devices">adb devices -l の一覧。</param>
    /// <param name="target">決め方。</param>
    /// <param name="lastUsedSerial">前回の実行先（プロジェクトの実行状態の last_target。優先する・エラーの説明に添える）。</param>
    /// <returns>判断。</returns>
    public static AndroidDeviceDecision DecideForRun(
        IReadOnlyList<AdbDevice> devices, AndroidDeviceTarget target, string? lastUsedSerial)
    {
        switch (target.Mode)
        {
            case AndroidDeviceTargetMode.Single:
                return FromSelection(Select(devices, null, lastUsedSerial));
            case AndroidDeviceTargetMode.Exact:
                return FromSelection(Select(devices, target.Serial));
            case AndroidDeviceTargetMode.ExactOrEmulator:
                return DecideExactOrEmulator(devices, target.Serial ?? string.Empty, lastUsedSerial);
            default:
                return DecideAuto(devices, lastUsedSerial);
        }
    }

    /// <summary>指定の端末。見えなければエミュレータへ。</summary>
    private static AndroidDeviceDecision DecideExactOrEmulator(IReadOnlyList<AdbDevice> devices, string serial, string? lastUsedSerial)
    {
        var found = devices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.Ordinal));
        if (found is not null)
        {
            if (found.IsReady) return new AndroidDeviceDecision { Kind = AndroidDeviceDecisionKind.Use, Device = found };
            if (found.Kind == AdbDeviceKind.Emulator && IsBooting(found))
            {
                return new AndroidDeviceDecision
                {
                    Kind = AndroidDeviceDecisionKind.WaitForEmulator,
                    Device = found,
                    Notes = new[] { string.Format(BootingEmulatorNoteFormat, found.Serial, found.StateText) },
                };
            }
            // つながっている（見えている）が使えない実機は、許可・挿し直しで使えるようになるので切り替えずに理由を返す
            return Failure(DescribeNotReady(found));
        }

        var warning = string.Format(
            AdbDeviceListParser.IsEmulatorSerial(serial) ? MissingEmulatorFallbackFormat : MissingPhysicalFallbackFormat, serial);
        return DecideEmulator(devices, lastUsedSerial, new List<string>(), new List<string> { warning });
    }

    /// <summary>自動: 実機 → エミュレータ。</summary>
    private static AndroidDeviceDecision DecideAuto(IReadOnlyList<AdbDevice> devices, string? lastUsedSerial)
    {
        var readyPhysical = devices.Where(d => d.Kind == AdbDeviceKind.Physical && d.IsReady).ToList();
        if (readyPhysical.Count > 0)
        {
            var lastUsed = readyPhysical.FirstOrDefault(d => string.Equals(d.Serial, lastUsedSerial, StringComparison.Ordinal));
            if (lastUsed is not null) return UsePhysical(lastUsed, LastUsedReason);
            if (readyPhysical.Count == 1) return UsePhysical(readyPhysical[0], OnlyPhysicalReason);
            return Failure(string.Format(AmbiguousPhysicalFormat, readyPhysical.Count,
                string.Join("\n", readyPhysical.Select(d => $"  {d.Serial}  {d.Model ?? "-"}"))));
        }

        var warnings = devices
            .Where(d => d.Kind == AdbDeviceKind.Physical && !d.IsReady)
            .Select(d => string.Format(SkippedPhysicalFormat, DescribeNotReady(d)))
            .ToList();
        return DecideEmulator(devices, lastUsedSerial, new List<string> { NoPhysicalNote }, warnings);
    }

    /// <summary>
    /// エミュレータの規則: 使えるもの（前回優先）→ 起動の途中のもの（前回優先）→ AVD を起動。
    /// </summary>
    /// <param name="devices">端末の一覧。</param>
    /// <param name="lastUsedSerial">前回の実行先。</param>
    /// <param name="notes">ここまでの説明（ここで足す）。</param>
    /// <param name="warnings">ここまでの警告（ここで足す）。</param>
    /// <returns>判断。</returns>
    private static AndroidDeviceDecision DecideEmulator(
        IReadOnlyList<AdbDevice> devices, string? lastUsedSerial, List<string> notes, List<string> warnings)
    {
        var emulators = devices.Where(d => d.Kind == AdbDeviceKind.Emulator).ToList();

        var ready = emulators.Where(d => d.IsReady).ToList();
        if (ready.Count > 0)
        {
            var chosen = PreferLastUsed(ready, lastUsedSerial);
            notes.Add(string.Format(RunningEmulatorNoteFormat, chosen.Serial));
            return new AndroidDeviceDecision { Kind = AndroidDeviceDecisionKind.Use, Device = chosen, Notes = notes, Warnings = warnings };
        }

        var booting = emulators.Where(IsBooting).ToList();
        if (booting.Count > 0)
        {
            var chosen = PreferLastUsed(booting, lastUsedSerial);
            notes.Add(string.Format(BootingEmulatorNoteFormat, chosen.Serial, chosen.StateText));
            return new AndroidDeviceDecision { Kind = AndroidDeviceDecisionKind.WaitForEmulator, Device = chosen, Notes = notes, Warnings = warnings };
        }

        // 未許可などのエミュレータは待っても使えるようにならないので、警告して新しく起動する
        warnings.AddRange(emulators.Select(d => string.Format(SkippedEmulatorFormat, DescribeNotReady(d))));
        notes.Add(LaunchEmulatorNote);
        return new AndroidDeviceDecision { Kind = AndroidDeviceDecisionKind.LaunchEmulator, Notes = notes, Warnings = warnings };
    }

    /// <summary>起動の途中のエミュレータの状態か（待てば使えるようになる状態）。</summary>
    /// <param name="device">端末。</param>
    /// <returns>起動の途中なら true。</returns>
    public static bool IsBooting(AdbDevice device) =>
        device.State is AdbDeviceState.Offline or AdbDeviceState.Connecting or AdbDeviceState.Authorizing;

    /// <summary>前回の実行先があればそれ、無ければ先頭。</summary>
    private static AdbDevice PreferLastUsed(IReadOnlyList<AdbDevice> candidates, string? lastUsedSerial) =>
        candidates.FirstOrDefault(d => string.Equals(d.Serial, lastUsedSerial, StringComparison.Ordinal)) ?? candidates[0];

    /// <summary>実機を使う判断（理由の説明付き）。</summary>
    private static AndroidDeviceDecision UsePhysical(AdbDevice device, string reason) => new()
    {
        Kind = AndroidDeviceDecisionKind.Use,
        Device = device,
        Notes = new[] { string.Format(AutoPhysicalNoteFormat, device.DisplayName, reason) },
    };

    /// <summary>従来の選び方の結果を判断へ直す。</summary>
    private static AndroidDeviceDecision FromSelection(AndroidDeviceSelection selection) =>
        selection.Device is null
            ? Failure(selection.Error ?? "端末を決められません。")
            : new AndroidDeviceDecision { Kind = AndroidDeviceDecisionKind.Use, Device = selection.Device };

    /// <summary>決められない判断。</summary>
    private static AndroidDeviceDecision Failure(string error) => new() { Kind = AndroidDeviceDecisionKind.Fail, Error = error };

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
