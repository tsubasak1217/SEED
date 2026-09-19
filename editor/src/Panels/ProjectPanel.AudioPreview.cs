// ============================================================
//  ProjectPanel.AudioPreview.cs — 音声タイルの試聴ボタン
//
//  【役割】
//  音声ファイルのタイルに、ファイル名の横へ小さな再生／停止ボタンを置き、
//  一覧の上で中身を確かめられるようにする。
//  ダブルクリック（＝既定のプレイヤーで開く）とは別の動線で、
//  こちらは「エディタから離れずに聴く」ためのもの。
//
//  【役割分担】
//   ・鳴らす／止める          … Audio/NAudioPreviewPlayer（実デバイス）
//   ・どれが鳴っているかの管理 … Audio/AudioPreviewController（純ロジック・テスト対象）
//   ・ボタンの見た目と後片付け … このファイル（WPF）
//  この分け方のおかげで、状態遷移（切り替え・鳴り終わり・停止）は
//  偽の再生装置を相手に editor/tests/ProjectPanelLogicTests で検証できる。
//
//  【止める契機】
//   ・別のファイルを再生した（Controller が面倒を見る）
//   ・最後まで鳴り終わった（同上）
//   ・フォルダを移動した（RefreshFileGrid）
//   ・Play（ゲーム実行）が始まった（RuntimeManager.StateChanged）
//   ・パネルが画面から外れた／エディタが終了した（Unloaded / MainWindow の Closing）
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Audio;
using SEEDEditor.Controls;
using SEEDEditor.Theme;

namespace SEEDEditor.Panels;

