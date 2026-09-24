// ============================================================
//  AndroidPipelineException.cs — Android のビルド・配置・起動の失敗（種類付き）
//
//  失敗の種類は、コンソールツールの終了コードとエディタの表示（何を直せばよいか）の手掛かりになる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Pipeline;

/// <summary>失敗の種類。</summary>
public enum AndroidFailureKind
{
    /// <summary>指定の誤り（引数・設定 JSON・プロジェクトの値の食い違い）。</summary>
    InvalidRequest,

    /// <summary>道具が見つからない・使えない（SDK / NDK / JDK / cargo / dotnet）。</summary>
    Toolchain,

    /// <summary>端末が無い・選べない・使えない状態。</summary>
    Device,

    /// <summary>ビルドの工程の失敗（cargo ndk・SeedPak・同梱 .NET・Gradle）。</summary>
    Build,

    /// <summary>端末への操作の失敗（インストール・転送・起動・停止）。</summary>
    DeviceOperation,
}

/// <summary>Android のビルド・配置・起動の失敗。</summary>
public sealed class AndroidPipelineException : Exception
{
    /// <summary>失敗の種類。</summary>
    public AndroidFailureKind Kind { get; }

    /// <summary>種類と理由を指定して作る。</summary>
    /// <param name="kind">失敗の種類。</param>
    /// <param name="message">利用者へ見せる説明（何を直せばよいかを含める）。</param>
    /// <param name="inner">元の例外。</param>
    public AndroidPipelineException(AndroidFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
