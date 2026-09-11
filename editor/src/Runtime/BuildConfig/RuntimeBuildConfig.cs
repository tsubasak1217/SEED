// ============================================================
//  RuntimeBuildConfig.cs — ランタイム（SEED.exe）のビルド構成 1 件
//
//  【役割】
//  「エディタの Play が起動する SEED.exe を、どの Cargo プロファイルで
//  ビルドし、どのフォルダから起動するか」という 1 組の対応をあらわす値。
//
//  【なぜ必要か】
//  従来、エディタは runtime/target/debug/SEED.exe（dev プロファイル）固定だった。
//  dev は自前コードが完全に unoptimized なため、同じシーンでも release の
//  約 2.5 倍のフレーム時間になる（実測 18 ms 対 7 ms）。かといって release は
//  デバッグ情報が乏しくビルドも長い。「最適化 1 ＋ デバッグ情報」の中間構成
//  （develop）を含めて、ユーザーが用途に応じて選べるようにする。
//
//  【データドリブン】
//  構成の一覧は editor/config/runtime_build_configs.json に置く（この型はその
//  1 要素に対応する）。構成を増やすときは JSON へ 1 件足し、Cargo.toml に
//  同名のプロファイルを足すだけで、エディタのコードを変えずに選べるようになる。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/RuntimeBuildConfigTests）からリンクして使うため、
//  WPF / エディタ本体のシングルトンへは一切依存しない。
// ============================================================

using System;
using System.Text.Json.Serialization;

namespace SEEDEditor.Runtime.BuildConfig;

/// <summary>
/// ランタイム（SEED.exe）のビルド構成 1 件。
/// editor/config/runtime_build_configs.json の "configs" 要素に 1 対 1 で対応する。
/// </summary>
public sealed class RuntimeBuildConfig
{
    /// <summary>
    /// 構成の識別子（"debug" / "develop" / "release"）。
    /// 環境設定（editor_preferences.json）へ保存されるのはこの値。
    /// ラベルを変えても選択が失われないよう、表示名とは別に持つ。
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>UI（ツールバーのコンボボックス）に出す表示名。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>
    /// cargo へ渡すプロファイル名（<c>cargo build --profile &lt;この値&gt;</c>）。
    /// ルート Cargo.toml の <c>[profile.*]</c> と一致していること。
    /// </summary>
    [JsonPropertyName("cargo_profile")]
    public string CargoProfile { get; set; } = "";

    /// <summary>
    /// 成果物が出る target 配下のフォルダ名（<c>runtime/target/&lt;この値&gt;/SEED.exe</c>）。
    /// cargo はプロファイル名 = 出力フォルダ名だが、dev だけは "debug" に出るため
    /// プロファイル名とは別フィールドで持つ（ここを推測で埋めない）。
    /// </summary>
    [JsonPropertyName("target_dir")]
    public string TargetDir { get; set; } = "";

    /// <summary>
    /// 構成の説明。コンボボックスのツールチップに出す。
    /// 「初回ビルドに時間がかかる」といった注意はここへ書く。
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// 必須項目がすべて埋まっているか。
    /// 空の id / cargo_profile / target_dir は、パスや cargo 引数を壊すため採用しない。
    /// （label が空でも動作はするが、選べない項目になるので必須とする）
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Label)
        && !string.IsNullOrWhiteSpace(CargoProfile)
        && !string.IsNullOrWhiteSpace(TargetDir);

    /// <summary>
    /// 値を指定して生成する（組み込み既定・テスト用）。
    /// JSON デシリアライズは既定コンストラクタ＋セッターを使う。
    /// </summary>
    /// <param name="id">識別子。</param>
    /// <param name="label">表示名。</param>
    /// <param name="cargoProfile">cargo のプロファイル名。</param>
    /// <param name="targetDir">target 配下の出力フォルダ名。</param>
    /// <param name="description">説明（ツールチップ）。</param>
    public static RuntimeBuildConfig Create(
        string id, string label, string cargoProfile, string targetDir, string description)
        => new()
        {
            Id           = id,
            Label        = label,
            CargoProfile = cargoProfile,
            TargetDir    = targetDir,
            Description  = description,
        };

    /// <summary>ログ用の短い表現。</summary>
    public override string ToString()
        => $"{Label}(id={Id}, profile={CargoProfile}, target={TargetDir})";

    /// <summary>
    /// 識別子の比較規則。id はファイル・設定に書かれる文字列なので、
    /// 大文字小文字の揺れ（"Develop" と "develop"）は同じものとして扱う。
    /// </summary>
    public static readonly StringComparison IdComparison = StringComparison.OrdinalIgnoreCase;
}
