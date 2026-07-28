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

    /// <summary>
    /// AI インライン補完（Copilot 風ゴーストテキスト → Tab 確定）を有効にするか。
    /// 有効化すると Groq（クラウド・OpenAI 互換）で補完する。既定は無効。
    /// </summary>
    public bool InlineCompletionEnabled { get; set; } = false;

    /// <summary>
    /// インライン補完を「手動トリガ（キー押下）時のみ」実行するか。
    /// true（既定）: 入力中に自動発火せず、明示的なショートカットでのみ補完する。
    ///   → クラウド API のレート制限（トークン/分）に当たりにくい。
    /// false: 入力が止まったら自動で補完する（従来動作。API 消費が多い）。
    /// </summary>
    public bool InlineCompletionManualOnly { get; set; } = true;

    /// <summary>Groq バックエンドの API キー（https://console.groq.com で取得）。</summary>
    public string GroqApiKey { get; set; } = "";

    /// <summary>Groq で使うモデル名（例: llama-3.1-8b-instant / qwen-2.5-coder-32b 等）。</summary>
    public string GroqModel { get; set; } = "llama-3.1-8b-instant";

    // ── 意味解析（識別子）系の配色キー ─────────────────────────
    // Roslyn のセマンティック分類（型名・メソッド名・変数名など）を論理的に
    // グループ化した色キー。1 つの論理キーへ複数の Roslyn 分類種別が対応する
    // （対応表は ScriptEditorPanel.SemanticKeyMap）。正規表現ハイライトでは
    // 識別できないユーザー定義型・変数を、ここで指定した色に着色する。
    public static class SemKeys
    {
        /// <summary>型名（クラス・デリゲート・型引数）。</summary>
        public const string Type          = "sem.type";
        /// <summary>構造体。</summary>
        public const string Struct        = "sem.struct";
        /// <summary>列挙体・インターフェース。</summary>
        public const string EnumInterface = "sem.enumInterface";
        /// <summary>メソッド名。</summary>
        public const string Method        = "sem.method";
        /// <summary>メンバ変数（フィールド・定数・列挙値）。</summary>
        public const string Field         = "sem.field";
        /// <summary>ローカル（一時）変数。</summary>
        public const string Local         = "sem.local";
        /// <summary>メソッド引数。</summary>
        public const string Parameter     = "sem.parameter";
    }

    // ── 配色設定（16 進 RGB 文字列 "#RRGGBB"）──────────────────
    /// <summary>
    /// 構文要素名 → 色。キーの体系は言語ごとに異なる:
    ///   - C#   : HighlightingColor 名（"Comment" 等）または SemKeys の論理キー（接頭辞なし）。
    ///   - WGSL : <see cref="WgslColorScheme.KeyPrefix"/>（"wgsl."）付きのキー。
    /// C# 側のキーは既存ユーザー設定との互換のため変更してはならない。
    /// 設定 UI に並ぶ項目の一覧は <see cref="ScriptEditorColorSchema"/> が持つ。
    /// </summary>
    public Dictionary<string, string> Colors { get; set; } = DefaultColors();

    /// <summary>VS ダークテーマ準拠のデフォルト配色。</summary>
    public static Dictionary<string, string> DefaultColors() => new()
    {
        // 正規表現ハイライト（キーワード・文字列・コメント等）
        ["Comment"]               = "#6A9955", // コメント
        ["String"]                = "#D69D85", // 文字列
        ["Keywords"]              = "#C586C0", // キーワード（if/for 等）
        ["ValueTypeKeywords"]     = "#569CD6", // 値型キーワード（int/bool 等）
        ["ReferenceTypeKeywords"] = "#569CD6", // 参照型キーワード（class/string 等）
        ["MethodCall"]            = "#DCDCAA", // メソッド呼び出し
        ["NumberLiteral"]         = "#B5CEA8", // 数値リテラル
        ["Preprocessor"]          = "#9B9B9B", // プリプロセッサ
        ["Punctuation"]           = "#DCDCDC", // 記号
        // 意味解析（識別子）系。メンバ変数と一時変数は色を分ける。
        [SemKeys.Type]            = "#4EC9B0", // 型名＝ティール
        [SemKeys.Struct]          = "#86C691", // 構造体＝緑系
        [SemKeys.EnumInterface]   = "#B8D7A3", // 列挙体・インターフェース
        [SemKeys.Method]          = "#DCDCAA", // メソッド＝薄黄
        [SemKeys.Field]           = "#FFFFFF", // メンバ変数＝白
        [SemKeys.Local]           = "#9CDCFE", // ローカル変数＝水色
        [SemKeys.Parameter]       = "#9CDCFE", // 引数＝水色
    };

    /// <summary>
    /// ユーザーが色設定できる <b>C#</b> 構文要素の一覧（表示名, キー）。
    /// WGSL 側は <see cref="WgslColorScheme.Entries"/> にあり、
    /// 両者は <see cref="ScriptEditorColorSchema"/> でセクションとして束ねられる。
    /// </summary>
    public static IReadOnlyList<(string Label, string Key)> ColorEntries => new[]
    {
        // 正規表現ハイライト
        ("コメント",       "Comment"),
        ("文字列",         "String"),
        ("キーワード",     "Keywords"),
        ("値型 (int等)",   "ValueTypeKeywords"),
        ("クラス/参照型",  "ReferenceTypeKeywords"),
        ("メソッド呼び出し", "MethodCall"),
        ("数値",           "NumberLiteral"),
        ("プリプロセッサ", "Preprocessor"),
        ("記号",           "Punctuation"),
        // 意味解析（識別子）系
        ("型名",           SemKeys.Type),
        ("構造体",         SemKeys.Struct),
        ("列挙体/IF",      SemKeys.EnumInterface),
        ("メソッド定義",   SemKeys.Method),
        ("メンバ変数",     SemKeys.Field),
        ("ローカル変数",   SemKeys.Local),
        ("引数",           SemKeys.Parameter),
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
            // 欠けている色キーはデフォルトで補完する。
            // C# / WGSL 双方を配色スキーマから走査するため、言語が増えても修正不要。
            // （WGSL の既定色は Wgsl.xshd から実行時に読み取られる）
            foreach (var section in ScriptEditorColorSchema.Sections())
                foreach (var entry in section.Entries)
                    if (!s.Colors.ContainsKey(entry.Key))
                        s.Colors[entry.Key] = entry.DefaultHex;
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
