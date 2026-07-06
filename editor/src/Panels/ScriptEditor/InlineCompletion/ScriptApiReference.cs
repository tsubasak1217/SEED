using System;
using System.IO;
using System.Text;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// スクリプト API リファレンス（docs/scripting_api.md）を読み込み、
/// インライン補完のシステムプロンプトへ注入するための正典テキストを提供する。
///
/// このファイルが「スクリプトから使える API を AI に教える」唯一の情報源。
/// API を追加したら docs/scripting_api.md を更新すれば、補完にも自動反映される。
///
/// 【トークン圧縮】ドキュメント全文を注入するとリクエストが数千トークンになり、
/// Groq の TPM / TPD（日次上限）を数回で使い切ってしまう。そのため注入時は
/// 「見出し + コードブロック + 表」だけを抽出し、説明の散文を落とした要約版を使う
///（API シグネチャの情報密度はコードブロックにほぼ集約されている）。
/// 実行時に 1 度だけ読み込んでキャッシュする。
/// </summary>
public static class ScriptApiReference
{
    /// <summary>リファレンス Markdown のファイル名。</summary>
    private const string FileName = "scripting_api.md";

    /// <summary>リポジトリ内での相対パス（親ディレクトリを遡って探す際に使う）。</summary>
    private const string RepoRelativePath = "docs/scripting_api.md";

    /// <summary>
    /// プロンプトへ注入する最大文字数（過大なプロンプトによる遅延・トークン消費を防ぐ）。
    /// 圧縮後の全文が収まるサイズにしておく（末尾を切ると新しい API ほど失われるため）。
    /// </summary>
    private const int MaxChars = 12000;

    // 読み込み結果のキャッシュ（null 未取得 / "" 見つからず）
    private static string? _cached;

    /// <summary>
    /// 注入用に圧縮したリファレンスを返す（見つからなければ空文字）。初回のみディスクから読み込む。
    /// </summary>
    public static string Load()
    {
        if (_cached is not null) return _cached;

        try
        {
            var path = Locate();
            if (path is null) { _cached = ""; return _cached; }

            var text = Compact(File.ReadAllText(path));
            if (text.Length > MaxChars) text = text[..MaxChars];
            _cached = text;
            SEEDEditor.EditorLog.Write($"[インライン補完] APIリファレンス注入サイズ: {text.Length} 文字（圧縮済み）");
        }
        catch (Exception ex)
        {
            SEEDEditor.EditorLog.Write($"[インライン補完] APIリファレンス読み込み失敗: {ex.Message}");
            _cached = "";
        }
        return _cached;
    }

    /// <summary>
    /// Markdown から「見出し・コードブロック・表・重要注記」だけを抽出して圧縮する。
    ///
    /// 残すもの:
    ///  - 見出し行（# 〜 ####。文脈の区切りとして必要）
    ///  - ```csharp コードブロック（API シグネチャの本体）
    ///  - 表の行（| 区切り。コンポーネント一覧・ライフサイクル表など）
    ///  - 「重要」を含む引用行（Unity ではない・SEED. 修飾必須などの誤補完防止情報）
    /// 落とすもの: 説明の散文・補足リスト・メンテナ向けセクション。
    /// </summary>
    private static string Compact(string markdown)
    {
        var sb = new StringBuilder(markdown.Length / 2);
        bool inCode = false;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            // コードフェンスの開始/終了（フェンス内は全行残す）
            if (trimmed.StartsWith("```"))
            {
                inCode = !inCode;
                sb.AppendLine(line);
                continue;
            }
            if (inCode) { sb.AppendLine(line); continue; }

            // 見出し（メンテナ向けセクション以降は不要なので打ち切る）
            if (trimmed.StartsWith("#"))
            {
                if (trimmed.Contains("メンテナ向け")) break;
                sb.AppendLine(line);
                continue;
            }
            // 表の行（API 一覧表・ライフサイクル表）
            if (trimmed.StartsWith("|")) { sb.AppendLine(line); continue; }
            // 「重要」を含む引用注記（Unity ではない等、誤補完防止に必須の情報）
            if (trimmed.StartsWith(">") && trimmed.Contains("重要")) { sb.AppendLine(line); continue; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// リファレンス Markdown の場所を特定する。
    /// 1) 実行ディレクトリ直下（ビルドで出力コピーされた場合）
    /// 2) 実行ディレクトリから親を遡って docs/scripting_api.md を探す（開発時のリポジトリ構成）
    /// </summary>
    private static string? Locate()
    {
        // 1) 出力ディレクトリへコピーされた場合
        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, FileName);
        if (File.Exists(local)) return local;

        // 2) 親ディレクトリを遡ってリポジトリ内の docs/scripting_api.md を探す
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, RepoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
