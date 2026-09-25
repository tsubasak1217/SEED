// ============================================================
//  AndroidAssetOverlaySync.cs — 端末の上書き層（files/assets）へ、端末の中身と違うアセットだけを送る（docs/android.md §23）
//
//  【流れ】
//    1. 候補を決める
//         エディタの差し替え（PushChangedAsync）… 変わったファイルを起点に参照をたどった閉包（AssetCollector.CollectFrom。
//                                                シーンを保存したら、シーンと参照するアセット）
//         SeedAndroid の push --assets（PushFolderAsync）… 今 pak を作ると入るもの（登録シーン・プロジェクト設定＋
//                                                アセットの中の全シーンを起点。SeedPak と同じ収録の規則）のうち、指定のフォルダの中
//    2. 差分を選ぶ（AndroidOverlayPlanner）: 手元の中身（pak に入れるときと同じ形）と端末の中身（送った記録 → APK の pak）を比べる
//         APK の pak と比べるのは、記録の土台にした pak（run のときに LaunchStep が大きさと更新時刻を記録）が、いまの
//         置き場の pak（runtime/android/app/src/main/assets/seed/assets.pak）と同じときだけ。違う・記録が無いときは
//         pak と比べずに「送ったことが無いものは送る」（多めに送るだけで、古いものが残ることはない）
//    3. 送る（run-as の tar。置いてあるものは消さず、同じパスは上書き。AdbClient.RunAsExtractTarIntoAsync）
//    4. 記録を更新する（送ったアセットの SHA-256。State/AndroidAssetOverlayState）
//  端末で取り込ませる（RELOAD_*）のは呼び出し側（AndroidHotReloadApplier・SeedAndroid の reload）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.State;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.Packaging;
using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Android.HotReload;

/// <summary>上書き層へ送った結果。</summary>
/// <param name="Plan">差分の選び方の結果（送ったもの・同じだったもの・読めなかったもの）。</param>
/// <param name="CandidateCount">候補の数。</param>
/// <param name="PakBaselineNote">APK の pak と比べなかった理由（比べたなら null）。</param>
/// <param name="Elapsed">かかった時間（候補の決定・比較・転送）。</param>
public sealed record AndroidOverlaySyncResult(
    AndroidOverlayPlan Plan, int CandidateCount, string? PakBaselineNote, TimeSpan Elapsed);

/// <summary>上書き層への差分の転送。</summary>
public sealed class AndroidAssetOverlaySync
{
    /// <summary>pak と比べない理由: 起動の記録が無い。</summary>
    public const string NoRecordNote = "起動の記録（asset_overlay.json）が無いため、APK の pak とは比べずに送ったことの無いものを送ります";

    /// <summary>pak と比べない理由: 置き場の pak が起動のときと違う。</summary>
    public const string PakChangedNote = "置き場の pak が端末へ入れたときと違う（その後にビルドした等）ため、APK の pak とは比べずに送ったことの無いものを送ります";

    /// <summary>pak と比べない理由: 置き場の pak を読めない。</summary>
    private const string PakUnreadableFormat = "置き場の pak を読めないため、APK の pak とは比べずに送ったことの無いものを送ります（{0}）";

    /// <summary>シーンファイルの拡張子（push --assets の起点に全シーンを足す）。</summary>
    private const string SceneSearchPattern = "*.scene";

    /// <summary>runtime/src のフォルダ名（SeedPak と同じくエンジン内蔵の assets:// 参照を起点に足す）。</summary>
    private const string RuntimeSourceDirName = "src";

    /// <summary>adb。</summary>
    private readonly AdbClient _adb;

    /// <summary>端末のシリアル。</summary>
    private readonly string _serial;

    /// <summary>アプリ ID。</summary>
    private readonly string _applicationId;

    /// <summary>プロジェクト。</summary>
    private readonly AndroidProjectInfo _project;

    /// <summary>エンジン側の置き場（APK の pak の置き場）。</summary>
    private readonly AndroidEnginePaths _engine;

    /// <summary>必要なものを指定して作る。</summary>
    /// <param name="adb">adb。</param>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="project">プロジェクト（アセットルート・記録の置き場）。</param>
    /// <param name="engine">エンジン側の置き場。</param>
    public AndroidAssetOverlaySync(AdbClient adb, string serial, string applicationId, AndroidProjectInfo project, AndroidEnginePaths engine)
    {
        _adb = adb;
        _serial = serial;
        _applicationId = applicationId;
        _project = project;
        _engine = engine;
    }

    /// <summary>アセットルート。</summary>
    private string AssetsRoot => _project.Folder.AssetsRoot;

