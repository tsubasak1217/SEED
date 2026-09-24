// ============================================================
//  OutputLineClassifier.cs — 見た目の指定が無い行（EditorLog.Write(本文)）の色と出どころを本文から決める
//
//  【由来】
//  従来 Panels/OutputPanel.xaml.cs の PickBrush（色）と Classify（出どころ）にあった判定を、そのまま規則の表にした
//  （段階C-2 で切り出し。判定の順と結果は変えていない。editor/tests/AndroidRunUiTests が従来の結果を固定している）。
//  規則は上から順に見て、最初に当たったものの色を使う。どれにも当たらなければ Default。
//
//  Android の実行の行は書き手（AndroidRun/AndroidRunOutputFormatter.cs）が見た目を決めて渡すので、ここを通らない
//  （子プロセスの出力・logcat の本文が「エラーらしいか」だけは <see cref="LooksLikeError"/> で同じ規則を使う）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Logging;

/// <summary>本文から Output パネルの行の見た目を決める。</summary>
public static class OutputLineClassifier
{
    /// <summary>色の規則 1 つ（本文にこの印を含めばこの色）。</summary>
    /// <param name="Tone">色の種類。</param>
    /// <param name="Marker">本文に含まれる印。</param>
    /// <param name="Comparison">照合の仕方（大文字小文字を区別するか）。</param>
    private sealed record ToneRule(OutputTone Tone, string Marker, StringComparison Comparison);

    /// <summary>
    /// 色の規則（上から順に見る）。ランタイムからの通知を最初に見るのは、通知の本文に error 等が含まれても
    /// 通知の色で出すため（従来の PickBrush と同じ順）。
    /// </summary>
    private static readonly ToneRule[] ToneRules =
    {
        new(OutputTone.Runtime, "[Runtime→Editor]", StringComparison.Ordinal),
        new(OutputTone.Error,   "error",            StringComparison.OrdinalIgnoreCase),
        new(OutputTone.Error,   "失敗",             StringComparison.Ordinal),
        new(OutputTone.Error,   "EXCEPTION",        StringComparison.Ordinal),
        new(OutputTone.Build,   "[cargo]",          StringComparison.Ordinal),
        new(OutputTone.Build,   "BUILDING",         StringComparison.Ordinal),
        new(OutputTone.Build,   "BuildAsync",       StringComparison.Ordinal),
    };

    /// <summary>
    /// ゲーム（ユーザースクリプト）の行の印。スクリプトの Debug.Log は [Script] 系を前に付けてランタイムの
    /// 標準出力へ流れる（[Script] / [ScriptCompileError] 等）。
    /// </summary>
    public const string GameMarker = "[Script";

    /// <summary>
    /// 本文から見た目を決める。
    /// </summary>
    /// <param name="line">本文。</param>
    /// <returns>見た目。</returns>
    public static OutputLineStyle Classify(string line) => new(ToneOf(line), SourceOf(line));

    /// <summary>本文から色の種類を決める（規則の表を上から見る）。</summary>
    /// <param name="line">本文。</param>
    /// <returns>色の種類。</returns>
    public static OutputTone ToneOf(string line)
    {
        foreach (var rule in ToneRules)
        {
            if (line.Contains(rule.Marker, rule.Comparison)) return rule.Tone;
        }
        return OutputTone.Default;
    }

    /// <summary>本文から出どころを決める（[Script を含めばゲーム）。</summary>
    /// <param name="line">本文。</param>
    /// <returns>出どころ。</returns>
    public static OutputSource SourceOf(string line) =>
        line.Contains(GameMarker, StringComparison.Ordinal) ? OutputSource.Game : OutputSource.Engine;

    /// <summary>
    /// 本文がエラーらしいか（エラーの規則のどれかに当たる）。子プロセスの出力・logcat の本文の色を決めるのに使う。
    /// </summary>
    /// <param name="text">本文。</param>
    /// <returns>エラーらしければ true。</returns>
    public static bool LooksLikeError(string text)
    {
        foreach (var rule in ToneRules)
        {
            if (rule.Tone == OutputTone.Error && text.Contains(rule.Marker, rule.Comparison)) return true;
        }
        return false;
    }
}
