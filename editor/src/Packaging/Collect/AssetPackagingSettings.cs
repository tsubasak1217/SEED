// ============================================================
//  AssetPackagingSettings.cs — 収録ルールのユーザー設定
//
//  【役割】
//  「どのフォルダ・拡張子を捨てるか」「参照グラフでは辿れないものをどう救うか」を
//  プロジェクトごとに保存する設定。packaging_settings.json の "assets" セクション。
//
//  【設計方針】
//  ・既定値は PackagingRules から取る（規則の正典を 2 か所に持たない）。
//  ・除外は「未参照のものを掃除する」ためのもので、参照より優先しない。
//    参照されている限り、除外設定に当たっていても同梱される（警告は出す）。
//  ・どうしても参照グラフで辿れないもの（文字列組み立てで読むアセット等）は
//    AdditionalFolders という逃げ道で丸ごと足せる。
// ============================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SEEDEditor.Packaging.Collect;

/// <summary>アセット収録ルールのユーザー設定。</summary>
public class AssetPackagingSettings
{
    /// <summary>
    /// true なら参照解決を行わず、アセットルート配下の全ファイルを同梱する（従来の挙動）。
    /// 収録漏れが疑われるときの緊急避難用。
    /// </summary>
    [JsonPropertyName("include_all_files")]
    public bool IncludeAllFiles { get; set; }

    /// <summary>除外するフォルダ名（パスのどこかの階層に一致で除外。'*' ワイルドカード可）。</summary>
    [JsonPropertyName("excluded_folders")]
    public List<string> ExcludedFolders { get; set; } = [.. PackagingRules.DefaultExcludedFolders];

    /// <summary>除外する拡張子（ドット付き）。</summary>
    [JsonPropertyName("excluded_extensions")]
    public List<string> ExcludedExtensions { get; set; } = [.. PackagingRules.DefaultExcludedExtensions];

    /// <summary>除外するファイル名（'*' ワイルドカード可）。OS が作るゴミファイル向け。</summary>
    [JsonPropertyName("excluded_file_names")]
    public List<string> ExcludedFileNames { get; set; } = [.. PackagingRules.DefaultExcludedFileNames];

    /// <summary>
    /// 参照の有無に関わらず丸ごと同梱するフォルダ（アセットルート相対）。
    /// スクリプトが実行時に文字列を組み立てて読むアセットなど、
    /// 静的な参照グラフでは辿れないものを救うための逃げ道。
    /// </summary>
    [JsonPropertyName("additional_folders")]
    public List<string> AdditionalFolders { get; set; } = [];

    /// <summary>
    /// 参照の有無に関わらず同梱する拡張子（除外ルールには従う）。
    /// 既定は空。スクリプト（.cs）はパッケージ化時に DLL へ事前コンパイルして
    /// 同梱するため、ソースを配る必要がない（PackagingRules 側のコメント参照）。
    /// </summary>
    [JsonPropertyName("always_included_extensions")]
    public List<string> AlwaysIncludedExtensions { get; set; } =
        [.. PackagingRules.DefaultAlwaysIncludedExtensions];
}
