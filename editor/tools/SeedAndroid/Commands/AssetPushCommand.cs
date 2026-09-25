// ============================================================
//  AssetPushCommand.cs — push --assets <フォルダ>（端末と違うアセットだけを上書き層 files/assets へ送る。docs/android.md §23）
//
//  【流れ】（中核の HotReload/AndroidAssetOverlaySync。エディタの実行中の差し替えと同じ差分の選び方）
//    1. プロジェクト（--project、無ければ --assets のフォルダから探す）・アプリ ID・端末を決める
//    2. 候補: 今 pak を作ると入るもの＋アセットの中の全シーンから辿れるもの（SeedPak と同じ収録の規則）のうち、フォルダの中
//    3. 端末の中身（送った記録 cache/android/asset_overlay.json → run のときの APK の pak）と手元の中身（pak に入れるときと同じ形）を
//       比べて違うものだけを run-as で送る（置いてあるものは消さない）。記録を更新する
//  アプリは起動し直さない（取り込ませるのは reload scene / reload asset）。デバッグ版の APK だけ（run-as）。
//  アプリが動いていなくても送れる（次に起動したときから上書き層として読まれる。run で起動し直すと消える）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.HotReload;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>push --assets。</summary>
public static class AssetPushCommand
{
    /// <summary>1 MiB（大きさの表示用）。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>送ったファイルを 1 行ずつ書く最大の件数（多すぎるときは「ほか」）。</summary>
    private const int MaxListedFiles = 30;

    /// <summary>
    /// 端末と違うアセットだけを送る。
    /// </summary>
    /// <param name="toolchain">道具の場所（adb）。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="config">設定 JSON の値（無ければ null）。</param>
    /// <param name="cancellationToken">中断の合図（Ctrl+C）。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(
        AndroidToolchain toolchain, SeedAndroidCommandLine line, AndroidRunRequest? config, CancellationToken cancellationToken)
    {
        var folder = Path.GetFullPath(line.OverlayAssetsDir!);
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"エラー: {SeedAndroidArguments.OverlayAssetsOption} のフォルダがありません: {folder}");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory);
        if (engine is null)
        {
            Console.Error.WriteLine("エラー: SEED のリポジトリ（runtime/Cargo.toml と runtime/android/gradlew.bat）が見つかりません。リポジトリの中で実行してください。");
            return SeedAndroidExitCodes.Toolchain;
        }

        // ── プロジェクト（--project、無ければフォルダから）・アプリ ID・端末 ──
        var request = SeedAndroidArguments.ToRequest(line, config);
        var project = AndroidProjectResolver.Resolve(request.ProjectDir ?? folder, null);
        if (project is null)
        {
            Console.Error.WriteLine($"エラー: プロジェクトが見つかりません（--project を指定してください）: {request.ProjectDir ?? folder}");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        if (AndroidAssetOverlaySync.FolderPrefix(project.Folder.AssetsRoot, folder) is null)
        {
            Console.Error.WriteLine($"エラー: {SeedAndroidArguments.OverlayAssetsOption} はアセットルート（{project.Folder.AssetsRoot}）かその中を指定してください: {folder}");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        // アプリ ID は --app-id（形式を確かめる）か、プロジェクトの設定（--project が無ければフォルダから探したプロジェクト）
        if (!TargetApplication.TryResolve(line, request with { ProjectDir = request.ProjectDir ?? folder }, out var applicationId, out var appError))
        {
            Console.Error.WriteLine($"エラー: {appError}");
            return SeedAndroidExitCodes.InvalidRequest;
        }
        var device = await new AndroidDeviceActions(toolchain).ResolveDeviceAsync(request.Serial, cancellationToken);

        // ── 差分を選んで送る ──
        Console.Out.WriteLine($"差分を調べます: {folder}（アプリ {applicationId}・端末 {device.DisplayName}）");
        var sync = new AndroidAssetOverlaySync(new AdbClient(toolchain.RequireAdb()), device.Serial, applicationId, project, engine);
        AndroidOverlaySyncResult result;
        try
        {
            result = await sync.PushFolderAsync(folder, cancellationToken);
        }
        catch (AdbCommandException ex)
        {
            Console.Error.WriteLine($"エラー: 転送できませんでした（デバッグ版の APK が入っているか確認してください）: {ex.Message}");
            return SeedAndroidExitCodes.DeviceOperation;
        }

        // ── 結果 ──
        var plan = result.Plan;
        if (result.PakBaselineNote is { } note) Console.Out.WriteLine($"注意: {note}");
        foreach (var item in plan.ToPush.Take(MaxListedFiles))
        {
            var reason = item.Change == AndroidOverlayChange.New ? "新しい" : "違う";
            Console.Out.WriteLine($"  送った（{reason}）: {item.Relative}  {item.Local.Size:N0} バイト");
        }
        if (plan.ToPush.Count > MaxListedFiles) Console.Out.WriteLine($"  …ほか {plan.ToPush.Count - MaxListedFiles} ファイル");
        foreach (var skip in plan.Unreadable) Console.Out.WriteLine($"  読めないため送らない: {skip.Relative}（{skip.Reason}）");
        Console.Out.WriteLine(
            $"完了: {plan.ToPush.Count} ファイル・{plan.PushBytes / BytesPerMegabyte:F1} MB を送りました" +
            $"（候補 {result.CandidateCount}・端末と同じ {plan.Unchanged.Count}・{result.Elapsed.TotalSeconds:F1} 秒）。" +
            (plan.ToPush.Count > 0 ? "取り込ませるには reload scene か reload asset <相対パス> を使います。" : string.Empty));
        return SeedAndroidExitCodes.Success;
    }
}
