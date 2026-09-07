// ============================================================
//  FishCatalogGenerator.cs — 魚図鑑カタログ（FishCatalog.cs）の生成器
//
//  魚の prefab（runtime/assets/mainGame/actors/Fish/Lv<N>/*.actor）を走査し、
//  スクリプトから参照できる静的データ表 `FishCatalog.cs` のソース全文を組み立てる。
//  エディタの「ツール → 図鑑画像を生成」（cmd: generate_fish_thumbnails）が、
//  全魚のサムネイル PNG を描き終えた最後に呼ぶ。
//
//  【Python 版との同一性（重要）】
//   同じ生成を行うフォールバック実装 `tools/gen_fish_catalog.py` があり、
//   **両者の出力は 1 バイトも違ってはいけない**（差分ノイズで
//   「どちらで生成したか」がリポジトリ履歴に現れないようにするため）。
//   そのため以下は Python 版と厳密に一致させている:
//     ・ヘッダ文言・空行の位置・行の並び
//     ・並び順（レベル昇順 → アクタ名の序数順。安定ソート）
//     ・文字列エスケープ（\ と " のみ）
//     ・改行は LF 固定・UTF-8（BOM なし）
//   この 2 ファイルのどちらかを直すときは、必ずもう一方も同じだけ直すこと。
//
//  【レベルの正典】
//   実行時の魚レベルは FishManager の levels 配列から引かれるが、あれはシーン内の
//   データであり、図鑑生成のためだけにシーンを読むのは依存が重い。よってここでは
//   prefab の置き場所（`Lv<N>` ディレクトリ名）だけをレベルの唯一の情報源とする。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SEEDEditor.AI.Tools;

/// <summary>
/// 魚 prefab から図鑑カタログ（FishCatalog.cs）のソースを生成する静的ヘルパー。
///
/// <para>
/// 使い方は「<see cref="CollectEntries"/> で走査 → <see cref="RenderCatalogCs"/> で文字列化
/// → <see cref="WriteCatalogCs"/> で書き出し」。まとめて行う
/// <see cref="Generate"/> が通常の入口になる。
/// </para>
/// </summary>
public static class FishCatalogGenerator
{
    // ── 定数（マジックナンバー・マジックストリング禁止）─────────────

    /// <summary>魚 prefab の置き場所（assets ルートからの相対。区切りは "/" 固定）。</summary>
    public const string FISH_ACTOR_REL_DIR = "mainGame/actors/Fish";

    /// <summary>図鑑画像の出力先（assets ルートからの相対。区切りは "/" 固定）。</summary>
    public const string ZUKAN_TEXTURE_REL_DIR = "mainGame/textures/zukan";

    /// <summary>生成する C# ファイル（assets ルートからの相対。区切りは "/" 固定）。</summary>
    public const string CATALOG_CS_REL_PATH = "mainGame/scripts/FishCatalog.cs";

    /// <summary>エディタ／ランタイムが使うアセット URI のスキーム接頭辞。</summary>
    private const string ASSET_URI_PREFIX = "assets://";

    /// <summary>アセット URI のディレクトリ区切り（Windows のパス区切りとは別物）。</summary>
    private const char ASSET_URI_SEPARATOR = '/';

    /// <summary>.actor の拡張子。</summary>
    private const string ACTOR_EXT = ".actor";

    /// <summary>図鑑画像の拡張子。</summary>
    public const string IMAGE_EXT = ".png";

    /// <summary>レベルディレクトリ名の書式（"Lv3" → 3）。</summary>
    private static readonly Regex LevelDirPattern = new(@"^Lv(\d+)$", RegexOptions.Compiled);

    /// <summary>魚スクリプトのファイル名（ScriptComponent.type_name の末尾で判定する）。</summary>
    private const string FISH_SCRIPT_FILE_NAME = "Fish.cs";

    /// <summary>表示名が入っている Fish スクリプトのフィールド名。</summary>
    private const string DISPLAY_NAME_FIELD = "fishName";

    /// <summary>.actor JSON のキー: アクタ名。</summary>
    private const string ACTOR_KEY_NAME = "name";

    /// <summary>.actor JSON のキー: コンポーネント配列。</summary>
    private const string ACTOR_KEY_COMPONENTS = "components";

    /// <summary>.actor JSON のキー: コンポーネント本体。</summary>
    private const string ACTOR_KEY_COMPONENT = "component";

