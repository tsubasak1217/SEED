// ============================================================
//  PackagingOutputDefaults.cs — パッケージ化の既定出力先
//
//  【役割】
//  出力フォルダが未設定（空）のときに使う既定値を 1 か所で決める。
//  プロジェクト概念の導入前は「未設定ならビルドできない」だったが、
//  プロジェクトルートが確定した今は <ProjectRoot>/build/<platform> を既定にできる。
//
//  判定は純関数なので、UI からもテストからも同じ結果が得られる。
// ============================================================

using System.IO;
using SEEDEditor.Project;

namespace SEEDEditor.Packaging;

/// <summary>
/// パッケージ化の出力先の既定値を解決する。
/// </summary>
public static class PackagingOutputDefaults
{
    /// <summary>
    /// プラットフォームごとの build 配下サブフォルダ名。
    /// 出力物のフォルダ名に使うので小文字・空白なしで固定する
    /// （表示名（"Nintendo Switch" 等）をそのまま使うとパスに空白が入るため）。
    /// </summary>
    /// <param name="platform">対象プラットフォーム。</param>
    /// <returns>build 配下のサブフォルダ名。</returns>
    public static string FolderNameFor(TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows        => "windows",
        TargetPlatform.macOS          => "macos",
        TargetPlatform.Android        => "android",
        TargetPlatform.iOS            => "ios",
        TargetPlatform.PlayStation5   => "ps5",
        TargetPlatform.NintendoSwitch => "switch",
        _                             => "other",
    };

    /// <summary>
    /// 出力先を解決する（純関数）。
    /// </summary>
    /// <param name="configuredPath">設定ファイルの output_path（空なら未設定）。</param>
    /// <param name="projectRoot">プロジェクトルート。空なら既定を作れない。</param>
    /// <param name="platform">対象プラットフォーム。</param>
    /// <returns>
    /// 設定されていればその値、未設定なら
    /// <c>&lt;ProjectRoot&gt;/build/&lt;platform&gt;</c>。
    /// どちらも決められなければ空文字（＝呼び出し側がエラーにする）。
    /// </returns>
    public static string Resolve(string? configuredPath, string? projectRoot, TargetPlatform platform)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath!.Trim();
        if (string.IsNullOrWhiteSpace(projectRoot))     return string.Empty;

        return Path.Combine(projectRoot!, ProjectPaths.BUILD_DIR_NAME, FolderNameFor(platform));
    }

    /// <summary>
    /// 現在開いているプロジェクトを前提に出力先を解決する。
    /// </summary>
    /// <param name="configuredPath">設定ファイルの output_path（空なら未設定）。</param>
    /// <param name="platform">対象プラットフォーム。</param>
    public static string ResolveForCurrentProject(string? configuredPath, TargetPlatform platform)
        => Resolve(configuredPath, ProjectContext.RootDir, platform);

    /// <summary>
    /// 設定 UI に出すヒント文（未設定時にどこへ出るかを利用者へ伝える）。
    /// </summary>
    /// <param name="platform">対象プラットフォーム。</param>
    public static string HintFor(TargetPlatform platform)
    {
        var defaultPath = ResolveForCurrentProject(null, platform);
        return string.IsNullOrEmpty(defaultPath)
            ? "出力フォルダが空のときは <プロジェクトルート>/build/<プラットフォーム> へ出力します。"
            : $"空のままにすると次の場所へ出力します:\n{defaultPath}";
    }
}
