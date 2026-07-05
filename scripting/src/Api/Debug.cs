using System;

namespace SEED;

/// <summary>
/// スクリプトからのログ出力。標準出力／標準エラーへ書き出し、エンジンのログに流れる。
/// <c>System.Console</c> を直接使わずに済むよう、Unity 風の <c>Debug.Log</c> を提供する。
/// </summary>
public static class Debug
{
    /// <summary>情報ログ。</summary>
    public static void Log(object? message)
        => Console.WriteLine($"[Script] {message}");

    /// <summary>警告ログ。</summary>
    public static void LogWarning(object? message)
        => Console.WriteLine($"[Script:警告] {message}");

    /// <summary>エラーログ（標準エラーへ）。</summary>
    public static void LogError(object? message)
        => Console.Error.WriteLine($"[Script:エラー] {message}");
}