public partial class ProjectPanel
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>
    /// 試聴ボタンのアイコン一辺（px）。当たり判定はこの 1.5 倍（下限 18px）。
    /// 当初は 11px だったが「一回り大きく」の要望（2026-09-20）で 14px にした
    /// （当たり判定は 18px → 21px）。
    /// </summary>
    private const double AudioPreviewIconSize = 14;

    /// <summary>試聴ボタンとファイル名の間隔（px）。ボタンは名前の左に置く。</summary>
    private const double AudioPreviewButtonGap = 3;

    /// <summary>再生中に出すアイコン（押すと止まる）。</summary>
    private const string AudioPreviewStopIconKey = "Icon.Stop";

    /// <summary>停止中に出すアイコン（押すと鳴る）。</summary>
    private const string AudioPreviewPlayIconKey = "Icon.Play";

    /// <summary>停止中のツールチップ。</summary>
    private const string AudioPreviewPlayTooltip = "試聴する";

    /// <summary>再生中のツールチップ。</summary>
    private const string AudioPreviewStopTooltip = "試聴を止める";

    /// <summary>ログ行の接頭辞（grep しやすくするため）。</summary>
    private const string AudioPreviewLogPrefix = "[試聴]";

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>
    /// 試聴の制御役。最初に試聴ボタンが押されたときだけ作る。
    ///
    /// <para>
    /// 起動時に作らないのは、出力デバイスの初期化を「音を聴こうとした人」の
    /// 操作まで遅らせるため（デバイスが無い環境でも起動に影響しない）。
    /// </para>
    /// </summary>
    private AudioPreviewController? _audioPreview;

    /// <summary>
    /// いま画面に出ている試聴ボタン（パス → ボタンのアイコン）。
    ///
    /// <para>
    /// 再生状態が変わったときに見た目を描き直すために持つ。
    /// 一覧を作り直すたびに捨てる（消えたタイルの参照を握り続けないため）。
    /// </para>
    /// </summary>
    private readonly List<(string Path, AppIcon Icon, Button Button)> _audioPreviewButtons = new();

    // ── ボタンの生成 ─────────────────────────────────────────────

    /// <summary>
    /// 音声ファイル用の試聴ボタンを作る。
    ///
    /// <para>
    /// <see cref="Button"/> にしているのは見た目のためだけではない。
    /// ボタンはマウス押下を自分で処理して <c>Handled</c> を立てるので、
    /// タイル側のハンドラ（選択・リネーム予約・ダブルクリック）が動かない。
    /// ドラッグ開始だけはトンネリング（Preview）で先に捕まえられてしまうため、
    /// <see cref="IsInsideButton"/> で別途除外している。
    /// </para>
    /// </summary>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    /// <returns>試聴ボタン。</returns>
    private Button BuildAudioPreviewButton(string audioPath)
    {
        var icon = AppIcon.Create(AudioPreviewPlayIconKey, AudioPreviewIconSize);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment   = VerticalAlignment.Center;

        // 復号器が無くて鳴らせない形式（標準では .ogg）は押せなくし、理由を出す。
        var unsupportedReason = AudioPreviewSupport.UnsupportedReason(audioPath);
        bool canPlay          = unsupportedReason is null;

        var button = new Button
        {
            Content   = icon,
            // 当たり判定はアイコンの 1.5 倍・下限 18px（共通規約）。
            MinWidth  = SeedButtonMetrics.IconHitAreaSize(AudioPreviewIconSize),
            MinHeight = SeedButtonMetrics.IconHitAreaSize(AudioPreviewIconSize),
            Padding   = new Thickness(0),
            // 名前の左に置くので、間隔は右側（名前との間）に取る。
            Margin    = new Thickness(0, 0, AudioPreviewButtonGap, 0),
            VerticalAlignment          = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Cursor      = System.Windows.Input.Cursors.Hand,
            ToolTip     = canPlay ? AudioPreviewPlayTooltip : unsupportedReason,
            IsEnabled   = canPlay,
            // ツールチップは無効なボタンでも出したい（理由が読めないと直しようがない）。
            Focusable   = false,
        };
        ToolTipService.SetShowOnDisabled(button, true);

        // 色・ホバー・押下・無効の見た目は共通書式に任せる（自前で色を決めない）。
        SeedButtonStyle.Apply(button, SeedButtonStyle.ICON);

        if (canPlay) button.Click += (_, _) => OnAudioPreviewClicked(audioPath);

        _audioPreviewButtons.Add((audioPath, icon, button));
        SyncAudioPreviewButton(audioPath, icon, button);
        return button;
    }

    /// <summary>
    /// 一覧を作り直すときに、前の一覧の試聴ボタンへの参照を捨てる。
    /// <see cref="RefreshFileGrid"/> から呼ぶこと（画面から消えた
    /// <see cref="Button"/> を握り続けるとタイルごと解放されない）。
    ///
    /// <para>
    /// 再生そのものは止めない。止めるのは <see cref="StopAudioPreview"/> の役目で、
    /// フォルダ移動時は <see cref="NavigateTo"/> がそちらを呼ぶ。
    /// </para>
    /// </summary>
    private void ClearAudioPreviewButtons() => _audioPreviewButtons.Clear();

    // ── 操作 ─────────────────────────────────────────────────────

    /// <summary>試聴ボタンが押されたときの処理（再生／停止のトグル）。</summary>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    private void OnAudioPreviewClicked(string audioPath)
    {
        var controller = EnsureAudioPreviewController();
        if (controller is null) return;

        var error = controller.Toggle(audioPath);
        if (error is not null)
        {
            // 鳴らせなかった理由は、その場（トースト）とログの両方へ出す。
            // トーストは消えるので、後から追えるようログにも残す。
            ShowProjectToast(error);
            EditorLog.Write($"{AudioPreviewLogPrefix} {error}");
        }

        // 失敗しても成功しても、ボタンの見た目は現在の状態に合わせ直す。
        RefreshAudioPreviewButtons();
    }

    /// <summary>
    /// 試聴を止める（鳴っていなければ何もしない）。
    ///
    /// <para>
    /// フォルダ移動・Play 開始・パネルが画面から外れたとき・エディタ終了から呼ぶ。
    /// </para>
    /// </summary>
    public void StopAudioPreview()
    {
        _audioPreview?.Stop();
        RefreshAudioPreviewButtons();
    }

    /// <summary>
    /// 試聴の制御役を破棄する（エディタ終了時）。
    /// </summary>
    public void DisposeAudioPreview()
    {
        _audioPreview?.Dispose();
        _audioPreview = null;
        ClearAudioPreviewButtons();
    }

    // ── 内部 ─────────────────────────────────────────────────────

    /// <summary>
    /// 制御役を必要になった時点で作る。
    /// 出力装置の生成で失敗しても、ここでは例外にしない（押しても鳴らないだけ）。
    /// </summary>
    /// <returns>制御役。作れなければ null。</returns>
    private AudioPreviewController? EnsureAudioPreviewController()
    {
        if (_audioPreview is not null) return _audioPreview;

        try
        {
            var controller = new AudioPreviewController(new NAudioPreviewPlayer());

            // 鳴り終わり・停止の通知は UI スレッドとは限らないスレッドから来る。
            // ボタンへ触る前に必ずディスパッチャへ渡す。
            controller.PlayingPathChanged += _ =>
                Dispatcher.BeginInvoke(new Action(RefreshAudioPreviewButtons));

            _audioPreview = controller;
            return _audioPreview;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"{AudioPreviewLogPrefix} 試聴の準備に失敗しました: {ex.Message}");
            return null;
        }
    }

    /// <summary>画面に出ている全ボタンの見た目を、現在の再生状態に合わせ直す。</summary>
    private void RefreshAudioPreviewButtons()
    {
        foreach (var (path, icon, button) in _audioPreviewButtons)
            SyncAudioPreviewButton(path, icon, button);
    }

    /// <summary>ボタン 1 個の見た目（アイコンとツールチップ）を状態に合わせる。</summary>
    /// <param name="audioPath">そのボタンが担当する音声ファイル。</param>
    /// <param name="icon">ボタンに載っているアイコン。</param>
    /// <param name="button">ボタン本体。</param>
    private void SyncAudioPreviewButton(string audioPath, AppIcon icon, Button button)
    {
        if (!button.IsEnabled) return;   // 鳴らせない形式は常に再生アイコンのまま

        bool playing  = _audioPreview?.IsPlaying(audioPath) == true;
        icon.IconKey  = playing ? AudioPreviewStopIconKey : AudioPreviewPlayIconKey;
        button.ToolTip = playing ? AudioPreviewStopTooltip : AudioPreviewPlayTooltip;
    }

    /// <summary>
    /// クリック位置がタイル内のボタンの上かどうか。
    ///
    /// <para>
    /// タイルの選択・ドラッグ判定はスクロールビューアのトンネリング
    /// （<c>PreviewMouseLeftButtonDown</c>）で行っているため、
    /// 何もしなければボタンを押した指の小さな揺れでファイルのドラッグが始まってしまう。
    /// ボタンの上から始まった操作は、タイルの操作として扱わない。
    /// </para>
    /// </summary>
    /// <param name="source">イベントの発生元。</param>
    /// <returns>ボタン（またはその子要素）の上なら true。</returns>
    private static bool IsInsideButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
            // タイルまで遡ったら、そこから上は見ない（別のタイルの外枠に当たらないように）。
            if (node is Border border && border.Tag is string) return false;
        }
        return false;
    }
}
