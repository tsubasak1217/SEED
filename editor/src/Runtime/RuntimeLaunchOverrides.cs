// ============================================================
//  RuntimeLaunchOverrides.cs — 別ウィンドウ Play のランタイムの起動に足す環境変数と起動引数
//
//  【役割】
//  RuntimeManager がウィンドウ Play（別プロセス）を起動するときに、従来の起動（エディタの PID・IPC のパイプ・
//  アセットの場所・シーン）に「足す」ものをまとめた値。いまの使い道は端末の模擬
//  （DevicePresets/DevicePresetLaunchEnvironment が作る。docs/editor_device_presets.md）。
//    - 環境変数: 名前と値。値が null の変数は「子プロセスから消す」（エディタ自身の環境から受け継いだ値を残さない）
//    - 起動引数: 従来の引数の後ろへ空白区切りで足す（1 本の文字列で渡すので、空白・引用符を含む引数は受け付けない）
//    - Key: 起動の条件の同一性。常駐の Play プロセス（Stop 後に隠して保持し、次の Play で使い回すもの）を
//           使い回してよいかを、この値が同じかで決める（PlayRuntimeReusePolicy）。環境変数と引数から作るので、
//           同じ条件なら同じ値になる
//
//  Edit のランタイム（エディタに埋め込むもの）には付けない（RuntimeManager.LaunchAsync が Play のときだけ当てる）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Runtime;

/// <summary>起動に足す環境変数 1 つ。</summary>
/// <param name="Name">名前。</param>
/// <param name="Value">値（null なら子プロセスの環境から消す）。</param>
public sealed record RuntimeLaunchVariable(string Name, string? Value);

/// <summary>別ウィンドウ Play のランタイムの起動に足す環境変数と起動引数。</summary>
public sealed class RuntimeLaunchOverrides
{
    /// <summary>引数の区切り（従来の引数と同じく空白 1 つ）。</summary>
    private const string ArgumentSeparator = " ";

    /// <summary>Key の中の項目の区切り（名前・値・引数に現れない改行）。</summary>
    private const string KeySeparator = "\n";

    /// <summary>Key とログでの「消す」変数の表記。</summary>
    private const string UnsetMarker = "(消す)";

    /// <summary>環境変数のログの書式（{0}=名前、{1}=値）。</summary>
    private const string VariableLogFormat = "{0}={1}";

    /// <summary>引数に使えない文字（空白・引用符。1 本の引数の文字列が割れる）。</summary>
    private static readonly char[] ForbiddenArgumentChars = { ' ', '\t', '\r', '\n', '"' };

    /// <summary>
    /// 上書きを作る。
    /// </summary>
    /// <param name="label">ログに出す短い説明（例「端末の模擬: Pixel 6a 半分」）。</param>
    /// <param name="environment">環境変数（並びのまま当てる）。</param>
    /// <param name="arguments">足す起動引数（空白・引用符を含まないこと）。</param>
    /// <exception cref="ArgumentException">名前が空の環境変数・空白や引用符を含む引数があるとき。</exception>
    public RuntimeLaunchOverrides(string label, IReadOnlyList<RuntimeLaunchVariable> environment, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var variable in environment)
        {
            if (string.IsNullOrWhiteSpace(variable.Name)) throw new ArgumentException("環境変数の名前が空です。", nameof(environment));
        }
        foreach (var argument in arguments)
        {
            if (string.IsNullOrEmpty(argument) || argument.IndexOfAny(ForbiddenArgumentChars) >= 0)
            {
                throw new ArgumentException($"起動引数に空白・引用符は使えません: \"{argument}\"", nameof(arguments));
            }
        }

        Label = label;
        Variables = environment;
        Arguments = arguments;
        Key = string.Join(KeySeparator,
            environment.Select(DescribeVariable).Concat(arguments));
    }

    /// <summary>ログに出す短い説明。</summary>
    public string Label { get; }

    /// <summary>環境変数（並びのまま当てる）。</summary>
    public IReadOnlyList<RuntimeLaunchVariable> Variables { get; }

    /// <summary>足す起動引数。</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>起動の条件の同一性（同じ環境変数と引数なら同じ値。常駐の Play を使い回してよいかの判定に使う）。</summary>
    public string Key { get; }

    /// <summary>
    /// 従来の起動引数の後ろへ足す。
    /// </summary>
    /// <param name="baseArguments">従来の起動引数（1 本の文字列）。</param>
    /// <returns>足した起動引数。</returns>
    public string AppendArguments(string baseArguments) =>
        Arguments.Count == 0 ? baseArguments : baseArguments + ArgumentSeparator + string.Join(ArgumentSeparator, Arguments);

    /// <summary>
    /// 子プロセスの環境（ProcessStartInfo.Environment）へ当てる。値が null の変数は消す。ほかの変数には触らない。
    /// </summary>
    /// <param name="environment">子プロセスの環境。</param>
    public void ApplyEnvironment(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var variable in Variables)
        {
            if (variable.Value is null) environment.Remove(variable.Name);
            else environment[variable.Name] = variable.Value;
        }
    }

    /// <summary>ログ用の 1 行（環境変数と引数を空白区切りで）。</summary>
    /// <returns>文字列。</returns>
    public string Describe() =>
        string.Join(ArgumentSeparator, Variables.Select(DescribeVariable).Concat(Arguments));

    /// <summary>環境変数 1 つの表記（"名前=値"。消す変数は "名前=(消す)"）。</summary>
    /// <param name="variable">環境変数。</param>
    /// <returns>表記。</returns>
    private static string DescribeVariable(RuntimeLaunchVariable variable) =>
        string.Format(VariableLogFormat, variable.Name, variable.Value ?? UnsetMarker);
}
