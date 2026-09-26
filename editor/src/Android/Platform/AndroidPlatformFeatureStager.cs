// ============================================================
//  AndroidPlatformFeatureStager.cs — マニフェストの断片を Gradle の置き場（app/src/seedFeatures/）へ置く（W1-2）
//
//  【流れ】（APK の工程の Gradle の前に毎回呼ぶ。Steps/GradleBuildStep）
//    生成したファイル一式（AndroidPlatformManifestWriter.Render）のうち、中身が違うファイルだけを書き、
//    一覧に無いファイル（前の版の生成物・手で置いたもの）は消す（Common/GeneratedFileSync）。機能が空でも中身の無い
//    マニフェストを必ず書く（古い生成物が残って、前のプロジェクトの権限が入るのを防ぐ）。
//  中身が同じファイルは書かない（更新時刻を変えず、Gradle の差分のビルドとこの工程の指紋を邪魔しない）。
//  置き場は .gitignore（runtime/android/.gitignore）で追跡しない。ランチャーのアイコン（Icons/LauncherIconStager）と同じ扱い。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

namespace SEEDEditor.Android.Platform;

/// <summary>置いた結果。</summary>
/// <param name="Written">書いたファイルの数（中身が変わった・新しい）。</param>
/// <param name="Unchanged">中身が同じで書かなかったファイルの数。</param>
/// <param name="Removed">消したファイルの数（一覧に無い古いもの）。</param>
public sealed record AndroidPlatformFeatureStageResult(int Written, int Unchanged, int Removed)
{
    /// <summary>ログ用の一行。</summary>
    /// <returns>説明。</returns>
    public string Describe() => $"書いた {Written}・同じ {Unchanged}・消した {Removed}";
}

/// <summary>マニフェストの断片を置く。</summary>
public static class AndroidPlatformFeatureStager
{
    /// <summary>
    /// 生成物を置く（中身の違うファイルだけ書き、一覧に無いファイルは消す）。
    /// </summary>
    /// <param name="stagingDir">置き場（app/src/seedFeatures）。</param>
    /// <param name="files">置くファイル一式。</param>
    /// <returns>結果。</returns>
    public static AndroidPlatformFeatureStageResult Stage(string stagingDir, AndroidPlatformFeatureFiles files)
    {
        var (written, unchanged) = GeneratedFileSync.WriteChanged(stagingDir, files.Files);
        var removed = GeneratedFileSync.RemoveStale(stagingDir, files.Files.Keys);
        return new AndroidPlatformFeatureStageResult(written, unchanged, removed);
    }
}
