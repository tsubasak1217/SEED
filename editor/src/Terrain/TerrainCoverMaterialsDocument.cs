// ============================================================
//  TerrainCoverMaterialsDocument.cs — cover_materials.json の読み取り
//
//  【責務】
//    地表カバー素材定義（assets/terrain/cover_materials.json）から
//    「素材 ID と表示名の一覧」だけを読み出す。
//    地形ツールバーのカバーブラシ用素材コンボを、ファイルの内容から
//    動的に組み立てるために使う（レイヤ／プロップのコンボとまったく同じ流儀）。
//
//  【なぜ読み取り専用なのか（props.json / layers.json との違い）】
//    あちらはエディタ（地形設定ウィンドウ）が編集して書き戻すため、
//    未知フィールドを失わない JsonNode 差分更新モデルを持っている。
//    カバー素材はランタイム（Rust: terrain/cover/material.rs）と
//    cover_materials.json が正典であり、エディタ側に編集 UI は無い。
//    書き戻さないなら差分更新の仕組みは要らないので、
//    「読むだけ」の最小構成にしてある（実装コストと壊れ方の両方が小さい）。
//
//  【ファイルが無い／壊れている場合】
//    空一覧を返す。呼び出し側（ツールバー）はコンボが空なら素材未選択となり、
//    塗りブラシはランタイム側で「未定義の素材 ID」として弾かれる。
//    ここで既定素材（雪・落ち葉…）を複製すると Rust 側のフォールバック定義と
//    二重管理になるため、あえて持たない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SEEDEditor.Terrain;

// ============================================================
//  TerrainCoverMaterialInfo — 素材 1 件の表示用情報
// ============================================================

/// <summary>
/// cover_materials.json の 1 素材ぶんの表示用情報（ID と名前だけ）。
/// 対応する Rust 型: terrain/cover/material.rs の <c>CoverMaterial</c>。
/// </summary>
internal sealed class TerrainCoverMaterialInfo
{
    /// <summary>素材 ID（IPC へ渡す安定キー）。</summary>
    public string Id { get; init; } = "";

    /// <summary>表示名（コンボに出す。空なら ID を代わりに使う）。</summary>
    public string Name { get; init; } = "";
}

// ============================================================
//  TerrainCoverMaterialsDocument — cover_materials.json 全体
// ============================================================

/// <summary>
/// cover_materials.json の読み取り専用ドキュメント。
/// </summary>
internal sealed class TerrainCoverMaterialsDocument
{
    /// <summary>素材配列の JSON キー。</summary>
    private const string KeyMaterials = "materials";

    /// <summary>素材 ID の JSON キー。</summary>
    private const string KeyId = "id";

    /// <summary>素材表示名の JSON キー。</summary>
    private const string KeyName = "name";

    /// <summary>assets ルートから見た cover_materials.json の相対パス。</summary>
    public const string RelativePath = @"terrain\cover_materials.json";

    /// <summary>読み込んだ素材一覧。並び順がそのまま素材添字（ランタイムの解釈）と一致する。</summary>
    public List<TerrainCoverMaterialInfo> Materials { get; } = new();

    /// <summary>読み込み時にファイルが無かった／壊れていたか（UI での注意表示に使える）。</summary>
    public bool WasMissingOrInvalid { get; private init; }

    private TerrainCoverMaterialsDocument() { }

    /// <summary>
    /// assets ルート配下の cover_materials.json を読み込む。
    /// ファイルが無い／壊れている場合は空一覧を返す（例外は投げない）。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public static TerrainCoverMaterialsDocument Load(string assetsRoot)
    {
        var path = ResolvePath(assetsRoot);
        JsonObject? root = null;
        try
        {
            if (File.Exists(path))
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception)
        {
            // 壊れた JSON で UI が開けなくなるのは割に合わない。空一覧へ倒す。
            root = null;
        }

        if (root is null)
            return new TerrainCoverMaterialsDocument { WasMissingOrInvalid = true };

        var doc = new TerrainCoverMaterialsDocument();
        if (root[KeyMaterials] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var id = ReadString(o, KeyId);
                // ID の無い要素はランタイム側でも参照できないので読み飛ばす。
                if (string.IsNullOrWhiteSpace(id)) continue;
                doc.Materials.Add(new TerrainCoverMaterialInfo
                {
                    Id = id,
                    Name = ReadString(o, KeyName) ?? "",
                });
            }
        }
        return doc;
    }

    /// <summary>assets ルートから cover_materials.json の絶対パスを求める。</summary>
    public static string ResolvePath(string assetsRoot)
        => Path.Combine(assetsRoot, RelativePath);

    /// <summary>
    /// コンボボックスの 1 行を整形する（例: "雪 (snow)"）。
    /// 名前が空なら ID だけを出す。
    /// </summary>
    public static string FormatComboEntry(string name, string id)
        => string.IsNullOrWhiteSpace(name) ? id : $"{name} ({id})";

    /// <summary>文字列キーを読む。存在しない／文字列でない場合は null。</summary>
    private static string? ReadString(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
