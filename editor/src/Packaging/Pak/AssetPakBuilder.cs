// ============================================================
//  AssetPakBuilder.cs — 収録アセットの決定 → パス書き換え → assets.pak 書き出しの手順
//
//  【役割】
//  パッケージ化ウィンドウ（PackagingWindow）と、エディタを起動せずに PAK を作る
//  コンソールツール（editor/tools/SeedPak）が、**同じ手順・同じログ** で assets.pak を
//  作るための共通部分。
//    1. 収録ファイルの決定 … AssetCollector（参照グラフの閉包）
//    2. 結果の報告         … 件数・サイズ・欠落参照・除外設定との食い違いをログへ
//    3. 書き出し           … PakWriter（書き換え対象は AssetPathRewriter で assets:// へ）
//    4. 書き出し結果の報告
//  ここが持つのは「呼ぶ順番」と「ログの書式」だけで、判断そのものは各クラスにある。
//
//  【WPF 非依存】
//  進捗バー・ステータス文字列などの UI は呼び出し側の責務。ログと進捗はコールバックで受ける。
//  PackagingCollectorTests / SeedPak がリンクで取り込むため、WPF 型に依存してはならない
//  （依存した瞬間にそれらのビルドが壊れて気付ける）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Packaging.Pak;

/// <summary>assets.pak を作る手順（収集 → 報告 → 書き出し → 報告）。</summary>
public static class AssetPakBuilder
{
    // ── ログの書式に関わる定数 ───────────────────────────────

    /// <summary>欠落参照をログへ列挙する最大件数（多すぎるとログが読めなくなる）。</summary>
    public const int MaxLoggedMissingReferences = 50;

    /// <summary>除外ルールに当たったまま同梱したファイルをログへ列挙する最大件数。</summary>
    public const int MaxLoggedExcludedButIncluded = 20;

    /// <summary>バイト数を MB 表記へ直すための除数。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>収録対象が 0 件だったときのログ（PAK は書かない）。</summary>
    public const string EmptyCollectionMessage =
        "❌ 収録対象が 0 件です。project_settings.json の start_scene / scenes を確認してください。";

    // ============================================================
    //  1. 収録ファイルの決定
    // ============================================================

    /// <summary>
    /// 参照グラフを辿って、PAK に入れるファイルを決める。
    /// </summary>
    /// <param name="assetsRoot">
    /// アセットルートの絶対パス。エディタが使うパスをそのまま渡すこと
    /// （シーン等に書かれた絶対パス参照の照合と assets:// への書き換えがこの表記を基準にする）。
    /// </param>
    /// <param name="settings">収録ルール（<c>packaging_settings.json</c> の <c>assets</c>）。</param>
    /// <param name="runtimeSourceRoot">
    /// runtime/src の絶対パス。エンジンに焼き込まれた assets:// 参照を起点に加える。null なら省略。
    /// </param>
    /// <param name="log">進行状況の出力先（省略可）。</param>
    /// <param name="extraSeeds">
    /// 既定の起点（project_settings.json・登録シーンほか）に足す追加の起点（省略・空なら従来どおり）。
    /// SeedPak の <c>--extra-scene</c>（Android の実行で、シーンマネージャに未登録の開いているシーンを pak に入れる）が使う。
    /// パッケージ化ウィンドウは渡さない（配布物は登録シーンから作る）。
    /// </param>
    /// <returns>収録一覧・欠落一覧・除外統計。</returns>
    public static AssetCollectionResult Collect(
        string assetsRoot,
        AssetPackagingSettings settings,
        string? runtimeSourceRoot,
        Action<string>? log,
        IReadOnlyCollection<string>? extraSeeds = null) =>
        new AssetCollector(assetsRoot, settings, runtimeSourceRoot, log).Collect(extraSeeds ?? Array.Empty<string>());

    // ============================================================
    //  2. 収集結果の報告
    // ============================================================

