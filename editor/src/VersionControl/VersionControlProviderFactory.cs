// ============================================================
//  VersionControlProviderFactory.cs — どのプロバイダを使うかの判定
//
//  【役割】
//  プロジェクトルートを見て、Lore の作業コピーなら LoreProvider を、
//  そうでなければ NullProvider を作る。判定条件を 1 か所に固定する。
//
//  【判定条件】
//  プロジェクトルート直下に `.lore/` フォルダがあるか、それだけ。
//  `.lore/config.toml` の中身までは見ない（壊れていても
//  「バージョン管理下ではある」ので、パネルにはエラーを出して気づかせたい。
//   無いことにしてパネルごと隠すと原因が分からなくなる）。
//
//  【LoreVcs への依存】
//  このファイルは LoreNativeBackend を作るため LoreVcs に間接的に依存する。
//  そのため単体テストへはリンクしない（テストは LoreProvider に偽の
//  ILoreBackend を直接差して検証する）。
// ============================================================

using System;
using SEEDEditor.VersionControl.Abstractions;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Null;
using SEEDEditor.VersionControl.Scheduling;

namespace SEEDEditor.VersionControl;

/// <summary>
/// プロジェクトに合うプロバイダを作る。
/// </summary>
public static class VersionControlProviderFactory
{
    /// <summary>
    /// プロジェクトルートに合わせてプロバイダを作る。
    ///
    /// <para>
    /// 失敗しても例外は投げず <see cref="NullVersionControlProvider"/> を返す
    /// （バージョン管理が動かないことでエディタが起動できなくなってはいけない）。
    /// </para>
    /// </summary>
    /// <param name="projectRootDir">プロジェクトルート（.seedproj のあるフォルダ）。</param>
    /// <param name="settings">設定（省略時は既定値）。</param>
    /// <param name="log">診断ログの出力先（省略可）。</param>
    /// <returns>作られたプロバイダ（常に非 null）。</returns>
    public static IVersionControlProvider Create(
        string? projectRootDir,
        VersionControlSettings? settings = null,
        Action<string>? log = null)
    {
        if (!VersionControlPaths.IsLoreWorkingCopy(projectRootDir))
            return new NullVersionControlProvider(projectRootDir);

        var effectiveSettings = settings ?? VersionControlSettings.Default;

        // 専用ワーカーはプロバイダの所有物にする（プロバイダを閉じれば一緒に止まる）。
        SerialWorkerScheduler? scheduler = null;
        try
        {
            scheduler = new SerialWorkerScheduler(effectiveSettings.ShutdownWait, log);
            var backend = new LoreNativeBackend(projectRootDir!);
            return new LoreProvider(backend, scheduler, effectiveSettings, ownsScheduler: true);
        }
        catch (Exception ex)
        {
            // ネイティブ DLL が見つからない等。バージョン管理なしで起動を続ける。
            log?.Invoke($"[VCS] Lore プロバイダを作れませんでした: {ex.Message}");
            try { scheduler?.Dispose(); } catch { /* 後始末の失敗は無視 */ }
            return new NullVersionControlProvider(projectRootDir);
        }
    }
}
