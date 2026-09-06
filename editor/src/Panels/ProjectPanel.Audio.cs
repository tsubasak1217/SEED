using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Audio;
using SEEDEditor.Dialogs;

namespace SEEDEditor.Panels;

/// <summary>
/// ProjectPanel の音声アセット向け機能（先頭無音カット）を担当する部分クラス。
///
/// 右クリックメニューの項目追加と、設定ダイアログ → 実処理 → 結果表示 → 一覧更新
/// までの流れの取りまとめのみを行う。実際の音声処理は
/// <see cref="AudioSilenceTrimmer"/>、入力収集は <see cref="AudioSilenceTrimWindow"/> が担当する。
/// </summary>
public partial class ProjectPanel
{
    /// <summary>無音カットのメニュー項目ラベル（ダイアログを開くので末尾は三点リーダ）。</summary>
    private const string TrimSilenceMenuHeader = "先頭の無音をカット…";

    /// <summary>
    /// 右クリックメニューへ音声ファイル向けの項目を足す。
    ///
    /// .wav / .mp3 を単一選択しているときは実行可能な項目を、
    /// それ以外の音声ファイル（.ogg / .flac 等）では「機能はあるが未対応」と分かるよう
    /// 無効化した項目を出す。音声以外のファイルでは何も足さない。
    /// </summary>
    /// <param name="menu">項目を足す対象のコンテキストメニュー。</param>
    private void AddAudioMenuItems(ContextMenu menu)
    {
        if (_selectedItems.Count != 1) return;
        if (_selectedItems.First().Tag is not string path) return;
        if (!File.Exists(path)) return;
        if (!AudioSilenceTrimmer.IsAudioFile(path)) return;

        bool processable = AudioSilenceTrimmer.IsProcessable(path);
        var  item        = new MenuItem
        {
            Header      = TrimSilenceMenuHeader,
            IsEnabled   = processable,
            // 未対応形式のときは、なぜ押せないのかをツールチップで補足する。
            ToolTip     = processable ? null : "この形式には未対応です（対応: .wav / .mp3）",
        };
        if (processable) item.Click += (_, _) => TrimAudioSilence(path);

        menu.Items.Add(item);
        menu.Items.Add(new Separator());
    }

    /// <summary>
    /// 無音カットの設定ダイアログを開き、OK なら処理を実行して結果を表示する。
    /// </summary>
    /// <param name="path">対象音声ファイルの絶対パス。</param>
    private void TrimAudioSilence(string path)
    {
        var dialog = new AudioSilenceTrimWindow(Path.GetFileName(path))
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Options == null) return;

        var result = AudioSilenceTrimmer.Trim(path, dialog.Options);

        // 結果はログにも残す（後から確認できるように）。
        EditorLog.Write($"[AudioTrim] {Path.GetFileName(path)}: {result.Status} - "
                      + result.Message.Replace(Environment.NewLine, " ").Replace("\n", " "));

        ShowTrimResult(path, result);

        // 書き出したときだけ一覧を更新する。
        // （FileSystemWatcher でも拾えるが、別名保存の新規ファイルを即座に見せるため明示的に更新する）
        if (result.Saved) RefreshFileGrid();
    }

    /// <summary>
    /// 無音カットの結果をメッセージボックスで提示する。
    /// 成功時はカット量と出力先を、失敗・スキップ時は理由をそのまま出す。
    /// </summary>
    /// <param name="sourcePath">対象ファイルの絶対パス。</param>
    /// <param name="result">処理結果。</param>
    private void ShowTrimResult(string sourcePath, AudioTrimResult result)
    {
        var owner = Window.GetWindow(this);
        string title = TrimSilenceMenuHeader.TrimEnd('…');

        if (!result.Saved)
        {
            var icon = result.Status == AudioTrimStatus.Failed || result.Status == AudioTrimStatus.FileLocked
                ? MessageBoxImage.Warning
                : MessageBoxImage.Information;
            MessageBox.Show(owner, $"{Path.GetFileName(sourcePath)}\n\n{result.Message}",
                            title, MessageBoxButton.OK, icon);
            return;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine($"{Path.GetFileName(sourcePath)} の無音をカットしました。");
        text.AppendLine();
        text.AppendLine($"先頭カット : {result.RemovedLeadingMs:0.#} ms");
        if (result.RemovedTrailingMs > 0)
            text.AppendLine($"末尾カット : {result.RemovedTrailingMs:0.#} ms");
        text.AppendLine($"出力の長さ : {result.OutputDurationMs:0.#} ms");
        text.AppendLine();
        text.AppendLine($"出力先 : {result.OutputPath}");
        if (result.BackupPath != null)
            text.AppendLine($"退避先 : {result.BackupPath}");

        MessageBox.Show(owner, text.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
