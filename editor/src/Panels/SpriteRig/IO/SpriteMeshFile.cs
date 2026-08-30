using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Panels.SpriteRig.Mesh;

namespace SEEDEditor.Panels.SpriteRig.IO;

/// <summary>
/// <c>.sprite_mesh</c>（2D メッシュ変形スキニングの形状アセット）の読み書き。
///
/// 形式の正典は <c>docs/sprite_skinning.md</c> §2 と
/// <c>runtime/src/engine/core/loader/sprite_mesh.rs</c>（実際のパーサ）である。
/// ここで書き出す JSON は必ずそのパーサの検証をすべて通る内容にする:
///   - <c>vertices</c> / <c>uvs</c> / <c>weights</c> の要素数が一致し、空でない
///   - <c>triangles</c> は 3 の倍数・全インデックスが頂点範囲内
///   - <c>bones</c> は 1 本以上・名前が空でなく重複しない
///   - 各頂点のウェイトは 1〜4 本・非負・合計が正
///
/// <c>texture</c> はエディタが再編集時に元画像を引き当てるためのヒントで、
/// ランタイム側では省略可能フィールド（既定値 空文字列）として無視される。
/// パスは <c>.sprite_mesh</c> ファイルからの相対パスで保存する（プロジェクトを移動しても壊れない）。
/// </summary>
public static class SpriteMeshFile
{
    /// <summary>書き出す <c>.sprite_mesh</c> のスキーマバージョン。</summary>
    public const int SchemaVersion = 1;

    /// <summary><c>.sprite_mesh</c> の拡張子（ドット付き）。</summary>
    public const string Extension = ".sprite_mesh";

    /// <summary>1 頂点が持てるボーン影響の最大本数（ランタイムの MAX_BONE_INFLUENCES と一致）。</summary>
    public const int MaxBoneInfluences = 4;

    /// <summary>JSON の入出力設定（読みやすい整形 + 非 ASCII をエスケープしない）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 読み込み結果（メッシュ本体 + ファイルに書かれていた付帯情報）。
    /// </summary>
    /// <param name="Mesh">復元された編集用メッシュ。</param>
    /// <param name="Name">アセット名（空の場合あり）。</param>
    /// <param name="Comment">制作者向けメモ（空の場合あり）。</param>
    /// <param name="TextureHint">
    /// <c>texture</c> フィールドを解決した絶対パス。フィールドが無い／実在しない場合は null。
    /// </param>
    public sealed record LoadResult(SpriteRigMesh Mesh, string Name, string Comment, string? TextureHint);

    // ============================================================
    //  書き出し
    // ============================================================

