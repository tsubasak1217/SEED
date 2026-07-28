using System.Collections.Generic;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// 配色設定 1 項目の定義（言語共通）。
/// </summary>
/// <param name="Label">設定ダイアログに表示する日本語ラベル。</param>
/// <param name="Key">設定ファイル（script_editor.json）の Colors に保存するキー。</param>
/// <param name="DefaultHex">既定色 "#RRGGBB"。</param>
public readonly record struct ColorSettingEntry(string Label, string Key, string DefaultHex);

/// <summary>
/// 言語ごとの配色設定セクション（設定 UI のグループ 1 つ分）。
/// </summary>
/// <param name="Label">セクション見出し（例 "C#" / "WGSL"）。</param>
/// <param name="Entries">このセクションに属する色設定項目。</param>
public sealed record ColorSettingSection(string Label, IReadOnlyList<ColorSettingEntry> Entries);

/// <summary>
/// スクリプトエディタの配色設定スキーマ（どの言語にどの色分類があるか）。
///
/// 設定ダイアログ・設定ロード時の既定値補完は、いずれもこのスキーマを走査して
/// データ駆動で動く。言語や分類を増やす場合はここへセクション／項目を足すだけでよい。
///
/// キーの規約:
///   - C#   … 歴史的経緯から接頭辞なし（"Comment" 等）。既存ユーザー設定を壊さないため変更禁止。
///   - WGSL … <see cref="WgslColorScheme.KeyPrefix"/>（"wgsl."）付き。C# と同名分類の衝突を避ける。
/// </summary>
public static class ScriptEditorColorSchema
{
    /// <summary>C# セクションの見出し。</summary>
    private const string CSharpSectionLabel = "C#";
    /// <summary>WGSL セクションの見出し。</summary>
    private const string WgslSectionLabel   = "WGSL";

    /// <summary>色が取得できなかった項目に使うフォールバック色（UI 表示用）。</summary>
    public const string FallbackHex = "#DCDCDC";

    /// <summary>
    /// 全セクションを返す。
    ///
    /// WGSL の既定色は Wgsl.xshd から実行時に読み取るため、
    /// 静的キャッシュせず呼び出しごとに構築する（xshd 未読込のタイミングでも
    /// 次回呼び出しで正しい既定が得られるようにするため）。
    /// xshd の読み込みに失敗した場合、WGSL セクションは項目 0 件になる。
    /// </summary>
    public static IReadOnlyList<ColorSettingSection> Sections()
    {
        return new[]
        {
            new ColorSettingSection(CSharpSectionLabel, CSharpEntries()),
            new ColorSettingSection(WgslSectionLabel,   WgslEntries()),
        };
    }

    /// <summary>C# の色設定項目（既存の定義から生成する。キー・既定色とも従来どおり）。</summary>
    private static IReadOnlyList<ColorSettingEntry> CSharpEntries()
    {
        var defaults = ScriptEditorSettings.DefaultColors();
        var list = new List<ColorSettingEntry>();
        foreach (var (label, key) in ScriptEditorSettings.ColorEntries)
            list.Add(new ColorSettingEntry(label, key, defaults.TryGetValue(key, out var d) ? d : FallbackHex));
        return list;
    }

    /// <summary>WGSL の色設定項目（既定色は Wgsl.xshd の Color 定義そのもの）。</summary>
    private static IReadOnlyList<ColorSettingEntry> WgslEntries()
    {
        var defaults = WgslColorScheme.DefaultColors();
        var list = new List<ColorSettingEntry>();
        foreach (var e in WgslColorScheme.Entries)
            if (defaults.TryGetValue(e.Key, out var d))
                list.Add(new ColorSettingEntry(e.Label, e.Key, d));
        return list;
    }
}
