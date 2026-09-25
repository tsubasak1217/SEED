// ============================================================
//  AndroidPipelineResult.cs — 1 回のビルド・配置・起動の結果
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Plan;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;

namespace SEEDEditor.Android.Pipeline;

/// <summary>1 つの工程の結果。</summary>
/// <param name="Phase">工程。</param>
/// <param name="Outcome">終わり方。</param>
/// <param name="Summary">結果の一行説明（飛ばした理由を含む）。</param>
/// <param name="Elapsed">所要時間。</param>
public sealed record AndroidStepOutcome(AndroidPipelinePhase Phase, AndroidPhaseOutcome Outcome, string Summary, TimeSpan Elapsed);

/// <summary>1 回の実行の結果。</summary>
public sealed record AndroidPipelineResult
{
    /// <summary>最後まで成功したか。</summary>
    public bool Succeeded => FailureKind is null && !Canceled;

    /// <summary>中断されたか。</summary>
    public bool Canceled { get; init; }

    /// <summary>失敗の種類（成功・中断なら null）。</summary>
    public AndroidFailureKind? FailureKind { get; init; }

    /// <summary>失敗の説明。</summary>
    public string? FailureMessage { get; init; }

    /// <summary>工程ごとの結果（行った順。飛ばしたものも含む）。</summary>
    public IReadOnlyList<AndroidStepOutcome> Steps { get; init; } = Array.Empty<AndroidStepOutcome>();

    /// <summary>全体の所要時間。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>実行計画（準備で失敗したときは null）。</summary>
    public AndroidBuildPlan? Plan { get; init; }

    /// <summary>対象の端末（無ければ null）。</summary>
    public AdbDevice? Device { get; init; }

    /// <summary>アプリの識別情報（準備で失敗したときは null）。</summary>
    public AndroidAppIdentity? Identity { get; init; }

    /// <summary>
    /// 今回の Gradle の出力（APK / AAB。ビルドの目的でなければ・準備で失敗したときは null。段階D）。
    /// パッケージ化ウィンドウはこれを出力フォルダへ写す。
    /// </summary>
    public string? ArtifactPath { get; init; }

    /// <summary>Google Play の要件チェックの結果（配布用のビルドだけ。段階D）。</summary>
    public AndroidRequirementReport? RequirementReport { get; init; }
}
