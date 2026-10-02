// ============================================================
//  LocaleFolderWatcher.cs — 多言語の置き場の外部変更の監視（FileSystemWatcher を最小で）
//
//  【流れ】（docs/localization.md §15。Reload/PrefabAutoReloader と同じ流儀を小さくしたもの）
//    1. FileSystemWatcher のイベント（スレッドプール）を Dispatcher で UI スレッドへ集め、タイマーを張り直す
//    2. 最後のイベントから Debounce 静まったら、置き場の *.json の中身の控え（LocaleFolderSnapshot）を取り直す
//    3. 読めないファイル（書き込み中）があれば少し待って取り直す（上限 UnreadableRetryLimit 回）
//    4. 知っている控えと違えば ExternalChange で知らせ、控えを今のものにする（同じ変更を 2 度知らせない）
//  パネルが読み込み・保存を終えたら AcceptCurrent で控えを取り直す（自分の書き込みを変更に数えない）。
//  フォルダが無い間は監視しない（Watch をもう一度呼ぶと始める）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using SEEDEditor.Reload;

namespace SEEDEditor.Localization.IO;

/// <summary>多言語の置き場の外部変更の監視。</summary>
public sealed class LocaleFolderWatcher : IDisposable
{
    /// <summary>最後のイベントからこの時間静まったら調べる（ミリ秒。スクリプト・シーン・プレハブの自動再読込と同じ値）。</summary>
    private const int DebounceMs = 600;

    /// <summary>読めないファイルがあったときに取り直す上限（回）。超えたら読めないまま比べる。</summary>
    private const int UnreadableRetryLimit = 5;

    /// <summary>監視するファイルの絞り込み（一時ファイルからの置き換えも拾うため全部を見て、比べるときに *.json に絞る）。</summary>
    private const string WatchFilter = "*";

    /// <summary>デバウンスのタイマー（UI スレッド）。</summary>
    private readonly DispatcherTimer _timer;

    /// <summary>UI スレッドの Dispatcher。</summary>
    private readonly Dispatcher _dispatcher;

    /// <summary>ファイル監視（監視していなければ null）。</summary>
    private FileSystemWatcher? _watcher;

    /// <summary>監視している置き場。</summary>
    private string _folder = string.Empty;

    /// <summary>知っている控え（最後に読んだ・保存した・知らせたときのもの）。</summary>
    private LocaleFolderSnapshot _known = LocaleFolderSnapshot.Empty;

    /// <summary>読めないファイルで取り直した回数。</summary>
    private int _retries;

    /// <summary>外部の変更を見つけた（変わったファイル名。UI スレッドで呼ぶ）。</summary>
    public event Action<IReadOnlyList<string>>? ExternalChange;

    /// <summary>監視の準備をする（まだ監視しない。Watch で始める）。</summary>
    /// <param name="dispatcher">UI スレッドの Dispatcher。</param>
    public LocaleFolderWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DebounceMs),
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// 置き場の監視を始める（別の置き場を監視していればやめて替える）。今の中身を「知っている控え」にする。
    /// </summary>
    /// <param name="folder">置き場の絶対パス（無いフォルダなら監視しない）。</param>
    public void Watch(string folder)
    {
        Stop();
        _folder = folder;
        AcceptCurrent();
        if (!Directory.Exists(folder)) return;

        try
        {
            _watcher = new FileSystemWatcher(folder, WatchFilter)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // 監視を始められない（ネットワークのフォルダなど）。この機能だけ止め、手動の再読み込みは使える
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>今の中身を「知っている控え」にする（読み込み・保存の直後に呼ぶ）。</summary>
    public void AcceptCurrent()
    {
        _known = _folder.Length == 0 ? LocaleFolderSnapshot.Empty : LocaleFolderSnapshot.Capture(_folder, FileContentHash.TryCompute);
        _retries = 0;
    }

    /// <summary>監視をやめる。</summary>
    public void Stop()
    {
        _timer.Stop();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _timer.Tick -= OnTimerTick;
    }

    /// <summary>ファイル監視のイベント（スレッドプール）→ UI スレッドでタイマーを張り直す。</summary>
    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_watcher is null) return;   // 止めた後に届いたイベント
            _timer.Stop();
            _timer.Start();
        });

    /// <summary>静まった: 控えを取り直して比べる。</summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_folder.Length == 0) return;

        var current = LocaleFolderSnapshot.Capture(_folder, FileContentHash.TryCompute);
        if (current.HasUnreadable && _retries < UnreadableRetryLimit)
        {
            // 書き込みの途中。少し待って取り直す
            _retries++;
            _timer.Start();
            return;
        }
        _retries = 0;

        var changed = current.ChangedSince(_known);
        if (changed.Count == 0) return;
        _known = current;
        ExternalChange?.Invoke(changed);
    }
}
