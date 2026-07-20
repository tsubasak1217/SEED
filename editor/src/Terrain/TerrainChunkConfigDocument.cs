// ============================================================
//  TerrainChunkConfigDocument.cs — chunk_config.json の読み書き
//
//  【責務】
//    地形のチャンク構成（X×Z チャンク数・1 チャンクあたりのボクセル分割数・
//    ボクセルサイズ）を assets/terrain/chunk_config.json として永続化する
//    ドキュメントモデル。
//
//  【なぜ TerrainLayersDocument（JsonNode 差分更新）ではなく POCO なのか】
//    layers.json は「ランタイムが正典で、エディタが知らないフィールドが
//    増えうる配列」を扱うため JsonNode 差分更新が必要だった。
//    一方こちらは 4 フィールドだけのフラットな設定で、エディタ自身が
//    正典として書き出す小さな値の集合であるため、POCO への
//    Deserialize/Serialize で十分であり、JsonNode 差分更新は過剰設計になる。
//    JSON シリアライズのオプション（WriteIndented・日本語を \uXXXX へ潰さない
//    Encoder）は TerrainLayersDocument と揃えている。
//
//  【ランタイムとの関係】
//    このファイルの値は TERRAIN_INIT / TERRAIN_HEIGHTMAP の新形式引数
//    （chunks_x, chunks_z, chunk_cells, voxel_size）としてそのまま送られる。
//    値の上下限は Rust 側の受理範囲と一致させてあり、Clamp() で必ず範囲内へ
//    丸めてから IPC へ渡す（不正な値を送るとランタイム側でエラー応答になる）。
//    変更が実際に反映されるのは地形を作り直したとき（TERRAIN_INIT /
//    TERRAIN_HEIGHTMAP）だけで、保存しただけでは既存の地形に影響しない。
// ============================================================

using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace SEEDEditor.Terrain;

/// <summary>
/// 地形チャンク構成（chunk_config.json）のドキュメントモデル。
/// ファイルが存在しない／壊れている場合は例外を投げず、既定値を返す
/// （設定ウィンドウが開けなくなる事態を避けるため）。
/// </summary>
internal sealed class TerrainChunkConfigDocument
{
    // ── 既定値（Rust 側の現行固定値と一致させる）────────────────

    /// <summary>チャンク数 X の既定値。</summary>
    public const int DefaultChunksX = 4;

    /// <summary>チャンク数 Z の既定値。</summary>
    public const int DefaultChunksZ = 4;

    /// <summary>チャンク 1 辺あたりのボクセル分割数の既定値。</summary>
    public const int DefaultChunkCells = 32;

    /// <summary>ボクセルサイズ（m）の既定値。</summary>
    public const double DefaultVoxelSize = 0.5;

    // ── 値域（IPC TERRAIN_INIT / TERRAIN_HEIGHTMAP の受理範囲と一致させる）──

    /// <summary>チャンク数（X・Z 共通）の下限。</summary>
    public const int ChunksAxisMin = 1;

    /// <summary>チャンク数（X・Z 共通）の上限。</summary>
    public const int ChunksAxisMax = 32;

    /// <summary>チャンク 1 辺あたりのボクセル分割数の下限。</summary>
    public const int ChunkCellsMin = 4;

    /// <summary>チャンク 1 辺あたりのボクセル分割数の上限。</summary>
    public const int ChunkCellsMax = 64;

    /// <summary>ボクセルサイズ（m）の下限。</summary>
    public const double VoxelSizeMin = 0.05;

    /// <summary>ボクセルサイズ（m）の上限。</summary>
    public const double VoxelSizeMax = 8.0;

    /// <summary>assets ルートから見た chunk_config.json の相対パス。</summary>
    public const string RelativePath = @"terrain\chunk_config.json";

    // ── 編集対象フィールド ────────────────────────────────────

    /// <summary>チャンク数 X（1..=32）。</summary>
    [JsonPropertyName("chunks_x")]
    public int ChunksX { get; set; } = DefaultChunksX;

    /// <summary>チャンク数 Z（1..=32）。</summary>
    [JsonPropertyName("chunks_z")]
    public int ChunksZ { get; set; } = DefaultChunksZ;

    /// <summary>チャンク 1 辺あたりのボクセル分割数（4..=64）。</summary>
    [JsonPropertyName("chunk_cells")]
    public int ChunkCells { get; set; } = DefaultChunkCells;

    /// <summary>ボクセルサイズ（m・0.05..=8.0）。</summary>
    [JsonPropertyName("voxel_size")]
    public double VoxelSize { get; set; } = DefaultVoxelSize;

    /// <summary>読み込み時に既存ファイルが無かった／壊れていたか（UI の注意表示に使う）。</summary>
    [JsonIgnore]
    public bool WasMissingOrInvalid { get; private init; }

    // ── 読み込み ──────────────────────────────────────────────

    /// <summary>
    /// assets ルート配下の chunk_config.json を読み込む。
    /// ファイルが無い／壊れている場合は例外を投げず、既定値のドキュメントを返す。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public static TerrainChunkConfigDocument Load(string assetsRoot)
    {
        var path = ResolvePath(assetsRoot);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<TerrainChunkConfigDocument>(File.ReadAllText(path));
                if (loaded is not null)
                {
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // 壊れた JSON はフォールバックへ倒す（ここで例外を投げるとウィンドウが開けない）。
        }

        return new TerrainChunkConfigDocument { WasMissingOrInvalid = true };
    }

    // ── 保存 ──────────────────────────────────────────────────

    /// <summary>
    /// 現在の値を chunk_config.json へ書き出す。書き出す前に必ず値域へクランプする。
    /// </summary>
    /// <param name="assetsRoot">assets ディレクトリの絶対パス。</param>
    public void Save(string assetsRoot)
    {
        Clamp();
        var path = ResolvePath(assetsRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 将来コメント等に日本語を含めても \uXXXX へ潰れないようにする（layers.json と同じ方針）。
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    /// <summary>assets ルートから chunk_config.json の絶対パスを求める。</summary>
    public static string ResolvePath(string assetsRoot)
        => Path.Combine(assetsRoot, RelativePath);

    // ── 値域クランプ ──────────────────────────────────────────

    /// <summary>
    /// 全フィールドを IPC が受理する値域へクランプする。
    /// UI からの入力途中の値や、手編集で壊れた JSON の値をそのまま IPC へ渡さないためのガード。
    /// </summary>
    public void Clamp()
    {
        ChunksX    = Math.Clamp(ChunksX, ChunksAxisMin, ChunksAxisMax);
        ChunksZ    = Math.Clamp(ChunksZ, ChunksAxisMin, ChunksAxisMax);
        ChunkCells = Math.Clamp(ChunkCells, ChunkCellsMin, ChunkCellsMax);
        VoxelSize  = Math.Clamp(VoxelSize, VoxelSizeMin, VoxelSizeMax);
    }
}