    /// <summary>.actor JSON のキー: コンポーネント種別名。</summary>
    private const string ACTOR_KEY_TYPE = "type";

    /// <summary>.actor JSON のキー: コンポーネントのデータ本体。</summary>
    private const string ACTOR_KEY_DATA = "data";

    /// <summary>.actor JSON のキー: スクリプトの型名（Windows 絶対パスで保存される）。</summary>
    private const string ACTOR_KEY_TYPE_NAME = "type_name";

    /// <summary>.actor JSON のキー: スクリプトの公開フィールド（既定値と異なるものだけ）。</summary>
    private const string ACTOR_KEY_FIELDS = "fields";

    /// <summary>表示名を持つスクリプトコンポーネントの種別名。</summary>
    private const string SCRIPT_COMPONENT_TYPE = "ScriptComponent";

    /// <summary>Windows のパス区切り（type_name の basename を取るために正規化する）。</summary>
    private const char WINDOWS_PATH_SEPARATOR = '\\';

    /// <summary>エントリが 1 件も無いときの MaxLevel。</summary>
    private const int NO_ENTRY_MAX_LEVEL = 0;

    /// <summary>生成するソースの改行（LF 固定。Python 版と 1 バイトも違わないため）。</summary>
    private const string LINE_SEPARATOR = "\n";

    /// <summary>自動生成ファイルの先頭に置く警告ヘッダ（末尾の改行は含めない）。</summary>
    private const string GENERATED_HEADER =
          "// 自動生成 — 編集しないで「図鑑画像を生成」で再生成" + LINE_SEPARATOR
        + "// （エディタ: Tools > 図鑑画像を生成 / cmd: generate_fish_thumbnails /" + LINE_SEPARATOR
        + "//   エディタ無し: python tools/gen_fish_catalog.py）" + LINE_SEPARATOR
        + "// 生成元: runtime/assets/mainGame/actors/Fish/Lv<N>/*.actor";

    // ── データ構造 ───────────────────────────────────────────────

    /// <summary>
    /// カタログ 1 行ぶんのデータ。生成後の C# 側 <c>FishCatalogEntry</c> と 1 対 1 で対応する。
    /// </summary>
    /// <param name="Level">魚レベル（prefab の置き場所 Lv&lt;N&gt; 由来）。</param>
    /// <param name="ActorName">.actor の name（無ければファイル名の拡張子なし）。</param>
    /// <param name="DisplayName">Fish スクリプトの表示名（未入力なら ActorName と同じ）。</param>
    /// <param name="ActorPath">prefab の assets:// パス。</param>
    /// <param name="ImagePath">図鑑画像（透過 PNG）の assets:// パス。</param>
    public readonly record struct FishEntry(
        int    Level,
        string ActorName,
        string DisplayName,
        string ActorPath,
        string ImagePath);

    // ── データ収集 ───────────────────────────────────────────────

    /// <summary>魚 prefab のディレクトリ（絶対パス）を返す。</summary>
    /// <param name="assetsPath">assets ルートの絶対パス。</param>
    public static string GetFishActorDir(string assetsPath)
        => Path.Combine(assetsPath, RelToOsPath(FISH_ACTOR_REL_DIR));

    /// <summary>生成先 FishCatalog.cs の絶対パスを返す。</summary>
    /// <param name="assetsPath">assets ルートの絶対パス。</param>
    public static string GetCatalogCsPath(string assetsPath)
        => Path.Combine(assetsPath, RelToOsPath(CATALOG_CS_REL_PATH));

    /// <summary>
    /// 指定レベルの図鑑画像（PNG）の絶対パスを返す。
    /// サムネイル生成側とカタログ生成側で組み立て規則を必ず共有するため、ここに集約する。
    /// </summary>
    /// <param name="assetsPath">assets ルートの絶対パス。</param>
    /// <param name="levelDirName">レベルディレクトリ名（"Lv3" など）。</param>
    /// <param name="actorFileStem">.actor のファイル名（拡張子なし）。</param>
    public static string GetImagePath(string assetsPath, string levelDirName, string actorFileStem)
        => Path.Combine(assetsPath, RelToOsPath(ZUKAN_TEXTURE_REL_DIR),
                        levelDirName, actorFileStem + IMAGE_EXT);

    /// <summary>"a/b/c" 形式の相対パスを OS のパス区切りへ直す。</summary>
    private static string RelToOsPath(string relPath)
        => relPath.Replace(ASSET_URI_SEPARATOR, Path.DirectorySeparatorChar);

