// ============================================================
//  TemplateActorCatalog.cs — テンプレートアクタのカタログの読み込み
//
//  【役割】
//  ライブラリ（templates/）のトップレベルフォルダを 1 段だけ見て、
//  各フォルダ直下の template_actors.json（カタログ）を読み、
//  「テンプレートアクタの一覧」と「カテゴリの木（2 段）」を作る。
//  コピーもランタイムへの送信もここでは行わない（決定と実行を分ける）。
//
//  【データドリブン】
//  カテゴリ名・表示名・説明・検索語・並びはすべてカタログ（データ）が持つ。
//  テンプレートを 1 つ足すときはカタログに 1 件足すだけでよく、コードは触らない。
//  カタログに載っていない .actor は一覧に出さない（見本の画面や内部の部品を隠すため）。
//
//  【壊れたデータへの態度】
//  1 件の書き損じで一覧全体が出なくなると困るので、エントリ単位で飛ばして
//  理由を Warnings に積む（画面の状態の行とエディタのログに出す）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタのカタログ（全カタログファイルを束ねた読み込み結果）。
/// </summary>
public sealed class TemplateActorCatalog
{
    /// <summary>カタログの JSON を読むときの寛容さ（手で書くデータなのでコメントと末尾のカンマを許す）。</summary>
    private static readonly JsonDocumentOptions CatalogJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling     = JsonCommentHandling.Skip,
    };

    /// <summary>テンプレート（.actor）の JSON を読むときの設定（ランタイムの serde_json と同じく厳密）。</summary>
    private static readonly JsonDocumentOptions TemplateJsonOptions = new();

    /// <summary>ライブラリ内のパスの区切り（ライブラリ相対パスは常に '/' 区切り）。</summary>
    private const char RelPathSeparator = '/';

    /// <summary>ライブラリルートの絶対パス。</summary>
    public string LibraryRoot { get; }

    /// <summary>テンプレートアクタ（カテゴリの並び → カタログに書いた順）。</summary>
    public IReadOnlyList<TemplateActorEntry> Entries { get; }

    /// <summary>カテゴリ（木の並び: 1 段目 → その子の 2 段目 → 次の 1 段目 …）。</summary>
    public IReadOnlyList<TemplateActorCategory> Categories { get; }

    /// <summary>読み込み中に見つかった書き損じ・欠けたファイル（利用者向けの文）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>エントリが 1 件も無ければ true。</summary>
    public bool IsEmpty => Entries.Count == 0;

    /// <summary>読み込み結果からカタログを作る（生成は <see cref="Load"/> から行う）。</summary>
    private TemplateActorCatalog(
        string libraryRoot,
        IReadOnlyList<TemplateActorEntry> entries,
        IReadOnlyList<TemplateActorCategory> categories,
        IReadOnlyList<string> warnings)
    {
        LibraryRoot = libraryRoot;
        Entries     = entries;
        Categories  = categories;
        Warnings    = warnings;
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>
    /// ライブラリのトップレベルフォルダからカタログを探して読み込む。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルート（templates/）の絶対パス。</param>
    /// <returns>
    /// 読み込み結果。ライブラリが無い・カタログが 1 つも無い場合は空のカタログ
    /// （例外にしないのは、画面側が「テンプレートアクタがありません」を出せれば十分なため）。
    /// </returns>
    public static TemplateActorCatalog Load(string libraryRoot)
    {
        var fullRoot = Path.GetFullPath(libraryRoot);
        var warnings = new List<string>();
        var files    = new List<CatalogFile>();

        if (Directory.Exists(fullRoot))
        {
            // ── トップレベルフォルダごとにカタログを 1 つ探す ─────────
            foreach (var folder in Directory.EnumerateDirectories(fullRoot))
            {
                var folderName = Path.GetFileName(folder);
                if (folderName.Length == 0 || folderName[0] == '.') continue;   // 隠しフォルダは見ない

                var catalogPath = Path.Combine(folder, TemplateLibraryMetadata.TemplateActorCatalogFileName);
                if (!File.Exists(catalogPath)) continue;

                var file = ReadCatalogFile(fullRoot, folderName, catalogPath, warnings);
                if (file is not null) files.Add(file);
            }
        }

        // ── フォルダ間の並び（sort_order → フォルダ名）──────────────
        files.Sort((a, b) =>
        {
            int bySort = a.SortOrder.CompareTo(b.SortOrder);
            return bySort != 0
                ? bySort
                : string.Compare(a.FolderName, b.FolderName, StringComparison.OrdinalIgnoreCase);
        });

        // ── エントリを解決する（重複パスは最初の 1 件だけ採る）─────────
        var entries = new List<TemplateActorEntry>();
        var seen    = new HashSet<string>(AssetPathUtil.PathComparer);
        foreach (var file in files)
        {
            for (int i = 0; i < file.RawEntries.Count; i++)
            {
                var entry = ResolveEntry(fullRoot, file, i, file.RawEntries[i], warnings);
                if (entry is null) continue;
                if (!seen.Add(entry.TemplateRelPath))
                {
                    warnings.Add($"{file.CatalogRelPath}: 同じテンプレートが 2 度載っています（後の方を無視）: {entry.TemplateRelPath}");
                    continue;
                }
                entries.Add(entry);
            }
        }

        // ── カテゴリの木を作り、エントリをカテゴリの並びへ揃える ───────
        var categories = BuildCategories(entries);
        var order      = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < categories.Count; i++) order[categories[i].Key] = i;
        var sorted = entries
            .Select((e, index) => (Entry: e, Index: index))
            .OrderBy(x => order.TryGetValue(x.Entry.CategoryKey, out var o) ? o : int.MaxValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Entry)
            .ToList();

        return new TemplateActorCatalog(fullRoot, sorted, categories, warnings);
    }

    // ============================================================
    //  カタログファイル 1 つ
    // ============================================================

    /// <summary>カタログファイル 1 つを読んだ中間結果（エントリはまだ生の JSON）。</summary>
    /// <param name="FolderName">カタログを置いたトップレベルフォルダ名（例 "ui"）。</param>
    /// <param name="CatalogRelPath">カタログのライブラリ相対パス（警告の出どころ）。</param>
    /// <param name="SortOrder">フォルダ間の並び順。</param>
    /// <param name="RawEntries">entries 配列の各要素（複製済み。元の JsonDocument は破棄してよい）。</param>
    private sealed record CatalogFile(
        string FolderName,
        string CatalogRelPath,
        int SortOrder,
        IReadOnlyList<JsonElement> RawEntries);

    /// <summary>
    /// カタログファイルを読み、版・並び順・エントリ配列を取り出す。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="folderName">カタログを置いたトップレベルフォルダ名。</param>
    /// <param name="catalogPath">カタログファイルの絶対パス。</param>
    /// <param name="warnings">警告の積み先。</param>
    /// <returns>読めたら中間結果、読めなければ null（理由は警告へ）。</returns>
    private static CatalogFile? ReadCatalogFile(
        string libraryRoot, string folderName, string catalogPath, List<string> warnings)
    {
        var catalogRel = folderName + RelPathSeparator + TemplateLibraryMetadata.TemplateActorCatalogFileName;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogPath), CatalogJsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{catalogRel}: 先頭が JSON のオブジェクトではありません（読みません）");
                return null;
            }

            // ── 版（未来の版は読まない。黙って読めるところだけ読むことはしない）──
            int version = TryGetInt(root, TemplateActorCatalogFormat.FormatVersionKey)
                          ?? TemplateActorCatalogFormat.SupportedFormatVersion;
            if (version > TemplateActorCatalogFormat.SupportedFormatVersion)
            {
                warnings.Add($"{catalogRel}: このエディタより新しい版（{version}）のカタログです。エディタを更新してください（読みません）");
                return null;
            }

            int sortOrder = TryGetInt(root, TemplateActorCatalogFormat.SortOrderKey)
                            ?? TemplateActorCatalogFormat.DefaultSortOrder;

            var raw = new List<JsonElement>();
            if (root.TryGetProperty(TemplateActorCatalogFormat.EntriesKey, out var entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                // JsonDocument の破棄後も使えるよう、各要素を複製して持ち出す
                foreach (var e in entries.EnumerateArray()) raw.Add(e.Clone());
            }
            else
            {
                warnings.Add($"{catalogRel}: \"{TemplateActorCatalogFormat.EntriesKey}\" の配列がありません");
            }

            return new CatalogFile(folderName, catalogRel, sortOrder, raw);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{catalogRel}: 読めませんでした（{ex.Message}）");
            return null;
        }
    }

    // ============================================================
    //  エントリ 1 件
    // ============================================================

    /// <summary>
    /// entries の 1 要素を検証してエントリにする。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルートの絶対パス。</param>
    /// <param name="file">要素を持つカタログ。</param>
    /// <param name="index">entries 配列の中の位置（警告の出どころ表示用）。</param>
    /// <param name="raw">要素の JSON。</param>
    /// <param name="warnings">警告の積み先。</param>
    /// <returns>エントリ。使えない要素なら null（理由は警告へ）。</returns>
    private static TemplateActorEntry? ResolveEntry(
        string libraryRoot, CatalogFile file, int index, JsonElement raw, List<string> warnings)
    {
        var where = $"{file.CatalogRelPath}: entries[{index}]";
        if (raw.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"{where} がオブジェクトではありません");
            return null;
        }

        // ── テンプレートの場所（必須）──────────────────────────
        var templateRel = ToLibraryRelative(file.FolderName, GetString(raw, TemplateActorCatalogFormat.PathKey));
        if (templateRel is null)
        {
            warnings.Add($"{where}: \"{TemplateActorCatalogFormat.PathKey}\" がありません（またはライブラリの外を指しています）");
            return null;
        }
        var ext = AssetPathUtil.GetExtensionLower(templateRel);
        if (!TemplateActorCatalogFormat.TemplateExtensions.Contains(ext))
        {
            warnings.Add($"{where}: .actor / .actor2d ではありません: {templateRel}");
            return null;
        }
        var templateAbs = AssetPathUtil.ToAbsolute(libraryRoot, templateRel);
        if (!File.Exists(templateAbs))
        {
            warnings.Add($"{where}: テンプレートが見つかりません: {templateRel}");
            return null;
        }

        // ── 2D / 3D の別（テンプレートのルートの actor_kind から）──────
        bool? is2D = ReadIs2D(templateAbs);
        if (is2D is null)
        {
            warnings.Add($"{where}: テンプレートを JSON として読めません: {templateRel}");
            return null;
        }

        // ── 表示に使う欄（無ければ既定値）─────────────────────────
        var name = GetString(raw, TemplateActorCatalogFormat.NameKey).Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(templateRel);
        var description = GetString(raw, TemplateActorCatalogFormat.DescriptionKey).Trim();
        var segments    = SplitCategory(GetString(raw, TemplateActorCatalogFormat.CategoryKey), where, warnings);
        var tags        = GetStringArray(raw, TemplateActorCatalogFormat.TagsKey);

        // ── サムネイル（明示が無ければ規約の場所。実在は問わない）─────
        var thumbExplicitRaw = GetString(raw, TemplateActorCatalogFormat.ThumbnailKey).Trim();
        string thumbnailRel;
        bool   thumbnailExplicit = thumbExplicitRaw.Length > 0;
        if (thumbnailExplicit)
        {
            thumbnailRel = ToLibraryRelative(file.FolderName, thumbExplicitRaw) ?? "";
            if (thumbnailRel.Length == 0 || !File.Exists(AssetPathUtil.ToAbsolute(libraryRoot, thumbnailRel)))
                warnings.Add($"{where}: サムネイルが見つかりません（頭文字の板を出します）: {thumbExplicitRaw}");
        }
        else
        {
            thumbnailRel = DefaultThumbnailRelPath(file.FolderName, templateRel);
        }

        // ── 一緒にコピーするファイル（実在しなければ警告だけ。追加時にも欠落として出る）──
        var requires = new List<string>();
        foreach (var r in GetStringArray(raw, TemplateActorCatalogFormat.RequiresKey))
        {
            var rel = ToLibraryRelative(file.FolderName, r);
            if (rel is null) { warnings.Add($"{where}: requires の値がライブラリの外を指しています: {r}"); continue; }
            if (!File.Exists(AssetPathUtil.ToAbsolute(libraryRoot, rel)))
                warnings.Add($"{where}: requires のファイルが見つかりません: {rel}");
            requires.Add(rel);
        }

        return new TemplateActorEntry(
            Name:              name,
            Description:       description,
            CategoryKey:       string.Join(TemplateActorCatalogFormat.CategorySeparator, segments),
            CategorySegments:  segments,
            Tags:              tags,
            TemplateRelPath:   templateRel,
            ThumbnailRelPath:  thumbnailRel,
            ThumbnailExplicit: thumbnailExplicit,
            RequiredRelPaths:  requires,
            Is2D:              is2D.Value,
            CatalogRelPath:    file.CatalogRelPath);
    }

    /// <summary>
    /// サムネイルの既定の場所（規約）を組み立てる:
    /// <c>&lt;カタログのフォルダ&gt;/thumbnails/&lt;テンプレートのファイル名（拡張子なし）&gt;.png</c>。
    /// </summary>
    /// <param name="folderName">カタログのフォルダ名（例 "ui"）。</param>
    /// <param name="templateRel">テンプレートのライブラリ相対パス（例 "ui/prefabs/button.actor"）。</param>
    /// <returns>サムネイルのライブラリ相対パス（例 "ui/thumbnails/button.png"）。</returns>
    public static string DefaultThumbnailRelPath(string folderName, string templateRel) =>
        folderName + RelPathSeparator + TemplateLibraryMetadata.ThumbnailFolderName + RelPathSeparator
        + Path.GetFileNameWithoutExtension(AssetPathUtil.GetFileName(templateRel))
        + TemplateActorCatalogFormat.ThumbnailExtension;

    /// <summary>
    /// カタログのフォルダ基準の相対パスを、ライブラリ相対パスへ変換する。
    /// </summary>
    /// <param name="folderName">カタログのフォルダ名。</param>
    /// <param name="relToFolder">カタログのフォルダ基準の相対パス。</param>
    /// <returns>ライブラリ相対パス。空・フォルダの外へ出るパスなら null。</returns>
    private static string? ToLibraryRelative(string folderName, string relToFolder)
    {
        if (string.IsNullOrWhiteSpace(relToFolder)) return null;
        // "../" でカタログのフォルダの外へ出るもの（別カテゴリの中身）も、
        // ライブラリの中に留まるなら受け付ける（例 "../textures/x.png" → "textures/x.png"）。
        // ライブラリルートより上へ出るパスは CollapseDotSegments が空文字にするので弾ける。
        var combined = AssetPathUtil.NormalizeRelative(folderName + RelPathSeparator + relToFolder);
        return combined.Length == 0 ? null : combined;
    }

    /// <summary>
    /// テンプレートのルートの actor_kind を読み、2D かどうかを返す。
    /// </summary>
    /// <param name="templateAbs">テンプレートの絶対パス。</param>
    /// <returns>2D なら true、3D なら false、JSON として読めなければ null。</returns>
    private static bool? ReadIs2D(string templateAbs)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(templateAbs), TemplateJsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(TemplateActorCatalogFormat.ActorKindKey, out var kind)
                   && kind.ValueKind == JsonValueKind.String
                   && string.Equals(kind.GetString(), TemplateActorCatalogFormat.ActorKind2D, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ============================================================
    //  カテゴリ
    // ============================================================

    /// <summary>
    /// カテゴリの文字列を段に分ける（空の段を落とし、深すぎる段は最後の段へまとめる）。
    /// </summary>
    /// <param name="raw">カタログの category 欄。</param>
    /// <param name="where">警告の出どころ。</param>
    /// <param name="warnings">警告の積み先。</param>
    /// <returns>1〜<see cref="TemplateActorCatalogFormat.MaxCategoryDepth"/> 段。</returns>
    public static IReadOnlyList<string> SplitCategory(string raw, string where, List<string> warnings)
    {
        var parts = raw.Split(TemplateActorCatalogFormat.CategorySeparator)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (parts.Count == 0) return [TemplateActorCatalogFormat.UncategorizedName];
        if (parts.Count <= TemplateActorCatalogFormat.MaxCategoryDepth) return parts;

        // 深すぎる分は最後の段の名前へ畳む（木は 2 段に保つ）
        warnings.Add($"{where}: カテゴリは {TemplateActorCatalogFormat.MaxCategoryDepth} 段までです（{raw}）");
        int keep   = TemplateActorCatalogFormat.MaxCategoryDepth - 1;
        var merged = parts.Take(keep).ToList();
        merged.Add(string.Join(TemplateActorCatalogFormat.CategoryDisplaySeparator, parts.Skip(keep)));
        return merged;
    }

    /// <summary>
    /// エントリに現れた順でカテゴリの木（1 段目 → その子）を作る。
    /// </summary>
    /// <param name="entries">エントリ（カタログの並び順）。</param>
    /// <returns>木の並びのカテゴリ一覧。</returns>
    private static List<TemplateActorCategory> BuildCategories(IReadOnlyList<TemplateActorEntry> entries)
    {
        // 1 段目の出現順と、その下の 2 段目の出現順を別々に覚える
        var topOrder = new List<string>();
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var top = e.CategorySegments[0];
            if (!children.ContainsKey(top))
            {
                children[top] = [];
                topOrder.Add(top);
            }
            if (e.CategorySegments.Count > 1 && !children[top].Contains(e.CategorySegments[1]))
                children[top].Add(e.CategorySegments[1]);
        }

        var result = new List<TemplateActorCategory>();
        foreach (var top in topOrder)
        {
            result.Add(new TemplateActorCategory(top, top, ParentKey: null, Depth: 1));
            foreach (var child in children[top])
            {
                var key = top + TemplateActorCatalogFormat.CategorySeparator + child;
                result.Add(new TemplateActorCategory(key, child, ParentKey: top, Depth: 2));
            }
        }
        return result;
    }

    // ============================================================
    //  JSON の小道具
    // ============================================================

    /// <summary>文字列の欄を読む（無い・文字列でないなら空文字）。</summary>
    private static string GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>文字列の配列の欄を読む（文字列でない要素・空文字は落とす）。</summary>
    private static List<string> GetStringArray(JsonElement obj, string key)
    {
        var list = new List<string>();
        if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString()?.Trim() ?? "";
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    /// <summary>整数の欄を読む（無い・整数でないなら null）。</summary>
    private static int? TryGetInt(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;
}
