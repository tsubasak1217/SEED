// ============================================================
//  PushedOverrides.cs — 端末に置いた上書きの解除（push の DLL＝files/bin/、実行中の差し替えのアセット＝files/assets/）
//                       （段階C-4・実行中の差し替え。docs/android.md §17.7・§23.4）
//
//  端末はスクリプトを「内部の files/bin/ → APK の bin/」の順に、デバッグ版はアセットを「内部の files/assets/ → APK の pak」の
//  順に探す。push・差し替えで置いたものが残っていると、APK を作り直して入れても古い上書きが優先されるので、APK の内容を
//  正とする場面では消す。adb install -r はアプリのデータ（files/）を残すので、入れ直すだけでは消えない。
//
//  【消す場面】（PushedOverrideMoment。判断は純粋な処理 ClearsScripts / ClearsAssets。単体テスト PushOverrideTests・HotReloadTests）
//    AfterInstall … APK を入れ直した直後（install・run のインストールの工程が APK を入れたとき。同じ APK で飛ばしたときは消さない）
//    BeforeLaunch … run の起動の前（アプリを止めた後。APK を入れ直さなかった run でも消す）
//    どちらも --project（APK に pak とスクリプトを入れる）のときだけ。開発用の --assets-dir は files/bin/・files/assets/ が
//    唯一の置き場なので消さない。スクリプトは --push-scripts（これから files/bin/ に置く・置いた）のときも消さない。
//    push（DLL だけ送って起動し直す）は差し替えを残す。
//
//  【上書き層の記録】（cache/android/asset_overlay.json。差し替えの差分の土台。HotReload/AndroidAssetOverlaySync）
//    files/assets/ を消した（元から無かった）ら、その端末の記録を「送ったもの無し・今の置き場の pak」に作り直す。
//    消せなかったら記録を残す（端末に残った上書きと記録が合ったまま＝差し替えの差分を取り違えない）。
//    開発用の run は起動の前に pak の無い記録にする（差し替えは送ったことの無いものを送る）。
//
//  消せなくても工程は続ける（警告の 1 行。上書きのまま動く）。
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;

namespace SEEDEditor.Android.Steps;

/// <summary>上書きを消す場面。</summary>
public enum PushedOverrideMoment
{
    /// <summary>APK を入れ直した直後（インストールの工程）。</summary>
    AfterInstall,

    /// <summary>run の起動の前（起動の工程。アプリを止めた後）。</summary>
    BeforeLaunch,
}

/// <summary>
/// 1 回の解除の材料（場面・指定・プロジェクト・端末・アプリ・エンジンの置き場）。
/// </summary>
/// <param name="Moment">場面。</param>
/// <param name="Request">指定。</param>
/// <param name="Project">プロジェクト（無ければ null）。</param>
/// <param name="Serial">端末のシリアル。</param>
/// <param name="ApplicationId">アプリ ID。</param>
/// <param name="Engine">エンジンの置き場（APK の pak の置き場・記録の置き場）。</param>
public sealed record PushedOverrideScope(
    PushedOverrideMoment Moment,
    AndroidRunRequest Request,
    AndroidProjectInfo? Project,
    string Serial,
    string ApplicationId,
    AndroidEnginePaths Engine)
{
    /// <summary>
    /// 工程の共有の値から作る。
    /// </summary>
    /// <param name="context">共有の値。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="moment">場面。</param>
    /// <returns>材料。</returns>
    public static PushedOverrideScope For(AndroidPipelineContext context, string serial, PushedOverrideMoment moment) =>
        new(moment, context.Request, context.Project, serial, context.Identity.ApplicationId, context.Engine);
}

/// <summary>
/// 端末の自分のアプリのデータフォルダの中のフォルダを消す口（run-as。消したら true、元から無ければ false、
/// 消せなければ <see cref="AdbCommandException"/>）。単体テストは偽物を渡す。
/// </summary>
/// <param name="remoteDir">アプリのデータフォルダからの相対パス（files/bin 等）。</param>
/// <param name="cancellationToken">中断の合図。</param>
/// <returns>消したら true、元から無ければ false。</returns>
public delegate Task<bool> RemoteDirectoryRemover(string remoteDir, CancellationToken cancellationToken);

