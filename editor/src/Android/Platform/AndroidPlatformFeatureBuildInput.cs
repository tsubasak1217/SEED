// ============================================================
//  AndroidPlatformFeatureBuildInput.cs — ビルド（SeedAndroid・エディタ）の準備でプラットフォーム機能を決める入口（W1-2）
//
//  【役割】
//  埋め込みの機能の表を読み、プロジェクト設定の android 節からこのビルドのプラットフォーム機能を決める
//  （AndroidPlatformFeatureResolver）。ビルドの中核（Pipeline/AndroidRunPipeline の準備）と、ビルドをしない要件の確認
//  （Release/AndroidRequirementsCheckRunner）が同じこれを使う。
//    ・表が埋め込まれていない・壊れている … エンジンの不具合として止める（AndroidFailureKind.Build）。黙って機能なしにすると、
//                                           目覚ましの権限が入らないまま APK ができるため
//    ・設定の誤り（ディープリンクの形等） … 何もビルドしないうちに止める（AndroidFailureKind.InvalidRequest。識別情報の誤りと同じ扱い）
//    ・注意（知らない機能・値等）         … 結果の Warnings に入れて返す（呼び出し側がログへ出す）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.IO;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.ProjectSettings;

namespace SEEDEditor.Android.Platform;

/// <summary>ビルドの準備でプラットフォーム機能を決める。</summary>
public static class AndroidPlatformFeatureBuildInput
{
    /// <summary>
    /// 埋め込みの機能の表を読む（読めなければビルドの失敗）。
    /// </summary>
    /// <returns>機能の表。</returns>
    /// <exception cref="AndroidPipelineException">表が埋め込まれていない・壊れているとき。</exception>
    public static AndroidPlatformFeatureCatalog LoadCatalog()
    {
        try
        {
            return AndroidPlatformFeatureCatalog.BuiltIn;
        }
        catch (InvalidDataException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build, ex.Message, ex);
        }
    }

    /// <summary>
    /// このビルドのプラットフォーム機能を決める（プロジェクトが無ければ機能なし・既定）。
    /// </summary>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <returns>結果（Warnings はログへ出すこと）。</returns>
    /// <exception cref="AndroidPipelineException">表が読めない・設定に誤りがあるとき。</exception>
    public static AndroidPlatformFeatureSet Resolve(AndroidProjectInfo? project)
    {
        var set = AndroidPlatformFeatureResolver.Resolve(project?.Settings.Android, LoadCatalog());
        if (set.Errors.Count > 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"プロジェクト設定のプラットフォーム機能（{project?.Settings.SettingsPath} の \"{AndroidAppSettings.SectionKey}\"）に誤りがあります: " +
                string.Join(" ", set.Errors));
        }
        return set;
    }
}
