using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// WGSL シンタックスハイライトの「色分類」定義と、ユーザー設定色の適用を担当する。
///
/// 役割:
///   - Wgsl.xshd の &lt;Color name="..."&gt; 定義（＝正典）と、設定ファイル上の
///     永続化キー（"wgsl.xxx"）との対応表を持つ。
///   - 既定色は xshd に書かれた色そのものを実行時に読み取って返す
///     （C# 側にハードコードした色定数を置かないため、xshd を編集すれば既定も追従する）。
///   - ユーザー設定色を <see cref="IHighlightingDefinition"/> の
///     <see cref="HighlightingColor"/> へ直接書き戻し、開いている .wgsl タブへ即時反映する。
///
/// キー名は必ず <see cref="KeyPrefix"/> を付ける。C# 側の配色キー（"Comment" 等、接頭辞なし）
/// と同名の分類（Comment / NumberLiteral）が存在するため、接頭辞が無いと衝突して
/// 片方の色設定がもう片方へ漏れてしまう。
/// </summary>
public static class WgslColorScheme
{
    /// <summary>WGSL 用配色キーの接頭辞（C# 側キーとの衝突回避用）。</summary>
    public const string KeyPrefix = "wgsl.";

    /// <summary>
    /// 設定 UI に並べる WGSL 色分類の 1 行分の定義。
    /// </summary>
    /// <param name="Label">設定ダイアログに表示する日本語ラベル。</param>
    /// <param name="Key">設定ファイルへ永続化するキー（接頭辞付き）。</param>
    /// <param name="XshdColorName">Wgsl.xshd 内の &lt;Color name="..."&gt; と一致する名前。</param>
    public readonly record struct Entry(string Label, string Key, string XshdColorName);

    // ── Wgsl.xshd の Color 名（正典）────────────────────────────
    // xshd の name 属性と 1 文字違わず一致させること。一致しない名前を書いても
    // 例外にはならず「色が反映されないだけ」になるため、変更時は xshd と対で直す。
    private const string XshdComment        = "Comment";
    private const string XshdAttribute      = "Attribute";
    private const string XshdControlKeyword = "ControlKeyword";
    private const string XshdDeclaration    = "Declaration";
    private const string XshdTypeKeyword    = "TypeKeyword";
    private const string XshdBoolLiteral    = "BoolLiteral";
    private const string XshdNumberLiteral  = "NumberLiteral";
    private const string XshdFunctionCall   = "FunctionCall";

    /// <summary>
    /// ユーザーが色設定できる WGSL 分類の一覧（表示順）。
    /// Wgsl.xshd の Color 定義と 1:1 で対応する。
    /// </summary>
    public static IReadOnlyList<Entry> Entries { get; } = new[]
    {
        new Entry("コメント",              KeyPrefix + "comment",        XshdComment),
        new Entry("属性 (@group 等)",      KeyPrefix + "attribute",      XshdAttribute),
        new Entry("制御構文キーワード",    KeyPrefix + "controlKeyword", XshdControlKeyword),
        new Entry("宣言 (fn/let/var 等)",  KeyPrefix + "declaration",    XshdDeclaration),
        new Entry("組み込み型 (f32 等)",   KeyPrefix + "type",           XshdTypeKeyword),
        new Entry("真偽値リテラル",        KeyPrefix + "boolLiteral",    XshdBoolLiteral),
        new Entry("数値リテラル",          KeyPrefix + "number",         XshdNumberLiteral),
        new Entry("関数呼び出し",          KeyPrefix + "functionCall",   XshdFunctionCall),
    };

    /// <summary>
    /// 各キーの既定色（"#RRGGBB"）を返す。値は Wgsl.xshd に書かれた色そのもの。
    ///
    /// xshd の読み込みに失敗した場合、または xshd 側に対応する Color 定義が
    /// 無い場合、そのキーは辞書に含まれない（呼び出し側は「設定できる項目が無い」
    /// として扱えばよい）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultColors()
    {
        var xshdDefaults = WgslHighlighting.XshdDefaultForegrounds();
        var result = new Dictionary<string, string>();
        foreach (var e in Entries)
            if (xshdDefaults.TryGetValue(e.XshdColorName, out var hex))
                result[e.Key] = hex;
        return result;
    }

    /// <summary>
    /// ユーザー設定の配色を WGSL ハイライト定義へ反映する。
    ///
    /// ハイライト定義は全エディタで 1 インスタンスを共有しているため、
    /// ここで書き換えたあとに各エディタを Redraw すれば、開いている
    /// .wgsl タブすべてへ即座に反映される（C# 側と同じ仕組み）。
    ///
    /// 設定に該当キーが無い場合は xshd の既定色へ戻す。これにより
    /// 「設定を消す＝xshd の色に戻る」という挙動が保証される。
    /// </summary>
    /// <param name="colors">配色設定（キー → "#RRGGBB"）。</param>
    public static void Apply(IReadOnlyDictionary<string, string> colors)
    {
        var def = WgslHighlighting.Get();
        if (def is null) return;

        var defaults = DefaultColors();

        // xshd 名 → 定義中の HighlightingColor を引けるようにする。
        var named = new Dictionary<string, HighlightingColor>(StringComparer.Ordinal);
        foreach (var c in def.NamedHighlightingColors)
            if (c.Name is not null) named[c.Name] = c;

        foreach (var e in Entries)
        {
            if (!named.TryGetValue(e.XshdColorName, out var target)) continue;

            // 設定値 → 既定値（xshd）の順に採用する。どちらも無ければ何もしない。
            if (!colors.TryGetValue(e.Key, out var hex) || string.IsNullOrWhiteSpace(hex))
                if (!defaults.TryGetValue(e.Key, out hex))
                    continue;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                target.Foreground = new SimpleHighlightingBrush(color);
            }
            catch
            {
                // 不正な色文字列は無視して従来の色を維持する
            }
        }
    }
}
