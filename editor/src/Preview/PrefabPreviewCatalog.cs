// ============================================================
//  PrefabPreviewCatalog.cs — プレビューに使えるプレハブの一覧と検索・並び
//
//  【役割】
//  プレハブを選ぶ窓（PrefabPreviewPickerWindow）の一覧の中身を作る。
//    - Scan   … アセットルートの下の .actor / .actor2d を列挙する
//               （'.' で始まるフォルダ〈.backup の世代バックアップなど〉は飛ばす）
//    - Filter … 検索（大小文字を区別しない部分一致。空白区切りは「全部を含む」）
//    - Order  … 並び（最近使ったものを先頭に最近の順、残りは仮想パスの順）
//  1 件は assets:// の仮想パス（区切りは '/'）・表示名（ファイル名）・フォルダ（アセットルートからの相対）を持つ。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Assets;

namespace SEEDEditor.Preview;

/// <summary>
/// 一覧の 1 件（プレハブ 1 つ）。
/// </summary>
/// <param name="VirtualPath">assets:// の仮想パス（区切りは '/'）。</param>
/// <param name="DisplayName">表示名（ファイル名。拡張子つき）。</param>
/// <param name="Folder">アセットルートからのフォルダ（区切りは '/'。直下なら空）。</param>
/// <param name="AbsolutePath">ファイルの絶対パス。</param>
public sealed record PrefabPreviewItem(string VirtualPath, string DisplayName, string Folder, string AbsolutePath);

/// <summary>
/// 並べた一覧（最近使ったもの → 残り）。
/// </summary>
/// <param name="Recent">最近使ったもの（最近の順。一覧に無いものは入らない）。</param>
/// <param name="Others">残り（仮想パスの順）。</param>
public sealed record PrefabPreviewOrdering(IReadOnlyList<PrefabPreviewItem> Recent, IReadOnlyList<PrefabPreviewItem> Others)
{
    /// <summary>全件数。</summary>
    public int Count => Recent.Count + Others.Count;
}

/// <summary>
/// プレハブの一覧の列挙・検索・並び。状態を持たない。
/// </summary>
public static class PrefabPreviewCatalog
{
    /// <summary>プレハブの拡張子（大小文字は区別しない）。</summary>
    public static readonly IReadOnlyList<string> Extensions = [".actor", ".actor2d"];

    /// <summary>飛ばすフォルダの名前の頭（.backup・.git など）。</summary>
    private const char HiddenFolderPrefix = '.';

    /// <summary>
    /// アセットルートの下のプレハブを列挙する（'.' で始まるフォルダは飛ばす。読めないフォルダも飛ばす）。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <returns>プレハブの一覧（仮想パスの順）。ルートが無ければ空。</returns>
    public static IReadOnlyList<PrefabPreviewItem> Scan(string? assetsRoot)
    {
        var items = new List<PrefabPreviewItem>();
        if (string.IsNullOrWhiteSpace(assetsRoot) || !Directory.Exists(assetsRoot)) return items;

        var root    = Path.GetFullPath(assetsRoot);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            // ── このフォルダのプレハブ ──
            foreach (var file in SafeEnumerate(() => Directory.EnumerateFiles(dir)))
            {
                if (!IsPrefabFile(file)) continue;
                if (CreateItem(root, file) is { } item) items.Add(item);
            }

            // ── 子のフォルダ（'.' で始まるものは飛ばす）──
            foreach (var sub in SafeEnumerate(() => Directory.EnumerateDirectories(dir)))
            {
                var name = Path.GetFileName(sub);
                if (name.Length == 0 || name[0] == HiddenFolderPrefix) continue;
                pending.Push(sub);
            }
        }

        items.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.VirtualPath, b.VirtualPath));
        return items;
    }

    /// <summary>ファイルがプレハブ（.actor / .actor2d）か。</summary>
    /// <param name="path">ファイルのパス。</param>
    /// <returns>プレハブなら true。</returns>
    public static bool IsPrefabFile(string path)
    {
        var ext = Path.GetExtension(path);
        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 1 件を作る（アセットルートの外なら null）。
    /// </summary>
    /// <param name="assetsRoot">アセットルートの絶対パス。</param>
    /// <param name="absolutePath">ファイルの絶対パス。</param>
    /// <returns>1 件、または null。</returns>
    public static PrefabPreviewItem? CreateItem(string assetsRoot, string absolutePath)
    {
        var relative = AssetUriPath.ToRelative(assetsRoot, absolutePath);
        if (string.IsNullOrEmpty(relative)) return null;

        var slash  = relative.LastIndexOf(AssetUriPath.Separator);
        var folder = slash >= 0 ? relative[..slash] : "";
        var name   = slash >= 0 ? relative[(slash + 1)..] : relative;
        return new PrefabPreviewItem(AssetUriPath.Scheme + relative, name, folder, Path.GetFullPath(absolutePath));
    }

    // ============================================================
    //  検索
    // ============================================================

    /// <summary>
    /// 検索の文字を語に分ける（空白〈全角の空白も〉で区切る。空なら語なし）。
    /// </summary>
    /// <param name="query">検索欄の文字。</param>
    /// <returns>語の並び。</returns>
    public static IReadOnlyList<string> SplitTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 1 件が語の全部を含むか（仮想パス〈フォルダとファイル名〉に対する大小文字を区別しない部分一致）。
    /// </summary>
    /// <param name="item">1 件。</param>
    /// <param name="terms">語の並び（空なら常に true）。</param>
    /// <returns>全部を含むなら true。</returns>
    public static bool Matches(PrefabPreviewItem item, IReadOnlyList<string> terms) =>
        terms.All(t => item.VirtualPath.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 検索の文字で絞り込む。
    /// </summary>
    /// <param name="items">一覧。</param>
    /// <param name="query">検索欄の文字。</param>
    /// <returns>当たった件（元の順）。</returns>
    public static IReadOnlyList<PrefabPreviewItem> Filter(IEnumerable<PrefabPreviewItem> items, string? query)
    {
        var terms = SplitTerms(query);
        return items.Where(i => Matches(i, terms)).ToList();
    }

    // ============================================================
    //  並び
    // ============================================================

    /// <summary>
    /// 最近使ったものを先頭に最近の順、残りを仮想パスの順に並べる（同じものは 2 度出さない）。
    /// </summary>
    /// <param name="items">一覧（絞り込んだ後でよい）。</param>
    /// <param name="recent">最近使ったプレハブの仮想パス（最近の順）。</param>
    /// <returns>並べた一覧。</returns>
    public static PrefabPreviewOrdering Order(IEnumerable<PrefabPreviewItem> items, IReadOnlyList<string>? recent)
    {
        var byPath = new Dictionary<string, PrefabPreviewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) byPath.TryAdd(item.VirtualPath, item);

        // 最近の順に、一覧にあるものだけを拾う
        var recentItems = new List<PrefabPreviewItem>();
        var taken       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in recent ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !byPath.TryGetValue(path, out var hit) || !taken.Add(hit.VirtualPath))
                continue;
            recentItems.Add(hit);
        }

        var others = byPath.Values
            .Where(i => !taken.Contains(i.VirtualPath))
            .OrderBy(i => i.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PrefabPreviewOrdering(recentItems, others);
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>
    /// フォルダの列挙を、読めないフォルダ（権限・途中で消えた）で止めずに空として返す。
    /// </summary>
    /// <param name="enumerate">列挙。</param>
    /// <returns>列挙の結果（失敗したら空）。</returns>
    private static IReadOnlyList<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
    {
        try { return enumerate().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
