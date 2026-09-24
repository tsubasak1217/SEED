// ============================================================
//  AndroidProjectResolver.cs — 指定（--project / --assets-dir）から今回のプロジェクトとアプリの識別情報を決める
//
//  ビルド・配置・起動（Pipeline/AndroidRunPipeline.cs）と、停止だけの操作（AndroidDeviceActions の stop。
//  アプリ ID が要る）の両方が使う。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Project;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Project;

/// <summary>今回のプロジェクトとアプリの識別情報を決める。</summary>
public static class AndroidProjectResolver
{
    /// <summary>
    /// プロジェクトを決める（--project か --assets-dir。どちらも無ければ null）。
    /// </summary>
    /// <param name="projectDir">プロジェクトフォルダ（APK に pak を入れる）。</param>
    /// <param name="assetsDir">開発用のアセットフォルダ（run-as で送る）。</param>
    /// <returns>プロジェクト（指定が無ければ null）。</returns>
    /// <exception cref="AndroidPipelineException">フォルダが無い・アセットルートを決められないとき。</exception>
    public static AndroidProjectInfo? Resolve(string? projectDir, string? assetsDir)
    {
        ResolvedProjectFolder? folder;
        AndroidProjectMode mode;
        string source;
        string? error;
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            source = Path.GetFullPath(projectDir);
            folder = ProjectFolderResolver.Resolve(source, out error);
            mode = AndroidProjectMode.Packaged;
        }
        else if (!string.IsNullOrWhiteSpace(assetsDir))
        {
            source = Path.GetFullPath(assetsDir);
            folder = ProjectFolderResolver.FromAssetsRoot(source, out error);
            mode = AndroidProjectMode.DevelopmentAssets;
        }
        else
        {
            return null;
        }
        if (folder is null) throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, error ?? "プロジェクトを決められません。");
        return new AndroidProjectInfo(folder, mode, source, AndroidProjectSettingsReader.Read(folder.AssetsRoot));
    }

    /// <summary>
    /// アプリの識別情報を決める（書かれた値を検査してから、既定値で埋める）。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ Gradle の既定値）。</param>
    /// <returns>識別情報。</returns>
    /// <exception cref="AndroidPipelineException">書かれた値に誤りがあるとき。</exception>
    public static AndroidAppIdentity ResolveIdentity(AndroidProjectInfo? project)
    {
        var errors = AndroidAppIdentityResolver.Validate(project?.Settings.Android);
        if (errors.Count > 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"プロジェクト設定の Android アプリ情報（{project!.Settings.SettingsPath} の \"{AndroidAppSettings.SectionKey}\"）に誤りがあります: " +
                string.Join(" ", errors));
        }
        return AndroidAppIdentityResolver.Resolve(
            project?.Settings.Android, project?.Folder.ProjectName, project?.Folder.ProjectDisplayName, project is not null);
    }
}
