// ============================================================
//  TargetApplication.cs — 端末で操作するアプリの ID を決める（stop・pause・resume・screenshot で共有）
//
//  --app-id があればそれ（形式を確かめる）、無ければ --project / --assets-dir（設定 JSON 可）の設定から決める
//  （ビルドのときと同じ既定値。プロジェクトも無ければ com.seedengine.runtime）。
// ============================================================

using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>端末で操作するアプリの ID を決める。</summary>
public static class TargetApplication
{
    /// <summary>
    /// アプリ ID を決める。
    /// </summary>
    /// <param name="line">コマンドラインの指定（--app-id）。</param>
    /// <param name="request">設定 JSON と重ねた指定（--project / --assets-dir）。</param>
    /// <param name="applicationId">決まったアプリ ID（失敗時は空）。</param>
    /// <param name="error">決められなかった理由（成功時は null）。</param>
    /// <returns>決まったら true。</returns>
    public static bool TryResolve(SeedAndroidCommandLine line, AndroidRunRequest request, out string applicationId, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(line.ApplicationId))
        {
            applicationId = line.ApplicationId.Trim();
            if (AndroidAppIdentityResolver.IsValidApplicationId(applicationId)) return true;
            error = $"アプリ ID の形式が正しくありません: {applicationId}";
            applicationId = string.Empty;
            return false;
        }
        applicationId = AndroidProjectResolver.ResolveIdentity(
            AndroidProjectResolver.Resolve(request.ProjectDir, request.AssetsDir)).ApplicationId;
        return true;
    }
}
