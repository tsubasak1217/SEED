// ============================================================
//  CompletionContext.cs — 編集中のファイルから集めた「文脈の語」とその重み
//
//  【役割】
//  CompletionContextExtractor が作り、ApiReferenceSelector が読む値。
//  語ごとに「どういう出方をしたか」（ファイルのどこか・カーソルの近く・書きかけ・
//  using の名前空間・SEED. に続く語）を持ち、重みはその種類の重みの和。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>文脈の語の出方（重ねて持つ）。</summary>
[Flags]
public enum ContextWordKinds
{
    /// <summary>出方なし。</summary>
    None = 0,
    /// <summary>ファイルのどこかに出る。</summary>
    File = 1 << 0,
    /// <summary>カーソルの前後 cursor_window_lines 行に出る。</summary>
    NearCursor = 1 << 1,
    /// <summary>カーソルの直前で書きかけ（"Bind|"・"Bind.|" の Bind）。</summary>
    Typing = 1 << 2,
    /// <summary>using の名前空間の語（using SEED.Localization; の Localization）。</summary>
    Using = 1 << 3,
    /// <summary>SEED. に続く語（SEED.UI.ScreenStack の UI・ScreenStack）。</summary>
    EngineQualified = 1 << 4,
}

/// <summary>
/// 編集中のファイルの文脈の語（作った後は変わらない）。
/// </summary>
public sealed class CompletionContext
{
    /// <summary>語 → 出方（大文字小文字を区別しない）。</summary>
    public IReadOnlyDictionary<string, ContextWordKinds> Kinds { get; }

    /// <summary>語 → 重み（出方の重みの和）。</summary>
    public IReadOnlyDictionary<string, double> Weights { get; }

    /// <summary>カーソルの直前で書きかけの語（無ければ null）。接頭辞の一致を必ず見る。</summary>
    public string? TypingWord { get; }

    /// <summary>文脈を組み立てる（生成は <see cref="CompletionContextExtractor"/> から）。</summary>
    /// <param name="kinds">語 → 出方。</param>
    /// <param name="weights">重みの設定。</param>
    /// <param name="typingWord">書きかけの語。</param>
    public CompletionContext(IReadOnlyDictionary<string, ContextWordKinds> kinds, ApiReferenceWeights weights, string? typingWord)
    {
        Kinds = kinds;
        TypingWord = typingWord;
        var map = new Dictionary<string, double>(ApiIdentifierTokenizer.WordComparer);
        foreach (var (word, kind) in kinds) map[word] = WeightOf(kind, weights);
        Weights = map;
    }

    /// <summary>出方の重みの和（当てはまる種類ごとに 1 回ずつ足す。出た回数は数えない）。</summary>
    /// <param name="kind">出方。</param>
    /// <param name="weights">重みの設定。</param>
    /// <returns>重み。</returns>
    public static double WeightOf(ContextWordKinds kind, ApiReferenceWeights weights)
    {
        double sum = 0.0;
        if (kind.HasFlag(ContextWordKinds.File)) sum += weights.File;
        if (kind.HasFlag(ContextWordKinds.NearCursor)) sum += weights.NearCursor;
        if (kind.HasFlag(ContextWordKinds.Typing)) sum += weights.Typing;
        if (kind.HasFlag(ContextWordKinds.Using)) sum += weights.Using;
        if (kind.HasFlag(ContextWordKinds.EngineQualified)) sum += weights.EngineQualified;
        return sum;
    }
}
