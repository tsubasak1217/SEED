// ============================================================
//  LocalizationPanel.DiskSync.cs — 置き場の外部変更の監視と帯（docs/localization.md §15）
//
//  【振る舞い】
//    - 置き場の *.json がディスク上で変わったら（テキストエディタ・AI・VCS の取得など）、
//      未保存の変更が無ければ黙って読み直す（選んでいた行・絞り込みは保つ）
//    - 未保存の変更があれば上書きせず、帯で知らせる（［読み直す（編集を捨てる）］［このまま編集を続ける］）。
//      続けて保存すると、ディスクの変更はパネルの中身で上書きされる
//  判定（デバウンス・中身のハッシュの比較・自分の保存を数えない）は IO/LocaleFolderWatcher が持つ。
//  帯の色はスクリプトエディタの通知帯（ScriptDiskNoticeBar）と同じ（色表の NOTICE_BAR_*）。
// ============================================================

using System.Collections.Generic;
using System.Windows;
using SEEDEditor.Localization.IO;
using SEEDEditor.Panels.Localization;

namespace SEEDEditor.Panels;

public partial class LocalizationPanel
{
    /// <summary>置き場の外部変更の監視。</summary>
    private LocaleFolderWatcher? _watcher;

    /// <summary>監視を用意する（コンストラクタから）。パネルが画面から外れても監視は続ける（隠したパネルも未保存を持つため）。</summary>
    private void InitDiskSync()
    {
        _watcher = new LocaleFolderWatcher(Dispatcher);
        _watcher.ExternalChange += OnExternalChange;
    }

    /// <summary>置き場の監視を始める（開き直すたび）。</summary>
    /// <param name="folder">置き場の絶対パス。</param>
    private void WatchFolder(string folder) => _watcher?.Watch(folder);

    /// <summary>今のディスクの中身を「知っている中身」にする（保存の直後。自分の書き込みを外部変更に数えない）。</summary>
    private void AcceptDiskState() => _watcher?.AcceptCurrent();

    /// <summary>監視をやめる（エディタの終了時に MainWindow から）。</summary>
    public void StopWatching() => _watcher?.Dispose();

    /// <summary>外部の変更を見つけた: 未保存が無ければ読み直し、あれば帯で知らせる。</summary>
    /// <param name="files">変わったファイル名。</param>
    private void OnExternalChange(IReadOnlyList<string> files)
    {
        if (_folder is null) return;
        EditorLog.Write($"{LogPrefix} ディスク上で変わりました: {string.Join(", ", files)}");
        if (!HasUnsavedChanges)
        {
            ReloadFromDisk();
            return;
        }
        NoticeText.Text = LocalizationPanelMessages.ChangedOnDisk(files);
        NoticeBar.Visibility = Visibility.Visible;
    }

    /// <summary>帯を畳む。</summary>
    private void HideNotice() => NoticeBar.Visibility = Visibility.Collapsed;

    /// <summary>帯の［読み直す（編集を捨てる）］。</summary>
    private void OnNoticeReloadClick(object sender, RoutedEventArgs e)
    {
        HideNotice();
        ReloadFromDisk();
    }

    /// <summary>帯の［このまま編集を続ける］（次に保存するとパネルの中身で上書きする）。</summary>
    private void OnNoticeKeepClick(object sender, RoutedEventArgs e) => HideNotice();
}
