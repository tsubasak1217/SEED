// ============================================================
//  CargoBuildCommand.cs — ビルド構成 → cargo の起動引数
//
//  【役割】
//  「この構成をビルドするとき cargo に何を渡すか」だけを決める純粋関数の置き場。
//  プロセス起動（Process.Start）は呼び出し側（RuntimeManager / PackagingWindow）が行う。
//
//  【なぜ分けるか】
//  引数の組み立ては単体テストできる（プロセスを起こす必要が無い）一方、
//  間違えると「debug をビルドして develop を起動する」ような静かな不整合になる。
//  検証できる形に切り出して、テストで固定する。
// ============================================================

using System;

namespace SEEDEditor.Runtime.BuildConfig;

/// <summary>
/// ビルド構成から <c>cargo build</c> の起動情報を組み立てる。
/// </summary>
public static class CargoBuildCommand
{
    /// <summary>cargo の実行ファイル名（PATH から解決させる）。</summary>
    public const string Executable = "cargo";

    /// <summary>cargo のサブコマンド。</summary>
    private const string SubCommand = "build";

    /// <summary>プロファイル指定オプション。</summary>
    private const string ProfileOption = "--profile";

    /// <summary>
    /// 構成に対応する <c>cargo build</c> の引数文字列を作る。
    ///
    /// <para>
    /// dev プロファイルも <c>--profile dev</c> と明示する（cargo 1.57 以降で有効）。
    /// 「引数なし＝dev」と特別扱いすると、構成が増えたときに分岐が増え、
    /// ログにどの構成でビルドしたかも残らないため、常に明示する方針にする。
    /// </para>
    /// </summary>
    /// <param name="config">ビルド構成。</param>
    /// <returns>例: <c>build --profile develop</c></returns>
    public static string BuildArguments(RuntimeBuildConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.CargoProfile))
            throw new ArgumentException($"cargo_profile が空です: id='{config.Id}'", nameof(config));

        return $"{SubCommand} {ProfileOption} {config.CargoProfile}";
    }
}