/// <summary>端末に置いた上書きの解除。</summary>
public static class PushedOverrides
{
    /// <summary>push した DLL の上書き（端末の files/bin/）を消したときの Output の 1 行（インストールの後・起動の前で共通）。</summary>
    public const string ScriptsClearedMessage =
        "push した DLL の上書きを解除しました（端末の " + AndroidRuntimeContract.RemoteScriptsDir + "/ を消し、APK の bin/ のスクリプトを使います）。";

    /// <summary>push した DLL の上書きを消せなかったときの警告の書式（{0}=理由）。</summary>
    private const string ScriptsNotClearedFormat =
        "push した DLL の上書きを解除できませんでした（端末の " + AndroidRuntimeContract.RemoteScriptsDir + "/ の DLL が APK の bin/ より優先されます）: {0}";

    /// <summary>差し替えで送ったアセットの上書き（端末の files/assets/）を消したときの Output の 1 行（§23）。</summary>
    public const string AssetsClearedMessage =
        "差し替えで送ったアセットの上書きを解除しました（端末の " + AndroidRuntimeContract.RemoteAssetsDir + "/ を消し、APK の pak のアセットを使います）。";

    /// <summary>差し替えで送ったアセットの上書きを消せなかったときの警告の書式（{0}=理由）。</summary>
    private const string AssetsNotClearedFormat =
        "差し替えで送ったアセットの上書きを解除できませんでした（端末の " + AndroidRuntimeContract.RemoteAssetsDir + "/ のアセットが APK の pak より優先されます）: {0}";

    /// <summary>上書き層の記録を書けなかったときの警告の書式（{0}=記録のファイル、{1}=理由）。</summary>
    private const string OverlayRecordNotSavedFormat =
        "上書き層の記録を書けません（差し替えの差分は APK の pak と比べずに選びます）: {0}（{1}）";

    /// <summary>
    /// push した DLL の上書き（端末の files/bin/）を消すか（純粋な処理）。APK にスクリプトを入れる（--project）ときだけ、
    /// インストールの後（install・run）と run の起動の前に消す。--push-scripts はこれから files/bin/ に置く（置いた）ので、
    /// 開発用の --assets-dir は APK に bin/ が無く files/bin/ が唯一の置き場なので、消さない。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="moment">場面。</param>
    /// <returns>消すなら true。</returns>
    public static bool ClearsScripts(AndroidRunRequest request, AndroidProjectInfo? project, PushedOverrideMoment moment) =>
        AppliesTo(request, moment) && !request.PushScripts && project is { Mode: AndroidProjectMode.Packaged };

    /// <summary>
    /// 差し替えで送ったアセットの上書き（端末の files/assets/）を消すか（純粋な処理。§23）。APK に pak を入れる（--project）ときだけ、
    /// インストールの後（install・run）と run の起動の前に消す（--push-scripts でもアセットは APK が正）。開発用の --assets-dir は
    /// files/assets/ が唯一のアセットの置き場なので消さない。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="moment">場面。</param>
    /// <returns>消すなら true。</returns>
    public static bool ClearsAssets(AndroidRunRequest request, AndroidProjectInfo? project, PushedOverrideMoment moment) =>
        AppliesTo(request, moment) && project is { Mode: AndroidProjectMode.Packaged };

    /// <summary>
    /// 上書き層の記録を「送ったもの無し・今の pak」に作り直すか（純粋な処理）。files/assets/ を消せた（元から無かった）ときは作り直し、
    /// 消せなかったときは残す（端末に残った上書きと記録を合わせたまま）。消す場面でないときは、run の起動の前（開発用の run）だけ作り直す。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <param name="moment">場面。</param>
    /// <param name="assetsCleared">files/assets/ を消した結果（消せた・元から無かった＝true、消せなかった＝false、消す場面でない＝null）。</param>
    /// <returns>作り直すなら true。</returns>
    public static bool ResetsOverlayRecord(AndroidRunRequest request, PushedOverrideMoment moment, bool? assetsCleared) => assetsCleared switch
    {
        true => true,
        false => false,
        null => moment == PushedOverrideMoment.BeforeLaunch && request.Goal == AndroidRunGoal.Run,
    };