    /// <summary>
    /// 魚 prefab を走査して、カタログ 1 行ぶんのデータ一覧を返す。
    ///
    /// <para>
    /// 並び順は「レベル昇順 → アクタ名の序数順」で決定的（＝再生成しても差分が出ない）。
    /// 同キーが並んだ場合はディレクトリ名・ファイル名の序数順（Python 版の安定ソートと同じ）。
    /// </para>
    /// </summary>
    /// <param name="assetsPath">assets ルートの絶対パス。</param>
    /// <exception cref="DirectoryNotFoundException">魚 prefab のディレクトリが無い場合。</exception>
    public static List<FishEntry> CollectEntries(string assetsPath)
    {
        var fishDir = GetFishActorDir(assetsPath);
        if (!Directory.Exists(fishDir))
            throw new DirectoryNotFoundException($"魚 prefab のディレクトリが見つからない: {fishDir}");

        var entries = new List<FishEntry>();

        // ── レベルディレクトリを名前順（序数）に走査する ──────────────
        // Python 版の sorted(os.listdir(...)) と同じ順序にするため序数比較で並べる。
        var levelDirs = Directory.GetDirectories(fishDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var levelDirName in levelDirs)
        {
            // "Lv<N>" 以外（FishBase.actor の置き場など）は図鑑の対象外。
            var matched = LevelDirPattern.Match(levelDirName);
            if (!matched.Success) continue;
            if (!int.TryParse(matched.Groups[1].Value, out var level)) continue;

            var levelDir = Path.Combine(fishDir, levelDirName);

            // ── そのレベルの .actor をファイル名順（序数）に走査する ────
            var actorFiles = Directory.GetFiles(levelDir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n)
                         && n!.EndsWith(ACTOR_EXT, StringComparison.Ordinal))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var fileName in actorFiles)
            {
                var stem      = fileName[..^ACTOR_EXT.Length];
                var actorJson = ReadActorJson(Path.Combine(levelDir, fileName));

                // アクタ名は .actor の name。空・欠落ならファイル名（拡張子なし）で代替する。
                var actorName = ReadStringProperty(actorJson, ACTOR_KEY_NAME);
                if (string.IsNullOrEmpty(actorName)) actorName = stem;

                entries.Add(new FishEntry(
                    Level:       level,
                    ActorName:   actorName,
                    DisplayName: ReadFishDisplayName(actorJson, actorName),
                    // ランタイム／スクリプト API が解決できる assets:// URI で持つ
                    ActorPath:   $"{ASSET_URI_PREFIX}{FISH_ACTOR_REL_DIR}"
                               + $"{ASSET_URI_SEPARATOR}{levelDirName}{ASSET_URI_SEPARATOR}{fileName}",
                    ImagePath:   $"{ASSET_URI_PREFIX}{ZUKAN_TEXTURE_REL_DIR}"
                               + $"{ASSET_URI_SEPARATOR}{levelDirName}{ASSET_URI_SEPARATOR}{stem}{IMAGE_EXT}"));
            }
        }

        // OrderBy は安定ソートなので、キーが同じなら走査順（＝上の序数順）が保たれる。
        return entries
            .OrderBy(e => e.Level)
            .ThenBy(e => e.ActorName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>.actor（JSON）を読む。BOM 付きでも読めるようにストリームから読ませる。</summary>
    private static JsonElement ReadActorJson(string path)
    {
        // JsonDocument は UTF-8 BOM を自前で読み飛ばす。Clone() で using を抜けても使えるようにする。
        using var stream = File.OpenRead(path);
        using var doc    = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    /// <summary>JSON オブジェクトから文字列プロパティを取り出す（無ければ空文字）。</summary>
    private static string ReadStringProperty(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        if (!obj.TryGetProperty(key, out var el)) return "";
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : "";
    }

    /// <summary>
    /// .actor の Fish スクリプト設定から表示名を取り出す。
    ///
    /// <para>
    /// ScriptComponent の fields は「既定値と異なる値だけ」が保存されるため、
    /// 表示名が未入力の prefab には <c>fishName</c> キー自体が存在しない
    /// （現状はこちらが通常ケース）。その場合はアクタ名で代替する。
    /// </para>
    /// </summary>
    /// <param name="actor">.actor のルート JSON。</param>
    /// <param name="fallback">表示名が取れなかったときに使う名前（＝アクタ名）。</param>
    private static string ReadFishDisplayName(JsonElement actor, string fallback)
    {
        if (actor.ValueKind != JsonValueKind.Object) return fallback;
        if (!actor.TryGetProperty(ACTOR_KEY_COMPONENTS, out var components)
            || components.ValueKind != JsonValueKind.Array)
            return fallback;

        foreach (var component in components.EnumerateArray())
        {
            // コンポーネントは { "name": ..., "component": { "type": ..., "data": {...} } } の形。
            if (component.ValueKind != JsonValueKind.Object) continue;
            if (!component.TryGetProperty(ACTOR_KEY_COMPONENT, out var inner)
                || inner.ValueKind != JsonValueKind.Object)
                continue;
            if (ReadStringProperty(inner, ACTOR_KEY_TYPE) != SCRIPT_COMPONENT_TYPE) continue;

            if (!inner.TryGetProperty(ACTOR_KEY_DATA, out var data)
                || data.ValueKind != JsonValueKind.Object)
                continue;

            // type_name は Windows の絶対パス（"\" 区切り）で保存されているので末尾要素で判定する。
            var typeName   = ReadStringProperty(data, ACTOR_KEY_TYPE_NAME);
            var scriptFile = typeName[(typeName.LastIndexOf(WINDOWS_PATH_SEPARATOR) + 1)..];
            if (scriptFile != FISH_SCRIPT_FILE_NAME) continue;

            // fields は string→string のマップ。未入力なら DISPLAY_NAME_FIELD 自体が無い。
            if (!data.TryGetProperty(ACTOR_KEY_FIELDS, out var fields)
                || fields.ValueKind != JsonValueKind.Object)
                continue;

            var name = ReadStringProperty(fields, DISPLAY_NAME_FIELD).Trim();
            if (name.Length > 0) return name;
        }

        return fallback;
    }

    // ── C# ソース生成 ────────────────────────────────────────────

    /// <summary>C# の文字列リテラルへエスケープする（" と \ のみで足りる）。</summary>
    private static string CsStringLiteral(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// FishCatalog.cs のソース全文を組み立てる。
    /// 行の並び・空行の位置は tools/gen_fish_catalog.py と厳密に一致させること。
    /// </summary>
    /// <param name="entries">カタログに載せるエントリ（並び順はそのまま出力される）。</param>
    public static string RenderCatalogCs(IReadOnlyList<FishEntry> entries)
    {
        // MaxLevel は収録エントリの最大レベル。1 件も無ければ 0。
        var maxLevel = entries.Count == 0 ? NO_ENTRY_MAX_LEVEL : entries.Max(e => e.Level);

        var lines = new List<string>
        {
            GENERATED_HEADER,
            "",
            "using System;",
            "using System.Collections.Generic;",
            "",
            "/// <summary>",
            "/// 図鑑 1 種ぶんの静的データ（prefab から自動抽出した内容）。",
            "/// </summary>",
            "[Serializable]",
            "public struct FishCatalogEntry",
            "{",
            @"    /// <summary>魚レベル（1〜<see cref=""FishCatalog.MaxLevel""/>）。prefab の置き場所 Lv&lt;N&gt; が正典。</summary>",
            "    public int level;",
            "",
            "    /// <summary>アクタ名（.actor の name。ローマ字の種別 ID として使える）。</summary>",
            "    public string actorName;",
            "",
            @"    /// <summary>表示名（Fish スクリプトの「表示名」。未入力なら <see cref=""actorName""/> と同じ）。</summary>",
            "    public string displayName;",
            "",
            "    /// <summary>prefab の assets:// パス。</summary>",
            "    public string actorPath;",
            "",
            "    /// <summary>図鑑画像（透過 PNG）の assets:// パス。<c>SEED.Sprite.TexturePath</c> にそのまま入る。</summary>",
            "    public string imagePath;",
            "}",
            "",
            "/// <summary>",
            "/// 全魚種の図鑑データ表。エディタの「図鑑画像を生成」で丸ごと再生成される。",
            "/// 並び順は「レベル昇順 → アクタ名の辞書順」で固定。",
            "/// </summary>",
            "public static class FishCatalog",
            "{",
            "    /// <summary>収録されている最大の魚レベル。</summary>",
            $"    public const int MaxLevel = {maxLevel};",
            "",
            "    /// <summary>全エントリ（レベル昇順 → アクタ名順）。</summary>",
            "    public static readonly FishCatalogEntry[] Entries =",
            "    {",
        };

        // ── エントリ行（1 種 1 行のオブジェクト初期化子）────────────────
        foreach (var entry in entries)
        {
            var fields = string.Join(", ", new[]
            {
                $"level = {entry.Level}",
                $"actorName = {CsStringLiteral(entry.ActorName)}",
                $"displayName = {CsStringLiteral(entry.DisplayName)}",
                $"actorPath = {CsStringLiteral(entry.ActorPath)}",
                $"imagePath = {CsStringLiteral(entry.ImagePath)}",
            });
            lines.Add($"        new FishCatalogEntry {{ {fields} }},");
        }

        lines.AddRange(new[]
        {
            "    };",
            "",
            @"    /// <summary>指定レベルのエントリだけを列挙する（並び順は <see cref=""Entries""/> と同じ）。</summary>",
            @"    /// <param name=""lv"">魚レベル。該当が無ければ空列挙を返す。</param>",
            "    public static IEnumerable<FishCatalogEntry> ForLevel(int lv)",
            "    {",
            "        foreach (FishCatalogEntry entry in Entries)",
            "        {",
            "            if (entry.level == lv) { yield return entry; }",
            "        }",
            "    }",
            "",
            "    /// <summary>アクタ名で 1 件引く。見つからなければ false。</summary>",
            @"    /// <param name=""actorName"">.actor の name（大文字小文字は区別しない）。</param>",
            @"    /// <param name=""found"">見つかったエントリ。</param>",
            "    public static bool TryGetByActorName(string actorName, out FishCatalogEntry found)",
            "    {",
            "        foreach (FishCatalogEntry entry in Entries)",
            "        {",
            "            if (string.Equals(entry.actorName, actorName, StringComparison.OrdinalIgnoreCase))",
            "            {",
            "                found = entry;",
            "                return true;",
            "            }",
            "        }",
            "        found = default;",
            "        return false;",
            "    }",
            "",
            "    /// <summary>表示名で 1 件引く。見つからなければ false。</summary>",
            @"    /// <param name=""displayName"">Fish スクリプトの表示名（大文字小文字は区別しない）。</param>",
            @"    /// <param name=""found"">見つかったエントリ。</param>",
            "    public static bool TryGetByDisplayName(string displayName, out FishCatalogEntry found)",
            "    {",
            "        foreach (FishCatalogEntry entry in Entries)",
            "        {",
            "            if (string.Equals(entry.displayName, displayName, StringComparison.OrdinalIgnoreCase))",
            "            {",
            "                found = entry;",
            "                return true;",
            "            }",
            "        }",
            "        found = default;",
            "        return false;",
            "    }",
            "}",
            // 末尾の空要素 = ファイル末尾を改行 1 個で終わらせるため（Python 版と同じ）
            "",
        });

        return string.Join(LINE_SEPARATOR, lines);
    }

    // ── 書き出し ─────────────────────────────────────────────────

    /// <summary>
    /// 生成したソースをファイルへ書き出す。
    /// 文字コードは UTF-8（BOM なし）、改行は LF 固定
    /// （Python 版の出力と 1 バイトも違わないようにするため）。
    /// </summary>
    /// <param name="outPath">書き出し先の絶対パス。</param>
    /// <param name="source">書き出すソース全文。</param>
    public static void WriteCatalogCs(string outPath, string source)
    {
        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // UTF8Encoding(false) = BOM を書かない。WriteAllText は改行を変換しないので LF のまま残る。
        File.WriteAllText(outPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 走査からファイル書き出しまでをまとめて行う（通常の入口）。
    /// </summary>
    /// <param name="assetsPath">assets ルートの絶対パス。</param>
    /// <param name="outPath">
    /// 書き出し先。null なら <see cref="GetCatalogCsPath"/>（assets 配下の正規の位置）。
    /// </param>
    /// <returns>(書き出したパス, 収録したエントリ数)。</returns>
    public static (string Path, int Count) Generate(string assetsPath, string? outPath = null)
    {
        var entries = CollectEntries(assetsPath);
        var source  = RenderCatalogCs(entries);
        var target  = outPath ?? GetCatalogCsPath(assetsPath);
        WriteCatalogCs(target, source);
        return (target, entries.Count);
    }
}
