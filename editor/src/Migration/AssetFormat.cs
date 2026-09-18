// ============================================================
//  AssetFormat.cs — アセット形式の版の表（C# 側の写し）
//
//  【役割】
//  「どの形式が、どの欄名で、いま何版か」をエディタ側で 1 か所に持つ。
//  正典はランタイムの runtime/src/engine/core/migration/kind.rs であり、
//  ここはその写しである。**写しである以上、必ずずれる**ので、
//  editor/tests/MigrationTests が kind.rs を読んで一致を機械的に確かめる
//  （ずれたらテストが落ちる）。
//
//  【なぜ写しを持つのか】
//  版の欄を刻む・版を覗き見る、という操作はエディタ側で頻繁に起きる。
//  そのたびに SEED.exe を起動して現行版を尋ねるのは（数十ミリ秒 × ファイル数の）
//  無駄であり、exe が未ビルドのときに保存できなくなる。
//  そこで「値は写しで持ち、ずれの検出はテストに任せる」形にしてある。
//
//  【依存】
//  WPF にも System.Text.Json にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Migration;

// ── 版の欄名 ─────────────────────────────────────────────────────

/// <summary>
/// 版を格納する JSON のトップレベル欄名。
///
/// <para>
/// 2 通りあるのは、<c>.inputmap</c> / <c>.sprite_mesh</c> が版の仕組みを
/// 導入する前から <c>version</c> で版を持っていたため。既存ファイルを
/// 1 バイトも書き換えずに仕組みへ載せるため、綴りを尊重する。
/// 新しく版を持たせる形式は <see cref="FormatVersion"/> を使う。
/// </para>
/// </summary>
public enum AssetVersionKey
{
    /// <summary><c>"format_version"</c>。この仕組みで新しく版を持たせる形式。</summary>
    FormatVersion,

    /// <summary><c>"version"</c>。導入前から独自に版を持っていた形式。</summary>
    Version,
}

// ── 書き手 ───────────────────────────────────────────────────────

/// <summary>
/// そのアセットを保存するとき、版を刻むのは誰か。
///
/// <para>
/// 変換（読み込み時の持ち上げ）は形式によらずランタイムに一本化されているが、
/// **保存**はランタイムが行う形式とエディタ（C#）が行う形式がある。
/// <see cref="Editor"/> の形式だけがエディタ側の刻印の対象になる。
/// </para>
/// </summary>
public enum AssetFormatWriter
{
    /// <summary>ランタイム（Rust）が保存し、刻印もランタイムが行う。</summary>
    Runtime,

    /// <summary>エディタ（C#）が保存する。刻印はエディタ側の責務。</summary>
    Editor,
}

// ── 形式 1 件 ────────────────────────────────────────────────────

/// <summary>
/// 1 つのアセット形式の仕様（表の 1 行）。
/// </summary>
public sealed class AssetFormat
{
    /// <summary>
    /// 形式名。<c>SEED.exe --migrate-json &lt;kind&gt;</c> の引数、および
    /// <c>--upgrade-project</c> のレポート行の <c>kind</c> と同じ綴り
    /// （＝ Rust の <c>FormatKind::label()</c>）。
    /// </summary>
    public string Label { get; }

    /// <summary>版を格納する JSON の欄名（列挙）。</summary>
    public AssetVersionKey VersionKey { get; }

    /// <summary>このエンジンが読み書きできる最新の版。保存時はこの版を刻む。</summary>
    public int CurrentVersion { get; }

    /// <summary>版を刻む書き手。</summary>
    public AssetFormatWriter Writer { get; }

    /// <summary>版を格納する JSON の欄名（文字列）。</summary>
    public string VersionKeyName => AssetFormats.VersionKeyName(VersionKey);

    /// <summary>全項目を指定して生成する（表の中からのみ生成する）。</summary>
    /// <param name="label">形式名。</param>
    /// <param name="versionKey">版の欄名。</param>
    /// <param name="currentVersion">現行版。</param>
    /// <param name="writer">版を刻む書き手。</param>
    private AssetFormat(
        string label, AssetVersionKey versionKey, int currentVersion, AssetFormatWriter writer)
    {
        Label          = label;
        VersionKey     = versionKey;
        CurrentVersion = currentVersion;
        Writer         = writer;
    }

    /// <summary>表の 1 行を作る（<see cref="AssetFormats"/> だけが呼ぶ）。</summary>
    /// <param name="label">形式名。</param>
    /// <param name="versionKey">版の欄名。</param>
    /// <param name="currentVersion">現行版。</param>
    /// <param name="writer">版を刻む書き手。</param>
    internal static AssetFormat Define(
        string label, AssetVersionKey versionKey, int currentVersion, AssetFormatWriter writer)
        => new(label, versionKey, currentVersion, writer);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"{Label}(v{CurrentVersion}, {VersionKeyName}, {Writer})";
}