    /// <summary>
    /// メッシュを <c>.sprite_mesh</c> として保存する。
    /// </summary>
    /// <param name="path">保存先の絶対パス。</param>
    /// <param name="mesh">保存するメッシュ（三角形が 1 枚以上必要）。</param>
    /// <param name="imageWidth">UV 計算に使う元画像の横幅（ピクセル）。</param>
    /// <param name="imageHeight">UV 計算に使う元画像の高さ（ピクセル）。</param>
    /// <param name="texturePath">元画像の絶対パス（null なら texture を書かない）。</param>
    /// <param name="name">アセット名（空なら拡張子を除いたファイル名を使う）。</param>
    /// <param name="comment">制作者向けメモ。</param>
    /// <exception cref="InvalidOperationException">メッシュがランタイムの検証を通らない場合。</exception>
    public static void Save(
        string path,
        SpriteRigMesh mesh,
        int imageWidth,
        int imageHeight,
        string? texturePath,
        string name = "",
        string comment = "")
    {
        string json = Serialize(mesh, imageWidth, imageHeight,
            texturePath == null ? null : MakeRelativeTexturePath(path, texturePath),
            string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name,
            comment);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// メッシュを <c>.sprite_mesh</c> の JSON 文字列へ直列化する（ファイル I/O 抜き。テスト用にも使う）。
    /// </summary>
    /// <param name="mesh">対象メッシュ。</param>
    /// <param name="imageWidth">UV 計算に使う元画像の横幅。</param>
    /// <param name="imageHeight">UV 計算に使う元画像の高さ。</param>
    /// <param name="relativeTexturePath">texture フィールドに書く相対パス（null なら省略）。</param>
    /// <param name="name">アセット名。</param>
    /// <param name="comment">制作者向けメモ。</param>
    public static string Serialize(
        SpriteRigMesh mesh,
        int imageWidth,
        int imageHeight,
        string? relativeTexturePath,
        string name,
        string comment)
    {
        Validate(mesh);

        var data = new SpriteMeshDto
        {
            Version = SchemaVersion,
            Name = name,
            Comment = comment,
            Texture = relativeTexturePath ?? string.Empty,
            Vertices = new List<double[]>(mesh.Vertices.Count),
            Uvs = new List<double[]>(mesh.Vertices.Count),
            Triangles = new List<int>(mesh.Triangles),
            Bones = new List<SpriteMeshBoneDto>(mesh.Bones.Count),
            Weights = new List<List<SpriteMeshInfluenceDto>>(mesh.Vertices.Count),
        };

        // ── 頂点と UV ──
        // UV は画像サイズで割った [0,1]^2・左上原点。丸め誤差で 1 を超えないようクランプする。
        double invWidth = imageWidth > 0 ? 1.0 / imageWidth : 0.0;
        double invHeight = imageHeight > 0 ? 1.0 / imageHeight : 0.0;
        foreach (var v in mesh.Vertices)
        {
            data.Vertices.Add(new[] { v.X, v.Y });
            data.Uvs.Add(new[]
            {
                Math.Clamp(v.X * invWidth, 0.0, 1.0),
                Math.Clamp(v.Y * invHeight, 0.0, 1.0),
            });
        }

        // ── ボーン ──
        foreach (var bone in mesh.Bones)
        {
            data.Bones.Add(new SpriteMeshBoneDto
            {
                Name = bone.Name,
                Parent = bone.Parent,
                Position = new[] { bone.Position.X, bone.Position.Y },
                Rotation = bone.Rotation,
                Scale = new[] { bone.Scale.X, bone.Scale.Y },
                Length = bone.Length,
            });
        }

        // ── ウェイト ──
        // 影響が無い／多すぎる頂点はここで矯正し、パーサが必ず受理する形にする。
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var influences = i < mesh.Weights.Count ? mesh.Weights[i] : null;
            data.Weights.Add(NormalizeInfluences(influences, mesh.Bones.Count));
        }

        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>
    /// 1 頂点ぶんの影響を、ランタイムが受理する形（1〜4 本・非負・合計が正）へ整える。
    /// </summary>
    /// <param name="influences">元の影響一覧（null 可）。</param>
    /// <param name="boneCount">ボーン総数（範囲外の添字を弾くため）。</param>
    private static List<SpriteMeshInfluenceDto> NormalizeInfluences(
        List<SpriteRigInfluence>? influences, int boneCount)
    {
        // ボーン範囲外の影響だけここで落とし、
        // 「4 本まで・正の値・合計 1.0」の整形は編集側と同じ規則（WeightPaint）に任せる。
        var valid = new List<SpriteRigInfluence>(MaxBoneInfluences);
        if (influences != null)
        {
            foreach (var influence in influences)
            {
                if (influence.BoneIndex < 0 || influence.BoneIndex >= boneCount) continue;
                valid.Add(influence);
            }
        }

        var normalized = WeightPaint.Normalize(valid);
        var result = new List<SpriteMeshInfluenceDto>(normalized.Count);
        foreach (var influence in normalized)
        {
            result.Add(new SpriteMeshInfluenceDto
            {
                Bone = influence.BoneIndex,
                Weight = influence.Weight,
            });
        }
        return result;
    }

    /// <summary>
    /// 保存前の検証。ランタイムのパーサが弾く条件をここで先に検出し、
    /// 「保存はできたが読めないファイル」を作らないようにする。
    /// </summary>
    /// <param name="mesh">検証するメッシュ。</param>
    /// <exception cref="InvalidOperationException">保存できない状態のとき。</exception>
    public static void Validate(SpriteRigMesh mesh)
    {
        if (mesh.Vertices.Count == 0)
            throw new InvalidOperationException("頂点が 1 つもありません。メッシュを作成してから保存してください。");
        if (mesh.Triangles.Count == 0 || mesh.Triangles.Count % Triangulation.IndicesPerTriangle != 0)
            throw new InvalidOperationException("三角形がありません（または 3 の倍数になっていません）。");

        foreach (int index in mesh.Triangles)
        {
            if (index < 0 || index >= mesh.Vertices.Count)
                throw new InvalidOperationException($"三角形が範囲外の頂点 {index} を参照しています。");
        }

        if (mesh.Bones.Count == 0)
            throw new InvalidOperationException("ボーンが 1 本もありません。");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in mesh.Bones)
        {
            if (string.IsNullOrEmpty(bone.Name))
                throw new InvalidOperationException("名前が空のボーンがあります。");
            if (!names.Add(bone.Name))
                throw new InvalidOperationException($"ボーン名 '{bone.Name}' が重複しています。");
        }
    }