    /// <summary>
    /// 変わったファイルを起点に参照をたどり、端末と違うものを送る（エディタの差し替え）。
    /// </summary>
    /// <param name="seeds">変わったファイル（アセットルートからの相対パス）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    public Task<AndroidOverlaySyncResult> PushChangedAsync(IReadOnlyCollection<string> seeds, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var collector = new AssetCollector(AssetsRoot, LoadSettings());
        var candidates = collector.CollectFrom(seeds).Included.Select(asset => asset.RelPath).ToList();
        return SyncAsync(candidates, started, cancellationToken);
    }

    /// <summary>
    /// 指定のフォルダの中で、今 pak を作ると入るもの（＋アセットの中の全シーンから辿れるもの）のうち端末と違うものを送る
    /// （SeedAndroid の push --assets）。
    /// </summary>
    /// <param name="folder">フォルダ（アセットルートかその中。絶対パス）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>結果。</returns>
    /// <exception cref="ArgumentException">フォルダがアセットルートの外。</exception>
    public Task<AndroidOverlaySyncResult> PushFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var prefix = FolderPrefix(AssetsRoot, folder)
                     ?? throw new ArgumentException($"フォルダはアセットルート（{AssetsRoot}）かその中を指定してください: {folder}", nameof(folder));
        var scenes = Directory.EnumerateFiles(AssetsRoot, SceneSearchPattern, SearchOption.AllDirectories)
            .Select(path => AssetPathUtil.ToRelative(AssetsRoot, path))
            .OfType<string>()
            .ToList();
        var runtimeSource = Path.Combine(_engine.RuntimeDir, RuntimeSourceDirName);
        var collector = new AssetCollector(AssetsRoot, LoadSettings(), Directory.Exists(runtimeSource) ? runtimeSource : null);
        var candidates = collector.Collect(scenes).Included
            .Select(asset => asset.RelPath)
            .Where(relative => prefix.Length == 0 || relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return SyncAsync(candidates, started, cancellationToken);
    }

    /// <summary>
    /// フォルダのアセットルートからの相対の接頭辞（"textures/"。アセットルートそのものなら ""。外なら null。純粋な処理）。
    /// </summary>
    /// <param name="assetsRoot">アセットルート。</param>
    /// <param name="folder">フォルダ。</param>
    /// <returns>接頭辞。</returns>
    public static string? FolderPrefix(string assetsRoot, string folder)
    {
        var root = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var relative = AssetPathUtil.ToRelative(root, full);
        return relative is null || relative.Length == 0 ? null : relative.TrimEnd('/') + "/";
    }

    /// <summary>
    /// APK の pak を比べる土台にしてよいか（記録の pak の目印が、いまの置き場の pak と同じか。純粋な処理）。
    /// </summary>
    /// <param name="record">端末の記録（無ければ null）。</param>
    /// <param name="current">いまの置き場の pak の目印。</param>
    /// <returns>使えるなら null、使えなければ理由。</returns>
    public static string? PakBaselineProblem(AndroidAssetOverlayRecord? record, (long? Size, DateTime? WriteTimeUtc) current)
    {
        if (record is null) return NoRecordNote;
        if (record.PakSize is null || record.PakWriteTimeUtc is null || current.Size is null) return PakChangedNote;
        return record.PakSize == current.Size && record.PakWriteTimeUtc == current.WriteTimeUtc ? null : PakChangedNote;
    }

    /// <summary>候補から差分を選んで送り、記録を更新する。</summary>
    private async Task<AndroidOverlaySyncResult> SyncAsync(
        IReadOnlyList<string> candidates, Stopwatch started, CancellationToken cancellationToken)
    {
        var statePath = AndroidAssetOverlayState.PathFor(_project, _engine);
        var state = AndroidAssetOverlayState.Load(statePath);
        var record = state.RecordFor(_serial, _applicationId);
        var pushedFiles = record?.Files ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── APK の pak を比べる土台にできるか ──
        var pakPath = Path.Combine(_engine.ApkPackageDir, PackageLayout.PakFileName);
        var note = PakBaselineProblem(record, AndroidAssetOverlayState.PakIdentity(pakPath));
        AndroidPakContentIndex? pak = null;
        if (note is null)
        {
            try
            {
                pak = AndroidPakContentIndex.Open(pakPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                note = string.Format(System.Globalization.CultureInfo.InvariantCulture, PakUnreadableFormat, ex.Message);
            }
        }

        // ── 差分を選ぶ（端末の中身: 送った記録 → APK の pak）──
        var plan = AndroidOverlayPlanner.Plan(
            candidates,
            relative => AndroidLocalAssetReader.Read(AssetsRoot, relative),
            relative => pushedFiles.TryGetValue(relative, out var digest) ? digest : pak?.DigestOf(relative));

        if (plan.ToPush.Count > 0)
        {
            // ── 送る（置いてあるものは消さない）──
            var payloads = plan.ToPush
                .Select(item => new RunAsTarPayload(item.Relative, item.Local.Content, item.Local.SourcePath))
                .ToList();
            await _adb.RunAsExtractTarIntoAsync(
                _serial, _applicationId, AndroidRuntimeContract.RemoteAssetsDir, payloads[0].Name,
                (stream, token) => RunAsTarArchive.WritePayloadsAsync(payloads, stream, token),
                cancellationToken).ConfigureAwait(false);

            // ── 記録を更新する（土台の pak の目印は run のときのまま）──
            var files = new Dictionary<string, string>(pushedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.ToPush) files[item.Relative] = item.Local.Digest;
            state.Devices[_serial] = new AndroidAssetOverlayRecord
            {
                ApplicationId = _applicationId,
                PakSize = record?.PakSize,
                PakWriteTimeUtc = record?.PakWriteTimeUtc,
                Files = files,
                UpdatedAt = DateTimeOffset.Now,
            };
            state.Save(statePath);
        }
        return new AndroidOverlaySyncResult(plan, candidates.Count, note, started.Elapsed);
    }

    /// <summary>収録の規則（packaging_settings.json の assets 節。無ければ既定）を読む（SeedPak と同じ）。</summary>
    private AssetPackagingSettings LoadSettings() =>
        PackagingData.LoadFrom(Path.Combine(AssetsRoot, PackagingData.SettingsFileName)).Assets;
}
