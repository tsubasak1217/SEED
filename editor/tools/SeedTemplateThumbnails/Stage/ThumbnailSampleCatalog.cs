// ============================================================
//  ThumbnailSampleCatalog.cs — カタログから thumbnail_sample の欄だけを読む
//
//  【なぜ別に読むのか】
//  エントリの解決（表示名・カテゴリ・サムネイルの場所・requires・2D/3D）はエディタと同じ TemplateActorCatalog に任せる。
//  thumbnail_sample はこのツールだけが使う欄なので、エディタの値オブジェクト（TemplateActorEntry）には足さず、
//  ここでカタログの JSON をもう一度読んで「テンプレートのライブラリ相対パス → 欄の値」の表にする。
//  読み方（コメントと末尾のカンマを許す・パスの正規化）は TemplateActorCatalog と同じ。
// ============================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Templates;
using SEEDEditor.Templates.Actors;

namespace SEEDEditor.Tools.SeedTemplateThumbnails.Stage;

/// <summary>カタログの thumbnail_sample の表。</summary>
public static class ThumbnailSampleCatalog
{
    /// <summary>カタログの JSON の読み方（手で書くデータなのでコメントと末尾のカンマを許す。TemplateActorCatalog と同じ）。</summary>
    private static readonly JsonDocumentOptions CatalogJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// ライブラリのカタログをすべて読み、テンプレートのライブラリ相対パスから thumbnail_sample の値を引く表を作る。
    /// </summary>
    /// <param name="libraryRoot">ライブラリルート。</param>
    /// <param name="warnings">読めなかったカタログの積み先。</param>
    /// <returns>表（欄の無いエントリは載らない）。</returns>
    public static IReadOnlyDictionary<string, JsonNode> Load(string libraryRoot, List<string> warnings)
    {
        var map = new Dictionary<string, JsonNode>(AssetPathUtil.PathComparer);
        foreach (var folder in Directory.EnumerateDirectories(libraryRoot))
        {
            var folderName = Path.GetFileName(folder);
            var catalogPath = Path.Combine(folder, TemplateLibraryMetadata.TemplateActorCatalogFileName);
            if (!File.Exists(catalogPath)) continue;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(catalogPath), documentOptions: CatalogJsonOptions) as JsonObject;
                if (root?[TemplateActorCatalogFormat.EntriesKey] is not JsonArray entries) continue;
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    if (entry[TemplateActorCatalogFormat.PathKey] is not JsonValue pv || !pv.TryGetValue<string>(out var path)) continue;
                    if (entry[ThumbnailSample.Key] is not { } sample) continue;
                    var rel = AssetPathUtil.NormalizeRelative(folderName + "/" + path);
                    map[rel] = sample.DeepClone();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{folderName}/{TemplateLibraryMetadata.TemplateActorCatalogFileName}: 読めませんでした（{ex.Message}）");
            }
        }
        return map;
    }
}
