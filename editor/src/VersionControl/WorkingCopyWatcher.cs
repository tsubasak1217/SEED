// ============================================================
//  WorkingCopyWatcher.cs — 作業コピーの変化を見て状態の取り直しを促す
//
//  【役割】
//  プロジェクトフォルダ配下でファイルが増減・改名・書き換えられたら
//  <see cref="VersionControlService.RequestRefresh"/> を呼ぶだけの小さな見張り。
//
//  【なぜプロジェクトパネルの監視に相乗りしないのか】
//  ProjectPanel にも FileSystemWatcher はあるが、
//    ・監視対象が `assets/` だけ（プロジェクト直下の設定ファイル等が入らない）
//    ・NotifyFilter が FileName / DirectoryName だけで **内容の書き換えを拾わない**
//  という作りで、バージョン管理が知りたい「保存してファイルの中身が変わった」を
//  拾えない。ProjectPanel 側の NotifyFilter を広げると、書き込みのたびに
//  ファイルグリッド全体が再構築され、あちらの挙動を変えてしまう。
//  そこで責務ごとに別の見張りを持つ（どちらも自分の関心事だけを見る）。
//
//  【無視するフォルダ】
//  ビルド成果物・キャッシュ・セーブデータ・ログ・バックアップ、そして Lore 自身の
//  `.lore/`。特に `.lore/` は **Lore の操作そのものが書き換える**ため、
//  無視しないと「取り直す → .lore が変わる → また取り直す」の無限ループになる。
//
//  【デバウンス】
//  ここでは行わない。<see cref="VersionControlService.RequestRefresh"/> が
//  設定値（StatusDebounce）で束ねるので、二重にタイマーを持たない。
//  FileSystemWatcher は 1 回の保存で複数イベントを出すが、すべてそこで潰れる。
//
//  【スレッド】
//  FileSystemWatcher のイベントはスレッドプールから来る。
//  ここでは UI を触らず RequestRefresh を呼ぶだけなので Dispatcher は要らない
//  （UI への反映は VersionControlService.StatusChanged の購読側が行う）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない。
// ============================================================

using System;
using System.IO;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl;

/// <summary>
/// 作業コピーを見張り、変化があったら状態の取り直しを予約する。
/// </summary>
public sealed class WorkingCopyWatcher : IDisposable
{
    /// <summary>
    /// 見張りから外すフォルダ名（パスのどこかにこの名前の階層があれば無視する）。
    ///
    /// <para>
    /// データドリブン: 除外を増やしたいときはこの配列に 1 行足すだけでよい。
    /// </para>
    /// </summary>
    private static readonly string[] IGNORED_DIRECTORY_NAMES =
    {
        // Lore 自身のメタデータ。ここを見ると自分の操作で自分が起動して無限ループになる。
        ".lore",
        "cache",
        "save",
        "logs",
        "build",
        ".backup",
    };

    /// <summary>パスの区切り（比較用に統一する）。</summary>
    private const char PATH_SEPARATOR = '/';

    /// <summary>内側の見張り。</summary>
    private readonly FileSystemWatcher _watcher;

    /// <summary>取り直しのモード（既定は走査つきオフライン＝サーバ往復なし）。</summary>
    private readonly StatusRefreshMode _mode;

    /// <summary>診断ログの出力先。</summary>
    private readonly Action<string>? _log;

    /// <summary>破棄済みか。</summary>
    private bool _disposed;

    /// <summary>
    /// 見張りを始める。
    /// </summary>
    /// <param name="workingCopyRoot">作業コピーのルート（= プロジェクトルート）。</param>
    /// <param name="mode">取り直しのモード。</param>
    /// <param name="log">診断ログの出力先（省略可）。</param>
    /// <exception cref="ArgumentException">ルートが空、または存在しないとき。</exception>
    public WorkingCopyWatcher(
        string workingCopyRoot,
        StatusRefreshMode mode = StatusRefreshMode.ScanOffline,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(workingCopyRoot) || !Directory.Exists(workingCopyRoot))
            throw new ArgumentException("作業コピーのルートが不正です。", nameof(workingCopyRoot));

        _mode = mode;
        _log  = log;

        _watcher = new FileSystemWatcher(Path.GetFullPath(workingCopyRoot))
        {
            IncludeSubdirectories = true,

            // 中身の書き換え（LastWrite）も見る。バージョン管理が知りたいのは
            // 「保存してファイルが変わった」であり、名前の増減だけでは足りない。
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size,
        };

        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error   += OnError;

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>作成・削除・書き換えを受けて取り直しを予約する。</summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnChanged(object sender, FileSystemEventArgs e) => Request(e.FullPath);

    /// <summary>
    /// 改名を受けて取り直しを予約する。
    /// 移動前・移動後のどちらかが対象内なら取り直す（片方だけ無視対象のことがある）。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!Request(e.FullPath)) Request(e.OldFullPath);
    }

    /// <summary>
    /// 見張り自体が落ちたとき（バッファ溢れなど）。
    /// 取りこぼした可能性があるので、ひとまず取り直しを予約する。
    /// </summary>
    /// <param name="sender">送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnError(object sender, ErrorEventArgs e)
    {
        _log?.Invoke($"[VCS] ファイル監視でエラーが発生しました: {e.GetException().Message}");
        if (!_disposed) VersionControlService.RequestRefresh(_mode);
    }

    /// <summary>
    /// 無視対象でなければ取り直しを予約する。
    /// </summary>
    /// <param name="fullPath">変化したパス。</param>
    /// <returns>予約したか（無視したときは偽）。</returns>
    private bool Request(string fullPath)
    {
        if (_disposed) return false;
        if (IsIgnored(fullPath)) return false;

        VersionControlService.RequestRefresh(_mode);
        return true;
    }

    /// <summary>
    /// 無視対象のパスか判定する（区切りを統一してフォルダ名の完全一致で見る）。
    ///
    /// <para>
    /// 部分一致にすると `save` が `saved_scenes` に、`build` が `buildings` に
    /// 誤って当たる。必ず階層名の完全一致で判定する。
    /// </para>
    /// </summary>
    /// <param name="fullPath">判定するパス。</param>
    public static bool IsIgnored(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        var normalized = fullPath.Replace('\\', PATH_SEPARATOR);
        var segments   = normalized.Split(PATH_SEPARATOR, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            foreach (var ignored in IGNORED_DIRECTORY_NAMES)
            {
                if (string.Equals(segment, ignored, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>見張りを止めて片付ける。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error   -= OnError;
            _watcher.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[VCS] ファイル監視の停止に失敗しました: {ex.Message}");
        }
    }
}
