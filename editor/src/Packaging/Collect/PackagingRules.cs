// ============================================================
//  PackagingRules.cs — パッケージ収録ルールの既定データ
//
//  【役割】
//  「どの拡張子をテキストとして走査するか」「どのファイルが暗黙の同伴ファイルか」
//  「既定でどれを除外するか」といった規則を **データとして 1 か所に集約**する。
//
//  【なぜ 1 か所か】
//  収録判定のロジック（AssetCollector）と規則（ここ）を分けておくと、
//  新しいアセット形式が増えたときにこのファイルへ 1 行足すだけで済む。
//  規則をコードの各所へ散らすと、追従漏れが「パッケージ版だけ動かない」形で出る。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Packaging.Collect;

/// <summary>
/// アセット収録ルールの既定データ集。すべて静的な読み取り専用テーブルで、
/// 実行時に変わる値（ユーザー設定）は <see cref="AssetPackagingSettings"/> 側が持つ。
/// </summary>
public static class PackagingRules
{
    // ── テキストとして参照を走査する拡張子 ─────────────────────
    //
    //  ここに載っている拡張子のファイルだけを開いて中身の参照文字列を拾う。
    //  バイナリ（.png/.glb/.tvox など）は開かないので、参照グラフの葉になる。

    /// <summary>参照抽出のためにテキストとして読む拡張子（小文字・ドット付き）。</summary>
    public static readonly IReadOnlyList<string> ScannableExtensions =
    [
        ".scene",       // シーン
        ".actor",       // アクタ（プレハブ）
        ".actor2d",     // 2D アクタ
        ".json",        // 各種設定（layers.json / props.json / terrain_meta.json など）
        ".anim",        // アニメーションクリップ
        ".icons",       // アイコンセット
        ".inputmap",    // 入力マップ
        ".cs",          // C# スクリプト（文字列リテラルに assets:// を持つ）
        ".gltf",        // glTF（.bin / テクスチャを uri で参照する）
        ".mtl",         // OBJ のマテリアル定義（テクスチャを参照する）
        ".wgsl",        // シェーダ（include 相当の参照を持ちうる）
        ".mat",         // マテリアル
        ".postfx",      // ポストエフェクト設定
        ".shading",     // シェーディングアセット
        ".sprite_mesh", // スプライトメッシュ
        ".toml",        // 設定
        ".txt",         // クレジット等（参照を書くことがある）
    ];

    // ── PAK 格納時にパス書き換えを行う拡張子 ───────────────────
    //
    //  絶対パス（C:\...\runtime\assets\...）を assets:// へ書き換える対象。
    //  書き換えるとバイト列サイズが変わるため、PakWriter は先にメモリ上で変換する。

    /// <summary>PAK 格納時に絶対パス → 仮想パスの書き換えを行う拡張子。</summary>
    public static readonly IReadOnlyList<string> PathRewriteExtensions =
    [
        ".json", ".scene", ".actor", ".actor2d", ".inputmap", ".anim", ".icons",
    ];

    // ── 暗黙の同伴ファイル（参照グラフに現れない） ─────────────
    //
    //  ランタイムが「拡張子を差し替えて隣のファイルを読む」種類の依存。
    //  例: 地形は .scene に .tvox しか書かれないが、
    //      runtime 側（terrain_ops.rs）が .tvox の拡張子を差し替えて
    //      .tscatter（散布）/ .tcover（カバー）を読みに行く。
    //      参照グラフだけを見ていると草と地面のカバーが丸ごと欠ける。

