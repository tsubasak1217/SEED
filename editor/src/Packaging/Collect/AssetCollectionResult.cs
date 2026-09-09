// ============================================================
//  AssetCollectionResult.cs — アセット収集の結果データ
//
//  【役割】
//  「何を入れたか / 何を落としたか / 何が見つからなかったか」を保持するだけの
//  値オブジェクト。UI とログ出力はこの中身を読むだけでよい。
//
//  【なぜ分けるか】
//  収集アルゴリズム（AssetCollector）と、その結果の見せ方（PackagingWindow）を
//  切り離すため。テストは結果オブジェクトだけを検証すればよくなる。
// ============================================================

using System.Collections.Generic;

namespace SEEDEditor.Packaging.Collect;

/// <summary>
/// 参照されているのに実体が見つからなかった 1 件。
/// </summary>
/// <param name="ReferencePath">参照先として解決を試みたアセットルート相対パス。</param>
/// <param name="RawText">元テキストに書かれていた生の文字列。</param>
/// <param name="SourceRelPath">その参照が書かれていたファイル（アセットルート相対）。</param>
public readonly record struct MissingReference(
    string ReferencePath,
    string RawText,
    string SourceRelPath);

/// <summary>
/// 収録対象となったファイル 1 件。
/// </summary>
/// <param name="RelPath">アセットルート相対パス（'/' 区切り）。</param>
/// <param name="SizeBytes">実ファイルのバイトサイズ。</param>
public readonly record struct CollectedAsset(string RelPath, long SizeBytes);

/// <summary>アセット収集の結果一式。</summary>
public sealed class AssetCollectionResult
{
    /// <summary>収録するファイル（アセットルート相対パスの昇順）。</summary>
    public IReadOnlyList<CollectedAsset> Included { get; init; } = [];

    /// <summary>収録ファイルの合計バイト数。</summary>
    public long IncludedBytes { get; init; }

    /// <summary>アセットルート配下の総ファイル数（除外判定前）。</summary>
    public int TotalFileCount { get; init; }

    /// <summary>アセットルート配下の総バイト数（除外判定前）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>参照されていたが実体が無かったパス一覧。</summary>
    public IReadOnlyList<MissingReference> MissingReferences { get; init; } = [];

    /// <summary>
    /// 除外ルールに当たっているが参照されていたため同梱したファイル。
    /// 「除外設定が実態と合っていない」ことを利用者へ知らせるための警告材料。
    /// </summary>
    public IReadOnlyList<string> IncludedDespiteExclusion { get; init; } = [];

    /// <summary>project_settings.json に登録されているのに実体が無かったシーン。</summary>
    public IReadOnlyList<string> MissingScenes { get; init; } = [];

    /// <summary>収録されなかったファイル数（総数 − 収録数）。</summary>
    public int ExcludedFileCount => TotalFileCount - Included.Count;

    /// <summary>収録されなかったバイト数（総バイト − 収録バイト）。</summary>
    public long ExcludedBytes => TotalBytes - IncludedBytes;
}