    // ============================================================
    //  読み込み
    // ============================================================

    /// <summary>
    /// <c>.sprite_mesh</c> を読み込み、編集用メッシュへ復元する。
    ///
    /// ファイルには頂点と三角形しか無いため、輪郭ポリゴンと内部点は
    /// <see cref="MeshTopology.ExtractBoundary"/> で復元する。
    /// 三角形は<b>ファイルのものをそのまま保持</b>し、再三角分割はしない
    /// （開いただけで形が変わってしまうのを避けるため）。
    /// </summary>
    /// <param name="path">読み込む <c>.sprite_mesh</c> の絶対パス。</param>
    public static LoadResult Load(string path)
    {
        string json = File.ReadAllText(path);
        return Deserialize(json, Path.GetDirectoryName(path));
    }

    /// <summary>
    /// JSON 文字列から編集用メッシュを復元する（ファイル I/O 抜き。テスト用にも使う）。
    /// </summary>
    /// <param name="json">.sprite_mesh の JSON 文字列。</param>
    /// <param name="baseDirectory">texture の相対パスを解決する基準ディレクトリ（null 可）。</param>
    public static LoadResult Deserialize(string json, string? baseDirectory)
    {
        var data = JsonSerializer.Deserialize<SpriteMeshDto>(json, JsonOptions)
                   ?? throw new InvalidDataException(".sprite_mesh の内容が空です。");

        if (data.Version != 0 && data.Version != SchemaVersion)
            throw new InvalidDataException($".sprite_mesh の version={data.Version} は未対応です（対応は {SchemaVersion}）。");
        if (data.Vertices.Count == 0)
            throw new InvalidDataException(".sprite_mesh に頂点がありません。");

        var mesh = new SpriteRigMesh();
        foreach (var v in data.Vertices)
        {
            mesh.Vertices.Add(new Vec2(v.Length > 0 ? v[0] : 0.0, v.Length > 1 ? v[1] : 0.0));
        }
        mesh.Triangles.AddRange(data.Triangles);

        // ── ボーン ──
        foreach (var bone in data.Bones)
        {
            mesh.Bones.Add(new SpriteRigBone
            {
                Name = bone.Name,
                Parent = bone.Parent,
                Position = ToVec2(bone.Position, Vec2.Zero),
                Rotation = bone.Rotation,
                Scale = ToVec2(bone.Scale, new Vec2(1.0, 1.0)),
                Length = bone.Length,
            });
        }
        mesh.EnsureRootBone();

        // ── ウェイト ──
        foreach (var influences in data.Weights)
        {
            var list = new List<SpriteRigInfluence>(influences.Count);
            foreach (var influence in influences) list.Add(new SpriteRigInfluence(influence.Bone, influence.Weight));
            mesh.Weights.Add(list);
        }
        // 頂点数と食い違う場合はルート 1.0 へ張り直す（壊れたファイルの救済）
        if (mesh.Weights.Count != mesh.Vertices.Count) mesh.ResetWeightsToRoot();

        // ── 輪郭・内部点の復元 ──
        var boundary = MeshTopology.ExtractBoundary(mesh.Vertices, mesh.Triangles);
        mesh.Polygons.AddRange(boundary.Polygons);
        mesh.InteriorPoints.AddRange(boundary.InteriorPoints);

        // ── texture ヒントの解決 ──
        string? textureHint = null;
        if (!string.IsNullOrEmpty(data.Texture))
        {
            string candidate = Path.IsPathRooted(data.Texture) || baseDirectory == null
                ? data.Texture
                : Path.GetFullPath(Path.Combine(baseDirectory, data.Texture));
            if (File.Exists(candidate)) textureHint = candidate;
        }

        return new LoadResult(mesh, data.Name, data.Comment, textureHint);
    }

