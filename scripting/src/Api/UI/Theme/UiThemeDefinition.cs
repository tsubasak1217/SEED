using System;
using System.Collections.Generic;
using System.Linq;

namespace SEED.UI;

// ============================================================
//  UiThemeDefinition.cs — 継承を解いたテーマ（明暗の組を持つ。W2-9。docs/ui_theme.md §3・§4。純粋な計算）
//
//  【作り】根（組み込みの既定のテーマ）から葉（読み込んだテーマ）までの JSON の鎖。UiTheme.Apply に渡すのはこれ。
//  【明暗】テーマの明暗（Brightness）と、対応する明暗（Supports）:
//    - 鎖を根から順に見て、brightness を書いたテーマで「その明暗だけ」に置き直し、light / dark の節を持つテーマでその明暗を足す
//    - 組み込みの既定のテーマは暗い方＋light の節 ＝ 両方に対応。brightness を書かず一部だけ上書きしたテーマも両方に対応し、
//      brightness を書いた単色のテーマ（Wake or Pay の midnight など）はその明暗だけ（節を書けばもう一方にも対応）
//  【解決】Resolve(明暗) は鎖を根から順に重ねた平らな表（UiThemeData。明暗ごとに 1 度だけ作って覚える）:
//    各テーマで 明暗に依らない値 → その明暗の節 → 種の色から作った値（明示していないトークンだけ。UiThemeTable.ApplyLayer）
//  対応していない明暗を求められたら、テーマの明暗で解く。
// ============================================================

/// <summary>継承を解いたテーマ（明暗の組を持つ）。</summary>
public sealed class UiThemeDefinition
{
    /// <summary>明暗の数（覚えておく表の数）。</summary>
    private const int BrightnessCount = 2;
    /// <summary>パスの区切り（名前をファイル名から作るとき）。</summary>
    private const char PathSeparator = '/';
    /// <summary>拡張子の区切り。</summary>
    private const char ExtensionSeparator = '.';

    /// <summary>根 → 葉の JSON の鎖。</summary>
    private readonly UiThemeSource[] _chain;
    /// <summary>明暗ごとの解決済みの表（まだなら null）。</summary>
    private readonly UiThemeData?[] _resolved = new UiThemeData?[BrightnessCount];

    /// <summary>名前（葉の name。無ければファイル名）。</summary>
    public string Name { get; }
    /// <summary>葉の出どころ（assets:// のパスか <see cref="UiThemePaths.BuiltInOrigin"/>）。</summary>
    public string Origin { get; }
    /// <summary>テーマの明暗（Theme の選び方・対応していない明暗を求められたときに使う）。</summary>
    public UiBrightness Brightness { get; }
    /// <summary>明るい方に対応しているか。</summary>
    public bool SupportsLight { get; }
    /// <summary>暗い方に対応しているか。</summary>
    public bool SupportsDark { get; }
    /// <summary>鎖の全部の警告（読み込み・継承の解き方）。</summary>
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>根 → 葉の出どころ（診断用）。</summary>
    public IReadOnlyList<string> ChainOrigins { get; }
    /// <summary>組み込みの既定のテーマそのものか。</summary>
    public bool IsBuiltIn => Origin == UiThemePaths.BuiltInOrigin;

    /// <summary>鎖から作る（UiThemeResolver が使う）。</summary>
    /// <param name="chain">根 → 葉の JSON（1 つ以上）。</param>
    /// <param name="resolverWarnings">継承を解くときに出た警告（無い親・輪・深すぎ）。</param>
    internal UiThemeDefinition(IReadOnlyList<UiThemeSource> chain, IEnumerable<string> resolverWarnings)
    {
        if (chain.Count == 0) throw new ArgumentException("テーマの鎖が空です", nameof(chain));
        _chain = chain.ToArray();
        var leaf = _chain[^1];
        Origin = leaf.Origin;
        Name = leaf.Name.Length > 0 ? leaf.Name : NameFromOrigin(leaf.Origin);
        // 明暗と対応: 根から順に、brightness で置き直し・節で足す
        var brightness = UiBrightness.Dark;
        bool light = false, dark = true;
        foreach (var source in _chain)
        {
            if (source.Brightness is { } b)
            {
                brightness = b;
                light = b == UiBrightness.Light;
                dark = b == UiBrightness.Dark;
            }
            if (source.Light is not null) light = true;
            if (source.Dark is not null) dark = true;
        }
        Brightness = brightness;
        SupportsLight = light;
        SupportsDark = dark;
        Warnings = _chain.SelectMany(s => s.Warnings).Concat(resolverWarnings).ToArray();
        ChainOrigins = _chain.Select(s => s.Origin).ToArray();
    }

    /// <summary>その明暗に対応しているか。</summary>
    public bool Supports(UiBrightness brightness) => brightness == UiBrightness.Light ? SupportsLight : SupportsDark;

    /// <summary>
    /// その明暗の平らな表（対応していなければテーマの明暗の表。明暗ごとに 1 度だけ作る）。
    /// </summary>
    public UiThemeData Resolve(UiBrightness brightness)
    {
        if (!Supports(brightness)) brightness = Brightness;
        int slot = (int)brightness;
        if (_resolved[slot] is { } cached) return cached;
        var table = UiThemeTable.From(null);
        foreach (var source in _chain) table.ApplyLayer(source, brightness);
        var data = table.Freeze(Name, brightness, Warnings);
        _resolved[slot] = data;
        return data;
    }

    /// <summary>出どころのファイル名（拡張子なし）を名前にする。</summary>
    private static string NameFromOrigin(string origin)
    {
        int slash = origin.LastIndexOf(PathSeparator);
        string file = slash < 0 ? origin : origin.Substring(slash + 1);
        int dot = file.LastIndexOf(ExtensionSeparator);
        return dot > 0 ? file.Substring(0, dot) : file;
    }
}