    /// <summary>拡張子 → 同じ場所・同じ名前で拡張子だけ違う同伴ファイルの拡張子。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SiblingExtensions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 地形ボクセル → 散布・カバー（terrain_ops.rs の命名規則と対応）
            [".tvox"] = [".tscatter", ".tcover"],
            // OBJ → MTL（同名のマテリアル定義）
            [".obj"] = [".mtl"],
        };

    // ── 暗黙の同伴ファイル（フォルダ単位） ─────────────────────

    /// <summary>拡張子 → そのファイルと同じフォルダに置かれる付随ファイル名。</summary>
    public static readonly IReadOnlyDictionary<string, string[]> FolderCompanions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 地形フォルダ直下のメタデータ（terrain_meta_ops.rs）
            [".tvox"] = ["terrain_meta.json"],
        };

    // ── 既定の除外ルール ───────────────────────────────────────
    //
    //  「未参照なら要らない」ものだけを並べる。参照されていれば同梱される
    //  （除外は掃除用であって、参照より優先はしない）。

    /// <summary>既定の除外フォルダ名（パスのどこかの階層に一致したら除外。'*' ワイルドカード可）。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedFolders =
    [
        ".backup",                    // エディタの自動バックアップ
        "templates",                  // 新規作成用テンプレート（実ゲームは参照分だけあればよい）
        "assets_realdir_backup_*",    // 手動バックアップ
        "__MACOSX",                   // macOS の zip 展開ゴミ
    ];

    /// <summary>既定の除外拡張子（小文字・ドット付き）。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedExtensions =
    [
        ".lock",    // エディタのロックファイル
        ".blend",   // Blender 原本
        ".blend1",  // Blender バックアップ
        ".zip",     // 素材アーカイブ
        ".psd",     // Photoshop 原本
        ".tmp",     // 一時ファイル
        ".bak",     // 一時バックアップ
    ];

    /// <summary>既定の除外ファイル名（'*' ワイルドカード可）。OS が作るゴミファイル。</summary>
    public static readonly IReadOnlyList<string> DefaultExcludedFileNames =
    [
        ".DS_Store",    // macOS
        "._*",          // macOS の AppleDouble
        "Thumbs.db",    // Windows エクスプローラ
        "desktop.ini",  // Windows エクスプローラ
    ];

    /// <summary>
    /// 参照の有無に関わらず（除外に当たらない限り）同梱する拡張子。
    ///
    /// <para>
    /// <b>既定は空</b>である。以前は .cs を入れていたが、これは
    /// 「パッケージ版が起動時にアセット配下の .cs をまとめてコンパイルする」
    /// 前提のものだった。現在はパッケージ化の時点で
    /// <c>SEEDUserScripts.dll</c> へ事前コンパイルして同梱する方式なので
    /// （editor/src/Packaging/Scripts/ScriptPackager.cs）、ソースを配る必要が無い。
    /// .cs の**参照走査**（<see cref="ScannableExtensions"/>）は引き続き行う
    /// ——スクリプトの文字列リテラルに書かれた assets:// 参照を拾うため。
    /// </para>
    /// <para>
    /// ここが空でも、プロジェクトの packaging_settings.json に
    /// 明示された拡張子（<see cref="AssetPackagingSettings.AlwaysIncludedExtensions"/>）は
    /// 従来どおり常時同梱される。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAlwaysIncludedExtensions = [];

    /// <summary>
    /// 参照されていても決して同梱しないファイル（アセットルート相対）。
    /// エディタ専用の設定は配布物に含めない（出力先の絶対パス等が漏れるため）。
    /// </summary>
    public static readonly IReadOnlyList<string> NeverIncludedRelativePaths =
    [
        "packaging_settings.json",
    ];

    // ── 走査の上限 ─────────────────────────────────────────────

    /// <summary>
    /// テキストとして開いて参照を走査するファイルサイズの上限（バイト）。
    /// これを超えるファイルは参照抽出を諦める（同梱はする）。
    /// 巨大な .json/.gltf を正規表現で舐めて固まるのを防ぐ保険。
    /// </summary>
    public const long MaxScanFileSizeBytes = 64L * 1024 * 1024;

    // ── ヘルパー ───────────────────────────────────────────────

    /// <summary>
    /// ユーザー入力の拡張子を照合用の正規形（小文字・先頭ドット付き）へ揃える。
    /// UI で "cs" と ".cs" のどちらを書かれても同じ意味になるようにする。
    /// </summary>
    /// <param name="extension">ユーザーが入力した拡張子。</param>
    /// <returns>正規化した拡張子（空入力なら空文字）。</returns>
    public static string NormalizeExtension(string extension)
    {
        var s = extension.Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        return s[0] == '.' ? s : "." + s;
    }

    /// <summary>
    /// '*' のみを任意文字列として扱う簡易ワイルドカード照合（大文字小文字は無視）。
    /// 除外フォルダ名・除外ファイル名の照合に使う。
    /// </summary>
    /// <param name="pattern">パターン（例 "assets_realdir_backup_*"）。</param>
    /// <param name="text">照合対象（フォルダ名またはファイル名）。</param>
    /// <returns>一致すれば true。</returns>
    public static bool WildcardMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        // ワイルドカードを含まないなら単純比較（大半はこちらを通る）
        if (!pattern.Contains('*'))
            return string.Equals(pattern, text, StringComparison.OrdinalIgnoreCase);

        // '*' で分割し、前から順に「その断片が現れるか」を確かめていく
        var parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            if (i == 0)
            {
                // 先頭断片は必ず先頭に一致していなければならない
                if (!text.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                pos = part.Length;
                continue;
            }

            if (i == parts.Length - 1)
            {
                // 末尾断片は必ず末尾に一致していなければならない
                if (!text.EndsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                // 前方断片と重ならないことを確認する
                return text.Length - part.Length >= pos;
            }

            var found = text.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            pos = found + part.Length;
        }
        return true;
    }
}