    /// <summary>
    /// 場面の規則どおりに上書きを消し、上書き層の記録を合わせる。消したときだけ Output に 1 行ずつ出す。
    /// 消せなくても続ける（警告を出す。上書きのまま動く）。
    /// </summary>
    /// <param name="scope">材料。</param>
    /// <param name="remove">端末のフォルダを消す口（<see cref="RunAs"/>。単体テストは偽物）。</param>
    /// <param name="log">工程のログ。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    public static async Task ClearAsync(
        PushedOverrideScope scope, RemoteDirectoryRemover remove, AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        // ── push した DLL（files/bin/）──
        if (ClearsScripts(scope.Request, scope.Project, scope.Moment))
        {
            await RemoveAsync(remove, AndroidRuntimeContract.RemoteScriptsDir, ScriptsClearedMessage, ScriptsNotClearedFormat, log, cancellationToken)
                .ConfigureAwait(false);
        }

        // ── 差し替えで送ったアセット（files/assets/）と、その記録 ──
        bool? assetsCleared = ClearsAssets(scope.Request, scope.Project, scope.Moment)
            ? await RemoveAsync(remove, AndroidRuntimeContract.RemoteAssetsDir, AssetsClearedMessage, AssetsNotClearedFormat, log, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (ResetsOverlayRecord(scope.Request, scope.Moment, assetsCleared))
        {
            ResetOverlayRecord(scope, log);
        }
    }

    /// <summary>
    /// adb の run-as で消す口（本番の経路）。
    /// </summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID（検査済みの値）。</param>
    /// <returns>消す口。</returns>
    public static RemoteDirectoryRemover RunAs(AdbClient adb, string serial, string applicationId) =>
        (remoteDir, cancellationToken) => adb.RunAsRemoveDirectoryAsync(serial, applicationId, remoteDir, cancellationToken);

    /// <summary>
    /// 上書き層の記録を新しく作る（送ったもの無し。APK に pak を入れたなら、その pak の目印を土台として記録する）。
    /// </summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="engine">エンジンの置き場（APK の pak の置き場）。</param>
    /// <returns>記録。</returns>
    public static AndroidAssetOverlayRecord NewOverlayRecord(string applicationId, AndroidProjectInfo? project, AndroidEnginePaths engine)
    {
        var (size, writeTime) = project is { Mode: AndroidProjectMode.Packaged }
            ? AndroidAssetOverlayState.PakIdentity(Path.Combine(engine.ApkPackageDir, PackageLayout.PakFileName))
            : ((long?)null, (DateTime?)null);
        return new AndroidAssetOverlayRecord
        {
            ApplicationId = applicationId,
            PakSize = size,
            PakWriteTimeUtc = writeTime,
            UpdatedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>その場面で工程が扱う目的か（インストールの後＝install・run、起動の前＝run）。</summary>
    private static bool AppliesTo(AndroidRunRequest request, PushedOverrideMoment moment) => moment switch
    {
        PushedOverrideMoment.AfterInstall => request.Goal is AndroidRunGoal.Install or AndroidRunGoal.Run,
        _ => request.Goal == AndroidRunGoal.Run,
    };

    /// <summary>
    /// 端末のフォルダを 1 つ消す。あって消したときだけ Output に 1 行、消せなければ警告の 1 行。
    /// </summary>
    /// <returns>消せた（元から無かった）なら true、消せなかったら false。</returns>
    private static async Task<bool> RemoveAsync(
        RemoteDirectoryRemover remove, string remoteDir, string clearedMessage, string notClearedFormat,
        AndroidPhaseLog log, CancellationToken cancellationToken)
    {
        try
        {
            if (await remove(remoteDir, cancellationToken).ConfigureAwait(false)) log.Info(clearedMessage);
            return true;
        }
        catch (AdbCommandException ex)
        {
            log.Warn(string.Format(notClearedFormat, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// その端末の上書き層の記録を「送ったもの無し・今の置き場の pak」に作り直して書く。書けなくても続ける
    /// （差し替えの差分を pak と比べずに選ぶだけ）。
    /// </summary>
    private static void ResetOverlayRecord(PushedOverrideScope scope, AndroidPhaseLog log)
    {
        var path = AndroidAssetOverlayState.PathFor(scope.Project, scope.Engine);
        try
        {
            var state = AndroidAssetOverlayState.Load(path);
            state.Devices[scope.Serial] = NewOverlayRecord(scope.ApplicationId, scope.Project, scope.Engine);
            state.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn(string.Format(OverlayRecordNotSavedFormat, path, ex.Message));
        }
    }
}
