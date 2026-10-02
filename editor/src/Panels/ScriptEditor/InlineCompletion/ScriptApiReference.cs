using System;
using System.IO;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

namespace SEEDEditor.Panels.ScriptEditor.InlineCompletion;

/// <summary>
/// スクリプト API リファレンス（docs/scripting_api.md）を読み込み、
/// インライン補完のシステムプロンプトへ注入するための正典テキストを提供する（エディタ側の入口）。
///
/// このファイルが「スクリプトから使える API を AI に教える」唯一の情報源。
/// API を追加したら docs/scripting_api.md を更新すれば、補完にも自動反映される。
///
/// 【トークン圧縮】ドキュメント全文を注入するとリクエストが数千トークンになり、
/// Groq の TPM / TPD（日次上限）を数回で使い切ってしまう。そのため注入時は
/// 「見出し + コードブロック + 表」だけを抽出し、説明の散文を落とした要約版を使う
/// （<see cref="ApiReferenceCompactor"/>。API シグネチャの情報密度はコードブロックにほぼ集約されている）。
///
/// 【文脈による節の選択】圧縮しても全体は約 20 万字あり、予算（既定 12000 字）に収まらない。
/// 以前は先頭から予算までを切っていたため後ろの節（§7.1x 以降）が AI に届かなかった。
/// 今は <see cref="LoadFor"/> が編集中のファイルの文脈に合う節を選んで予算内に収める
/// （<see cref="ContextualApiReference"/>。正典は docs/editor_inline_completion.md）。
///
/// 【設定】選び方は editor/config/inline_completion_reference.json（<see cref="ApiReferenceSettings"/>）、
/// 予算は環境設定 editor_preferences.json の inline_completion_reference_chars（無ければ JSON の budget_chars）。
/// リファレンスと設定は初回に 1 度だけ読み込んでキャッシュする（変えたらエディタを再起動する）。
/// </summary>
public static class ScriptApiReference
{
    /// <summary>リファレンス Markdown のファイル名。</summary>
    private const string FileName = "scripting_api.md";

    /// <summary>リポジトリ内での相対パス（親ディレクトリを遡って探す際に使う）。</summary>
    private const string RepoRelativePath = "docs/scripting_api.md";

    /// <summary>ログの接頭辞（[インライン補完] で追える）。</summary>
    private const string LogPrefix = "[インライン補完]";

    /// <summary>遅延読み込みの排他の錠。</summary>
    private static readonly object Gate = new();

    /// <summary>読み込み済みの窓口（null = 未読み込みか、リファレンスが見つからない・読めない）。</summary>
    private static ContextualApiReference? _reference;

    /// <summary>読み込みを試みたか（見つからないときに毎回ディスクを探さないため）。</summary>
    private static bool _loadAttempted;

    /// <summary>
    /// 今の予算（注入する最大文字数）。環境設定の inline_completion_reference_chars が優先し、
    /// 無ければ inline_completion_reference.json の budget_chars（既定 12000）。範囲外は丸める。
    /// </summary>
    public static int BudgetChars
    {
        get
        {
            var settings = GetReference()?.Settings ?? ApiReferenceSettings.BuiltIn();
            return settings.ResolveBudget(EditorPreferences.Instance.InlineCompletionReferenceChars);
        }
    }

    /// <summary>
    /// 文脈を渡さない呼び出し（従来の動き）: 圧縮したリファレンスの先頭から予算までを返す
    /// （見つからなければ空文字）。初回のみディスクから読み込む。
    /// </summary>
    /// <returns>注入する本文。</returns>
    public static string Load()
    {
        var reference = GetReference();
        return reference is null ? string.Empty : reference.Head(BudgetChars);
    }

    /// <summary>
    /// 編集中のファイルの文脈に合う節を予算内で選ぶ（見つからなければ空の選択）。
    /// 同じ内容・カーソルの位置・予算なら前回の選択を使い回す（補完は打鍵ごとに走るため）。
    /// </summary>
    /// <param name="fileText">編集中のファイルの全文。</param>
    /// <param name="caretOffset">カーソルの位置。</param>
    /// <returns>選択（本文・節の名前・文字数）。</returns>
    public static ApiReferenceSelection LoadFor(string fileText, int caretOffset)
    {
        var reference = GetReference();
        return reference is null ? ApiReferenceSelection.Empty : reference.Select(fileText, caretOffset, BudgetChars);
    }

    /// <summary>
    /// 窓口を返す（初回だけリファレンスと設定を読み、索引を作ってログへ出す。失敗したら null）。
    /// </summary>
    private static ContextualApiReference? GetReference()
    {
        lock (Gate)
        {
            if (_loadAttempted) return _reference;
            _loadAttempted = true;
            try
            {
                var path = Locate();
                if (path is null)
                {
                    SEEDEditor.EditorLog.Write($"{LogPrefix} APIリファレンスが見つからない（{RepoRelativePath}）。リファレンスなしで補完する");
                    return null;
                }

                // ① 選び方の設定（無い・壊れているときは組み込み既定）
                var settings = ApiReferenceSettings.LoadFromDir(SEEDEditor.Settings.EditorPaths.ConfigDir);
                foreach (var warning in settings.Warnings) SEEDEditor.EditorLog.Write($"{LogPrefix} {warning}");

                // ② 圧縮 → 節と切れ端 → 索引
                _reference = ContextualApiReference.FromMarkdown(File.ReadAllText(path), settings);
                foreach (var warning in _reference.Warnings) SEEDEditor.EditorLog.Write($"{LogPrefix} {warning}");

                var index = _reference.Index;
                SEEDEditor.EditorLog.Write(
                    $"{LogPrefix} APIリファレンス索引: 節 {index.SectionCount}・切れ端 {index.Parts.Count}・" +
                    $"圧縮後 {index.CompactText.Length:N0} 文字・常に入れる節 {_reference.AlwaysIncludedChars:N0} 文字・" +
                    $"予算 {settings.ResolveBudget(EditorPreferences.Instance.InlineCompletionReferenceChars):N0} 文字" +
                    $"（設定 {settings.SourcePath ?? "(組み込み既定)"}）");
            }
            catch (Exception ex)
            {
                SEEDEditor.EditorLog.Write($"{LogPrefix} APIリファレンス読み込み失敗: {ex.Message}");
                _reference = null;
            }
            return _reference;
        }
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
