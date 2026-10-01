// ============================================================
//  PakBuildManifest.cs — pak に入れる「ビルドの印」（開発用のビルドか）の書き手側
//
//  【何のためか】
//  ランタイムは「pak 実行（assets.pak を読んで動いている）＝配布版」とみなし、スクリプトの
//  SEED.Application.IsDebugAllowed（デバッグの命令・開発用の機能の共通ゲート）を false にしていた。
//  開発用のビルド（SeedAndroid の debug の APK・パッケージ化ウィンドウのビルド種別 Debug・SeedPak の --debug-build）でも
//  開発用の機能を使えるように、pak に「開発用」の印を入れる。ランタイムは起動時に読んで IsDebugBuild にし、
//  IsDebugAllowed = !IsPackaged || IsDebugBuild とする。
//
//  【形式】（読み手の正典はランタイムの runtime/src/engine/pak/build_manifest.rs）
//    エントリ名: PackageLayout.BuildManifestEntryPath（".seed/build.json"。予約の名前）
//    中身:       UTF-8 の JSON  {"format":1,"debug":true}
//  **開発用のビルドのときだけ書き、配布用（release）では書かない**（エントリが無い＝配布用。ランタイムの既定も false）。
//  Android の配布前の検査（editor/src/Android/Release/AndroidArtifactChecks.cs）は、配布物の pak にこのエントリがあれば不合格にする。
//  pak のバイナリ形式は変えない（PakWriter の生成エントリとして 1 件足すだけ。印の無い pak は従来どおり読める）。
//
//  【予約の名前】
//  エントリはアセットと同じ表（assets:// の名前空間）に入る。利用者が同じ名前のファイルを置いていても pak には入れない
//  （IsReservedPath。AssetPakBuilder が収録一覧から外す）。配布用のビルドに利用者のファイルが印として紛れ込まないため。
//
//  WPF に依存しない（SeedPak・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Packaging.Pak;

/// <summary>pak の「ビルドの印」（開発用のビルドか）を作る。</summary>
public static class PakBuildManifest
{
    /// <summary>形式の版（ランタイム側 build_manifest.rs の SUPPORTED_FORMAT と一致必須）。</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// 印の中身（JSON の 1 オブジェクト）。キー名はランタイム側 build_manifest.rs の BuildManifest のフィールド名と一致必須。
    /// </summary>
    /// <param name="Format">形式の版。</param>
    /// <param name="Debug">開発用のビルドか。</param>
    private sealed record ManifestJson(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("debug")] bool Debug);

    /// <summary>
    /// 収録ファイルの相対パスが、エンジンの予約の名前（ビルドの印のエントリ名）か【純粋な処理】。
    /// 照合はランタイムの pak の引き方と同じ（区切り \ と / を同じに・大文字小文字を問わない）。
    /// </summary>
    /// <param name="relPath">アセットルートからの相対パス。</param>
    /// <returns>予約の名前なら true（pak に入れてはいけない）。</returns>
    public static bool IsReservedPath(string relPath) =>
        AssetPathUtil.PathComparer.Equals(relPath.Replace('\\', '/'), PackageLayout.BuildManifestEntryPath);

    /// <summary>開発用のビルドの印の中身（UTF-8 の JSON。BOM なし）を作る【純粋な処理】。</summary>
    /// <returns>中身のバイト列。</returns>
    public static byte[] CreateDebugBuildContent() =>
        JsonSerializer.SerializeToUtf8Bytes(new ManifestJson(FormatVersion, Debug: true));

    /// <summary>
    /// ビルドの種類に応じて pak に足す生成エントリを返す【純粋な処理】。
    /// 開発用なら印 1 件、配布用（release）なら空（印を入れない）。
    /// </summary>
    /// <param name="debugBuild">開発用のビルドか。</param>
    /// <returns>生成エントリ。</returns>
    public static IReadOnlyList<PakGeneratedEntry> EntriesFor(bool debugBuild) =>
        debugBuild
            ? new[] { new PakGeneratedEntry(PackageLayout.BuildManifestEntryPath, CreateDebugBuildContent()) }
            : Array.Empty<PakGeneratedEntry>();

    /// <summary>ログに出す 1 行（印を入れるか・ランタイムでどうなるか）。</summary>
    /// <param name="debugBuild">開発用のビルドか。</param>
    /// <returns>1 行。</returns>
    public static string Describe(bool debugBuild) => debugBuild
        ? $"  開発用のビルドの印: 入れる（{PackageLayout.BuildManifestEntryPath}。pak 実行でも SEED.Application.IsDebugAllowed が true）"
        : "  開発用のビルドの印: 入れない（配布用。pak 実行では SEED.Application.IsDebugAllowed が false）";
}