// ── 表 ───────────────────────────────────────────────────────────

/// <summary>
/// アセット形式の表。正典は <c>runtime/src/engine/core/migration/kind.rs</c>。
/// </summary>
public static class AssetFormats
{
    // ── 欄名の綴り（マジック文字列の一元化）──────────────────

    /// <summary>版の欄名（新しい形式が使う綴り）。</summary>
    public const string FORMAT_VERSION_KEY = "format_version";

    /// <summary>版の欄名（<c>.inputmap</c> / <c>.sprite_mesh</c> が以前から使う綴り）。</summary>
    public const string LEGACY_VERSION_KEY = "version";

    /// <summary>版の欄が無いファイルを何版とみなすか（docs/asset_migration.md 1 章）。</summary>
    public const int IMPLICIT_FIRST_VERSION = 1;

    // ── 表の本体 ─────────────────────────────────────────────

    /// <summary>シーン（<c>*.scene</c>）。ランタイムが保存する。</summary>
    public static readonly AssetFormat Scene =
        AssetFormat.Define("scene", AssetVersionKey.FormatVersion, 2, AssetFormatWriter.Runtime);

    /// <summary>アクタ＝プレハブ（<c>*.actor</c> / <c>*.actor2d</c>）。ランタイムが保存する。</summary>
    public static readonly AssetFormat Actor =
        AssetFormat.Define("actor", AssetVersionKey.FormatVersion, 2, AssetFormatWriter.Runtime);

    /// <summary>アニメーションクリップ（<c>*.anim</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat Anim =
        AssetFormat.Define("anim", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>マテリアル（<c>*.mat</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat Material =
        AssetFormat.Define("material", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>ポストエフェクトチェーン（<c>*.postfx</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat Postfx =
        AssetFormat.Define("postfx", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>入力アクションマップ（<c>*.inputmap</c>）。欄名は <c>version</c>。</summary>
    public static readonly AssetFormat InputMap =
        AssetFormat.Define("inputmap", AssetVersionKey.Version, 2, AssetFormatWriter.Editor);

    /// <summary>2D スプライトメッシュ（<c>*.sprite_mesh</c>）。欄名は <c>version</c>。</summary>
    public static readonly AssetFormat SpriteMesh =
        AssetFormat.Define("sprite_mesh", AssetVersionKey.Version, 1, AssetFormatWriter.Editor);

    /// <summary>地形レイヤ定義（<c>assets/terrain/layers.json</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat TerrainLayers =
        AssetFormat.Define("terrain_layers", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>地形散布プロップ定義（<c>assets/terrain/props.json</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat TerrainProps =
        AssetFormat.Define("terrain_props", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>地形カバー素材定義（<c>assets/terrain/cover_materials.json</c>）。読むだけ。</summary>
    public static readonly AssetFormat TerrainCoverMaterials =
        AssetFormat.Define("terrain_cover_materials", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>プロジェクト設定（<c>assets/project_settings.json</c>）。書き手はエディタ。</summary>
    public static readonly AssetFormat ProjectSettings =
        AssetFormat.Define("project_settings", AssetVersionKey.FormatVersion, 1, AssetFormatWriter.Editor);

    /// <summary>
    /// 全形式の一覧（kind.rs の <c>FormatKind::ALL</c> と同じ順序）。
    /// テストがこの一覧と kind.rs を突き合わせる。
    /// </summary>
    public static readonly IReadOnlyList<AssetFormat> All = new[]
    {
        Scene,
        Actor,
        Anim,
        Material,
        Postfx,
        InputMap,
        SpriteMesh,
        TerrainLayers,
        TerrainProps,
        TerrainCoverMaterials,
        ProjectSettings,
    };

    // ── 逆引き ───────────────────────────────────────────────

    /// <summary>版の欄名（列挙 → 文字列）。</summary>
    /// <param name="key">版の欄名。</param>
    public static string VersionKeyName(AssetVersionKey key) => key switch
    {
        AssetVersionKey.FormatVersion => FORMAT_VERSION_KEY,
        AssetVersionKey.Version       => LEGACY_VERSION_KEY,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "未知の版の欄名です。"),
    };

    /// <summary>形式名から形式を引く。見つからなければ null。</summary>
    /// <param name="label">形式名（<c>--migrate-json</c> の引数と同じ綴り）。</param>
    public static AssetFormat? FromLabel(string? label)
    {
        if (string.IsNullOrEmpty(label)) return null;
        foreach (var format in All)
        {
            if (string.Equals(format.Label, label, StringComparison.Ordinal)) return format;
        }
        return null;
    }
}
