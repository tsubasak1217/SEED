// ============================================================
//  ContextualApiReference.cs — 「編集中のファイルの文脈に合う API リファレンス」を返す窓口
//
//  【役割】
//  索引（ApiReferenceIndex）・設定（ApiReferenceSettings）・キャッシュを束ね、
//  「ファイルの全文・カーソルの位置・予算」から注入する本文を返す。
//    文脈の抽出（CompletionContextExtractor）→ 点付け・選択（ApiReferenceSelector）→ キャッシュ
//  エディタ側の入口は ScriptApiReference（ファイルの場所探し・環境設定の予算・ログ）。
//
//  【WPF 非依存】editor/tests/InlineCompletionTests がこのフォルダを丸ごとリンクする。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

/// <summary>
/// 文脈に合う API リファレンスを選ぶ窓口（索引と設定は作った後は変わらない。複数のスレッドから呼んでよい）。
/// </summary>
public sealed class ContextualApiReference
{
    /// <summary>選択結果を覚える数の既定。</summary>
    public const int DefaultCacheCapacity = 16;

    /// <summary>索引。</summary>
    public ApiReferenceIndex Index { get; }

    /// <summary>選び方の設定。</summary>
    public ApiReferenceSettings Settings { get; }

    /// <summary>作るときに見つかった問題（どの節にも当たらない always_include など。空なら正常）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>常に入れる節（全切れ端）の文字数の合計（予算がこれより小さいと一部しか入らない）。</summary>
    public int AlwaysIncludedChars { get; }

    /// <summary>選択結果のキャッシュ。</summary>
    public ApiReferenceSelectionCache Cache { get; }

    /// <summary>常に入れる切れ端の印。</summary>
    private readonly bool[] _alwaysMask;

    /// <summary>窓口を作る。</summary>
    /// <param name="index">索引。</param>
    /// <param name="settings">選び方の設定。</param>
    /// <param name="cacheCapacity">選択結果を覚える数。</param>
    public ContextualApiReference(ApiReferenceIndex index, ApiReferenceSettings settings, int cacheCapacity = DefaultCacheCapacity)
    {
        Index = index;
        Settings = settings;
        Cache = new ApiReferenceSelectionCache(cacheCapacity);

        _alwaysMask = ApiReferenceSelector.AlwaysIncludeMask(index, settings, out var unmatched);
        AlwaysIncludedChars = index.Parts.Where(p => _alwaysMask[p.Index]).Sum(p => p.Length);
        Warnings = unmatched
            .Select(entry => $"always_include の「{entry}」で始まる見出しの節がリファレンスに無い（見出しが変わった？）")
            .ToList();
    }

    /// <summary>リファレンスの Markdown と設定から作る（切れ端の上限は設定の max_part_chars）。</summary>
    /// <param name="markdown">docs/scripting_api.md の全文。</param>
    /// <param name="settings">選び方の設定。</param>
    /// <returns>窓口。</returns>
    public static ContextualApiReference FromMarkdown(string markdown, ApiReferenceSettings settings) =>
        new(ApiReferenceIndex.FromMarkdown(markdown ?? string.Empty, settings.MaxPartChars), settings);

    /// <summary>この切れ端を常に入れるか。</summary>
    /// <param name="part">切れ端。</param>
    /// <returns>常に入れるなら true。</returns>
    public bool IsAlwaysIncluded(ApiReferencePart part) => _alwaysMask[part.Index];

    /// <summary>
    /// 編集中のファイルの文脈に合う節を予算の中で選ぶ（同じ内容・位置・予算ならキャッシュを返す）。
    /// </summary>
    /// <param name="fileText">編集中のファイルの全文。</param>
    /// <param name="caretOffset">カーソルの位置。</param>
    /// <param name="budgetChars">予算（範囲外は丸める。0 なら空）。</param>
    /// <returns>選択。</returns>
    public ApiReferenceSelection Select(string fileText, int caretOffset, int budgetChars)
    {
        fileText ??= string.Empty;
        int budget = Math.Clamp(budgetChars, ApiReferenceSettings.MinBudgetChars, ApiReferenceSettings.MaxBudgetChars);
        if (budget == 0 || Index.Parts.Count == 0) return ApiReferenceSelection.Empty;

        var key = ApiReferenceSelectionCache.MakeKey(fileText, caretOffset, budget);
        if (Cache.TryGet(key, out var cached)) return cached;

        var selection = ApiReferenceSelector.Select(Index, _alwaysMask, ScoreFor(fileText, caretOffset), Settings, budget);
        Cache.Add(key, selection);
        return selection;
    }

    /// <summary>
    /// 文脈を渡さない呼び出し用: 圧縮後のリファレンスの先頭から予算までを返す（従来の動き）。
    /// </summary>
    /// <param name="budgetChars">予算（範囲外は丸める）。</param>
    /// <returns>先頭からの本文。</returns>
    public string Head(int budgetChars)
    {
        int budget = Math.Clamp(budgetChars, ApiReferenceSettings.MinBudgetChars, ApiReferenceSettings.MaxBudgetChars);
        var text = Index.CompactText;
        return text.Length > budget ? text[..budget] : text;
    }

    /// <summary>切れ端ごとの点を求める（キャッシュしない。診断・テスト用）。</summary>
    /// <param name="fileText">編集中のファイルの全文。</param>
    /// <param name="caretOffset">カーソルの位置。</param>
    /// <returns>切れ端の通し番号ごとの点。</returns>
    public double[] ScoreFor(string fileText, int caretOffset)
    {
        var context = CompletionContextExtractor.Extract(fileText ?? string.Empty, caretOffset, Settings);
        return ApiReferenceSelector.Score(Index, context, Settings);
    }
}
