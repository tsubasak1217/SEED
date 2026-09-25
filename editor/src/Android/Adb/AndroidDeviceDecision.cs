// ============================================================
//  AndroidDeviceDecision.cs — 実行先の端末をどうするかの判断（AndroidDeviceSelector.DecideForRun の結果）
//
//  【判断の種類】
//    Use             … この端末で実行する（エミュレータなら、起動の完了だけは確かめてから使う）
//    WaitForEmulator … 起動の途中のエミュレータ（adb の状態が offline 等）の起動の完了を待ってから使う
//    LaunchEmulator  … 使える端末が無いので AVD からエミュレータを起動して待つ
//    Fail            … 決められない（理由付き。例: 実機が 2 台以上で選べない・選んだ実機が未許可）
//  説明（Notes）と警告（Warnings）は、どれを・なぜ選んだかを Output パネル／コンソールへ 1 行ずつ出すためのもの。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Android.Adb;

/// <summary>端末の判断の種類。</summary>
public enum AndroidDeviceDecisionKind
{
    /// <summary>この端末で実行する。</summary>
    Use,

    /// <summary>起動の途中のエミュレータの完了を待ってから使う。</summary>
    WaitForEmulator,

    /// <summary>AVD からエミュレータを起動して待つ。</summary>
    LaunchEmulator,

    /// <summary>決められない（理由は <see cref="AndroidDeviceDecision.Error"/>）。</summary>
    Fail,
}

/// <summary>実行先の端末の判断。</summary>
public sealed record AndroidDeviceDecision
{
    /// <summary>判断の種類。</summary>
    public required AndroidDeviceDecisionKind Kind { get; init; }

    /// <summary>使う・待つ端末（Use / WaitForEmulator のときだけ）。</summary>
    public AdbDevice? Device { get; init; }

    /// <summary>決められない理由（Fail のときだけ）。</summary>
    public string? Error { get; init; }

    /// <summary>どれを・なぜ選んだかの説明（出す順）。</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>警告（選んだ端末が見えない・使えない実機を飛ばした等。出す順）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