    /// <summary>長さ 2 の配列を <see cref="Vec2"/> へ（欠けていれば既定値）。</summary>
    private static Vec2 ToVec2(double[]? values, Vec2 fallback)
        => values is { Length: >= 2 } ? new Vec2(values[0], values[1]) : fallback;

    /// <summary>
    /// テクスチャの絶対パスを、<c>.sprite_mesh</c> からの相対パスへ変換する。
    /// 別ドライブなど相対化できない場合は絶対パスのまま返す。
    /// </summary>
    /// <param name="meshPath">.sprite_mesh の保存先パス。</param>
    /// <param name="texturePath">画像の絶対パス。</param>
    public static string MakeRelativeTexturePath(string meshPath, string texturePath)
    {
        string? meshDirectory = Path.GetDirectoryName(Path.GetFullPath(meshPath));
        if (string.IsNullOrEmpty(meshDirectory)) return texturePath;

        try
        {
            string relative = Path.GetRelativePath(meshDirectory, Path.GetFullPath(texturePath));
            // JSON へは常に '/' 区切りで書く（Windows / それ以外でパスを共有できるようにする）
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return texturePath;
        }
    }

    // ============================================================
    //  JSON DTO（ランタイムの serde 構造体と 1:1 対応）
    // ============================================================

    /// <summary><c>.sprite_mesh</c> ファイル全体。</summary>
    private sealed class SpriteMeshDto
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("comment")] public string Comment { get; set; } = string.Empty;
        [JsonPropertyName("texture")] public string Texture { get; set; } = string.Empty;
        [JsonPropertyName("vertices")] public List<double[]> Vertices { get; set; } = new();
        [JsonPropertyName("uvs")] public List<double[]> Uvs { get; set; } = new();
        [JsonPropertyName("triangles")] public List<int> Triangles { get; set; } = new();
        [JsonPropertyName("bones")] public List<SpriteMeshBoneDto> Bones { get; set; } = new();
        [JsonPropertyName("weights")] public List<List<SpriteMeshInfluenceDto>> Weights { get; set; } = new();
    }

    /// <summary>ボーン宣言（バインドポーズ）。</summary>
    private sealed class SpriteMeshBoneDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("parent")] public string Parent { get; set; } = string.Empty;
        [JsonPropertyName("position")] public double[]? Position { get; set; }
        [JsonPropertyName("rotation")] public double Rotation { get; set; }
        [JsonPropertyName("scale")] public double[]? Scale { get; set; }

        /// <summary>
        /// ボーンの長さ（省略可・既定 0）。オーサリング専用で実行時のスキニングには影響しない。
        /// ランタイム側も <c>#[serde(default)]</c> で受けるため、旧ファイル・新ファイルの
        /// どちらもエディタ／ランタイムの両方で読める。
        /// </summary>
        [JsonPropertyName("length")] public double Length { get; set; }
    }

    /// <summary>1 頂点に対する 1 本ぶんのボーン影響。</summary>
    private sealed class SpriteMeshInfluenceDto
    {
        [JsonPropertyName("bone")] public int Bone { get; set; }
        [JsonPropertyName("weight")] public double Weight { get; set; }
    }
}