    /// <summary>収集結果（件数・サイズ・欠落・警告）をログへ書き出す。</summary>
    /// <param name="result">Collect の結果。</param>
    /// <param name="log">ログの出力先。</param>
    public static void ReportCollection(AssetCollectionResult result, Action<string> log)
    {
        log($"収録: {result.Included.Count} ファイル / {ToMegabytes(result.IncludedBytes):F1} MB");
        log($"除外: {result.ExcludedFileCount} ファイル / {ToMegabytes(result.ExcludedBytes):F1} MB " +
            $"（アセット全体 {result.TotalFileCount} ファイル / {ToMegabytes(result.TotalBytes):F1} MB）");

        // 実体の無いシーン登録
        foreach (var scene in result.MissingScenes)
            log($"⚠ 登録シーンの実体がありません: {scene}");

        // 除外ルールに当たっているが参照されたので入れたもの（設定見直しの材料）
        if (result.IncludedDespiteExclusion.Count > 0)
        {
            log($"⚠ 除外ルールに一致するが参照されているため同梱: {result.IncludedDespiteExclusion.Count} ファイル");
            foreach (var path in result.IncludedDespiteExclusion.Take(MaxLoggedExcludedButIncluded))
                log($"    {path}");
            if (result.IncludedDespiteExclusion.Count > MaxLoggedExcludedButIncluded)
                log($"    …ほか {result.IncludedDespiteExclusion.Count - MaxLoggedExcludedButIncluded} ファイル");
        }

        // 参照はあるが実体が無いパス（パッケージ版で読み込み失敗になる箇所）
        if (result.MissingReferences.Count > 0)
        {
            log($"❌ 参照先が見つからないパス: {result.MissingReferences.Count} 件");
            foreach (var m in result.MissingReferences.Take(MaxLoggedMissingReferences))
                log($"    {m.ReferencePath}  ← {m.SourceRelPath}");
            if (result.MissingReferences.Count > MaxLoggedMissingReferences)
                log($"    …ほか {result.MissingReferences.Count - MaxLoggedMissingReferences} 件");
        }
    }

    /// <summary>収集結果に PAK へ入れるものがあるか（0 件なら書き出さない）。</summary>
    /// <param name="result">Collect の結果。</param>
    /// <returns>1 件以上あれば true。</returns>
    public static bool HasContent(AssetCollectionResult result) => result.Included.Count > 0;

    // ============================================================
    //  3. 書き出し
    // ============================================================

    /// <summary>
    /// 収録が決まったファイルを assets.pak として書き出す（パス書き換えを含む）。
    /// </summary>
    /// <param name="pakPath">出力する .pak のパス（親フォルダが無ければ作る）。</param>
    /// <param name="assetsRoot">アセットルートの絶対パス（Collect に渡したものと同じ表記）。</param>
    /// <param name="result">Collect の結果。</param>
    /// <param name="log">ログ出力先（省略可）。</param>
    /// <param name="progress">進捗通知先（省略可）。</param>
    /// <returns>書き出し結果の統計。</returns>
    public static PakWriteStats Write(
        string pakPath,
        string assetsRoot,
        AssetCollectionResult result,
        Action<string>? log,
        Action<PakWriteProgress>? progress) =>
        PakWriter.Write(pakPath, assetsRoot, result.Included, log, progress);

    // ============================================================
    //  4. 書き出し結果の報告
    // ============================================================

    /// <summary>書き出し結果（件数・サイズ・食い違い）をログへ書き出す。</summary>
    /// <param name="stats">Write の結果。</param>
    /// <param name="log">ログの出力先。</param>
    public static void ReportWrite(PakWriteStats stats, Action<string> log)
    {
        log($"✓ {PackageLayout.PakFileName} 作成完了: {stats.EntryCount} ファイル / {ToMegabytes(stats.TotalBytes):F1} MB");
        if (stats.SizeMismatchCount > 0)
            log($"⚠ 収集後にサイズが変わったファイル: {stats.SizeMismatchCount} 件（0 埋め / 切り捨てで整合させました）");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>バイト数を MB へ変換する。</summary>
    /// <param name="bytes">バイト数。</param>
    /// <returns>MB 単位の値。</returns>
    public static double ToMegabytes(long bytes) => bytes / BytesPerMegabyte;
}
