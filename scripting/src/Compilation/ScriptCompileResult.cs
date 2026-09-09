// ============================================================
//  ScriptCompileResult.cs — スクリプトコンパイルの結果
//
//  【役割】
//  「成功したか」「何型できたか」「どんなエラーが出たか」を呼び出し側へ返す。
//
//  【なぜ戻り値を型にするか】
//  ランタイム向けの入口（FFI）は int 1 個しか返せないので、エラー内容は
//  stderr へ流すしかない。一方エディタのパッケージ化では
//  「エラー全文をログへ出し、1 件でもあれば中止する」判断が要る。
//  戻り値を型にしておくと、同じコンパイル実装を両方から使い回せる。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Scripting.Compilation;

/// <summary>スクリプトコンパイルの結果（成功可否・型数・診断）。</summary>
public sealed class ScriptCompileResult
{
    /// <summary>コンパイルと出力に成功したか。</summary>
    public bool Success { get; init; }

    /// <summary>対象になったソースファイル数。</summary>
    public int SourceFileCount { get; init; }

    /// <summary>解決可能になったスクリプト型の数（型マップの登録型数）。</summary>
    public int ScriptTypeCount { get; init; }

    /// <summary>エラー診断の整形済みメッセージ（"パス(行): 内容"）。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>警告診断の件数（内容はログに出さず件数だけ持つ）。</summary>
    public int WarningCount { get; init; }

    /// <summary>出力した DLL のパス（失敗時は空文字）。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>
    /// 失敗結果を作る。
    /// </summary>
    /// <param name="errors">エラーメッセージ。</param>
    /// <param name="sourceFileCount">対象ソース数（判明していれば）。</param>
    /// <returns>失敗を表す結果。</returns>
    public static ScriptCompileResult Failed(IReadOnlyList<string> errors, int sourceFileCount = 0) =>
        new() { Success = false, Errors = errors, SourceFileCount = sourceFileCount };
}
