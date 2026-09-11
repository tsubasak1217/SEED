// ============================================================
//  TemplateImportPlan.cs — インポートの計画と結果（値オブジェクト）
//
//  【役割】
//  「何を・どこへコピーするか」「何が既存ファイルと衝突するか」を保持するだけの型。
//  作成は TemplateImporter.CreatePlan、実行は TemplateImporter.Execute が行う。
//
//  【なぜ計画と実行を分けるか】
//   1. ユーザーに「これから何が起きるか」（件数・サイズ・衝突）を先に見せられる。
//   2. コピーという副作用を伴わずに閉包計算だけをテストできる。
//   3. 衝突方針（スキップ / 上書き）を、同じ計画に対して後から選び直せる。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates;

/// <summary>
/// コピー先に同名ファイルが既にあったときの扱い。
/// </summary>
public enum TemplateImportConflictPolicy
{
    /// <summary>既存ファイルを残し、そのファイルのコピーを行わない（既定）。</summary>
    Skip,

    /// <summary>既存ファイルを上書きする。</summary>
    Overwrite,
}

/// <summary>
/// コピー対象のファイル 1 件。
/// </summary>
/// <param name="RelPath">
/// ライブラリルート相対パス。ライブラリはアセットルートと同じ構成なので、
/// コピー先（&lt;ProjectAssets&gt;/…）の相対パスとしてもそのまま使う。
/// </param>
/// <param name="SizeBytes">コピー元のバイトサイズ。</param>
/// <param name="ConflictsWithExisting">コピー先に同名ファイルが既にあるなら true。</param>
public readonly record struct TemplateCopyItem(
    string RelPath,
    long SizeBytes,
    bool ConflictsWithExisting);

/// <summary>
/// インポート計画。選択したエントリと、その依存を含むコピー対象一式を持つ。
/// </summary>
public sealed class TemplateImportPlan
{
    /// <summary>コピー元のライブラリルート（絶対パス）。</summary>
    public required string LibraryRoot { get; init; }

    /// <summary>コピー先のプロジェクトアセットルート（絶対パス）。</summary>
    public required string AssetsRoot { get; init; }

    /// <summary>計画の起点になった選択エントリの相対パス。</summary>
    public IReadOnlyList<string> SelectedEntryPaths { get; init; } = [];

    /// <summary>コピー対象のファイル（相対パスの昇順）。依存を含む。</summary>
    public IReadOnlyList<TemplateCopyItem> Items { get; init; } = [];

    /// <summary>
    /// ライブラリの中で解決できなかった参照。
    /// 「インポートしても参照先が足りない」ことを事前に知らせるために持つ。
    /// </summary>
    public IReadOnlyList<MissingReference> MissingReferences { get; init; } = [];

    /// <summary>コピー対象のファイル数。</summary>
    public int FileCount => Items.Count;

    /// <summary>コピー対象の合計バイト数（衝突してスキップされる分も含む見積り）。</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var i in Items) total += i.SizeBytes;
            return total;
        }
    }

    /// <summary>コピー先に既存ファイルがある対象（衝突）の相対パス一覧。</summary>
    public IReadOnlyList<string> ConflictPaths
    {
        get
        {
            var list = new List<string>();
            foreach (var i in Items) if (i.ConflictsWithExisting) list.Add(i.RelPath);
            return list;
        }
    }

    /// <summary>衝突しているファイル数。</summary>
    public int ConflictCount
    {
        get
        {
            int n = 0;
            foreach (var i in Items) if (i.ConflictsWithExisting) n++;
            return n;
        }
    }
}

/// <summary>
/// インポート実行の結果。
/// </summary>
public sealed class TemplateImportResult
{
    /// <summary>実際にコピーしたファイル数（新規 + 上書き）。</summary>
    public int CopiedCount { get; init; }

    /// <summary>そのうち既存ファイルを上書きした数。</summary>
    public int OverwrittenCount { get; init; }

    /// <summary>衝突のためコピーしなかったファイル数。</summary>
    public int SkippedCount { get; init; }

    /// <summary>コピーしたファイルの合計バイト数。</summary>
    public long CopiedBytes { get; init; }

    /// <summary>コピーに失敗したファイルとその理由。</summary>
    public IReadOnlyList<TemplateCopyFailure> Failures { get; init; } = [];

    /// <summary>計画時点で解決できなかった参照（そのまま引き継ぐ）。</summary>
    public IReadOnlyList<MissingReference> MissingReferences { get; init; } = [];

    /// <summary>1 件でもコピーできたなら true（呼び出し側のファイル一覧更新の判断に使う）。</summary>
    public bool HasCopied => CopiedCount > 0;
}

/// <summary>
/// コピーに失敗した 1 件。
/// </summary>
/// <param name="RelPath">失敗した相対パス。</param>
/// <param name="Message">失敗の理由（例外メッセージ）。</param>
public readonly record struct TemplateCopyFailure(string RelPath, string Message);
