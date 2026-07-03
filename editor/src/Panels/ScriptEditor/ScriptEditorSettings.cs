using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタの書式設定・配色設定。
/// editor/settings/script_editor.json に永続化される。
///
/// - 書式: インデント幅・タブ/空白・タブサイズ・自動整形
/// - 配色: 構文要素（キーワード/クラス/enum/属性/文字列/コメント等）の色
/// </summary>
public sealed class ScriptEditorSettings
{
    // ── 書式設定 ──────────────────────────────────────────────
    /// <summary>インデント幅（空白数）。</summary>
    public int  IndentationSize   { get; set; } = 4;
    /// <summary>タブ文字を空白に変換するか。</summary>
    public bool ConvertTabsToSpaces { get; set; } = true;
    /// <summary>基準フォントサイズ。</summary>
    public double FontSize { get; set; } = 13.0;

    // ── 配色設定（16 進 RGB 文字列 "#RRGGBB"）──────────────────
    /// <summary>構文要素名 → 色。キーは HighlightingColor 名（BuildDarkCSharpHighlighting と対応）。</summary>
    public Dictionary<string, string> Colors { get; set; } = DefaultColors();

    /// <summary>VS ダークテーマ準拠のデフォルト配色。</summary>
    public static Dictionary<string, string> DefaultColors() => new()
    {
        ["Comment"]               = "#6A9955", // コメント
        ["String"]                = "#D69D85", // 文字列
        ["Keywords"]              = "#C586C0", // キーワード（if/for 等）
        ["ValueTypeKeywords"]     = "#569CD6", // 値型キーワード（int/bool 等）
        ["ReferenceTypeKeywords"] = "#569CD6", // 参照型キーワード（class/string 等）
        ["MethodCall"]            = "#DCDCAA", // メソッド呼び出し
        ["NumberLiteral"]         = "#B5CEA8", // 数値リテラル
        ["Preprocessor"]          = "#9B9B9B", // プリプロセッサ
        ["Punctuation"]           = "#DCDCDC", // 記号
    };

    /// <summary>ユーザーが色設定できる構文要素の一覧（表示名, キー）。</summary>
    public static IReadOnlyList<(string Label, string Key)> ColorEntries => new[]
    {
        ("コメント",       "Comment"),
        ("文字列",         "String"),
        ("キーワード",     "Keywords"),
        ("値型 (int等)",   "ValueTypeKeywords"),
        ("クラス/参照型",  "ReferenceTypeKeywords"),
        ("メソッド",       "MethodCall"),
        ("数値",           "NumberLiteral"),
        ("プリプロセッサ", "Preprocessor"),
        ("記号",           "Punctuation"),
    };

    // ── 永続化 ────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>設定ファイルのパス（editor/settings/script_editor.json）。</summary>
    public static string FilePath(string settingsDir) => Path.Combine(settingsDir, "script_editor.json");

    /// <summary>設定を読み込む。ファイルが無い・壊れている場合はデフォルトを返す。</summary>
    public static ScriptEditorSettings Load(string settingsDir)
    {
        try
        {
            var path = FilePath(settingsDir);
            if (!File.Exists(path)) return new ScriptEditorSettings();
            var s = JsonSerializer.Deserialize<ScriptEditorSettings>(File.ReadAllText(path), JsonOpts);
            if (s is null) return new ScriptEditorSettings();
            // 欠けている色キーはデフォルトで補完する
            foreach (var (_, key) in ColorEntries)
                if (!s.Colors.ContainsKey(key) && DefaultColors().TryGetValue(key, out var d))
                    s.Colors[key] = d;
            return s;
        }
        catch
        {
            return new ScriptEditorSettings();
        }
    }

    /// <summary>設定を保存する。</summary>
    public void Save(string settingsDir)
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(FilePath(settingsDir), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプトエディタ設定の保存に失敗: {ex.Message}");
        }
    }
}
