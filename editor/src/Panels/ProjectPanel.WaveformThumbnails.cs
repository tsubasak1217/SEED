// ============================================================
//  ProjectPanel.WaveformThumbnails.cs — 音声タイルの波形サムネイル
//
//  【役割】
//  音声ファイルのタイルのアイコンを、実際の波形を描いた画像へ差し替える。
//  Unity のプロジェクトビューと同じ発想で、「どれが短い効果音で、
//  どれが長い BGM か」「どこが無音か」を一覧のまま見分けられるようにする。
//
//  【3 段構え（モデルサムネイルと同じ考え方）】
//   1. キャッシュ PNG があれば、それを読んで貼る（復号しない）
//   2. 無ければ復号を予約する（UI スレッド外・同時 2 本まで）
//   3. 復号できない・打ち切られた場合は何もしない（形式アイコンのまま）
//
//  【画面に無いものを作り続けない】
//  フォルダを次々に開くと、前のフォルダの復号が走ったままになる。
//  一覧を作り直すたびに打ち切り（CancellationTokenSource）を立て直し、
//  走っている復号を止める（ProjectPanel.ModelThumbnails.cs の
//  InvalidateModelThumbnailRequests と同じ役割）。
//
//  【置き場】
//  <プロジェクト>/cache/editor/waveforms/<ハッシュ>.png
//  規則は Assets/WaveformThumbnailCacheKey.cs が持つ（ここでは組み立てない）。
// ============================================================

using System;
using System.Threading;
using System.Windows.Controls;
using SEEDEditor.Assets;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

public partial class ProjectPanel
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>ログ行の接頭辞（grep しやすくするため）。</summary>
    private const string WaveformLogPrefix = "[波形]";

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>
    /// いまの一覧ぶんの打ち切り。フォルダを移動すると作り直され、
    /// 前のフォルダのために走っていた復号はここで止まる。
    /// </summary>
    private CancellationTokenSource? _waveformCancellation;

    // ── 予約 ─────────────────────────────────────────────────────

    /// <summary>
    /// 音声ファイルの波形サムネイル生成を予約する。
    ///
    /// <para>
    /// キャッシュ PNG があればその場で貼る（復号しない）。無ければ
    /// バックグラウンドで復号し、戻ってきてから UI スレッドで描いて貼る。
    /// どの段でも失敗したら何もしない＝呼び出し側が先に敷いた
    /// 形式アイコン（音声アイコン）がそのまま残る。
    /// </para>
    /// </summary>
    /// <param name="imgCtrl">差し替え先のアイコンコントロール。</param>
    /// <param name="audioPath">音声ファイルの絶対パス。</param>
    private async void ScheduleWaveformThumbnail(Image imgCtrl, string audioPath)
    {
        int width  = WaveformThumbnailCacheKey.DefaultWidthPx;
        int height = WaveformThumbnailCacheKey.DefaultHeightPx;

        // ① キャッシュ PNG があれば復号せずに貼る。
        var cachePath = WaveformThumbnailCacheKey.PathForFile(
            SEEDEditor.Project.ProjectContext.RootDir, audioPath, width, height);

        var cached = WaveformThumbnailRenderer.TryLoadCachedPng(cachePath);
        if (cached is not null)
        {
            ApplyThumbnail(imgCtrl, cached);
            return;
        }

        // ② 無ければ復号を予約する。打ち切りはフォルダ単位。
        var token = EnsureWaveformCancellation();

        try
        {
            var columns = await WaveformThumbnailRenderer
                .DecodeColumnsAsync(audioPath, width, token)
                .ConfigureAwait(true);   // 続きを UI スレッドで行うため true

            // 復号の間にフォルダを移動していたら、もう貼る先が無い。
            if (columns is null || token.IsCancellationRequested) return;

            // 描画は WPF のオブジェクトを作るので UI スレッドで行う
            //（ConfigureAwait(true) でここは UI スレッドに戻っている）。
            var bitmap = WaveformThumbnailRenderer.RenderColumns(columns, width, height);
            if (bitmap is null) return;

            ApplyThumbnail(imgCtrl, bitmap);

            // ③ 次回以降デコードしなくて済むよう PNG で残す。
            //    失敗しても表示は済んでいるので、ログだけ残して続ける。
            if (cachePath is not null && !WaveformThumbnailRenderer.TrySavePng(bitmap, cachePath))
                EditorLog.Write($"{WaveformLogPrefix} キャッシュを保存できませんでした: {cachePath}");
        }
        catch (OperationCanceledException)
        {
            // フォルダ移動による打ち切り。異常ではない。
        }
        catch (Exception ex)
        {
            // 波形が出ないだけで一覧は使えるので、落とさずログに残す。
            EditorLog.Write($"{WaveformLogPrefix} 生成に失敗しました: {audioPath} — {ex.Message}");
        }
    }

    // ── 打ち切り ─────────────────────────────────────────────────

    /// <summary>
    /// いまの一覧ぶんの打ち切りトークンを返す（無ければ作る）。
    /// </summary>
    /// <returns>この一覧のあいだ有効なトークン。</returns>
    private CancellationToken EnsureWaveformCancellation()
    {
        _waveformCancellation ??= new CancellationTokenSource();
        return _waveformCancellation.Token;
    }

    /// <summary>
    /// 走っている波形生成を打ち切る。
    /// <see cref="RefreshFileGrid"/> のように
    /// ファイルグリッドを空にするすべての場所から呼ぶこと
    /// （呼ばないと、もう画面に無いタイルのために復号が走り続ける）。
    /// </summary>
    private void InvalidateWaveformThumbnailRequests()
    {
        var previous = _waveformCancellation;
        _waveformCancellation = null;

        if (previous is null) return;

        // Cancel はするが Dispose はしない。
        // 打ち切った時点で、まだ同時実行数の順番待ちをしている復号がいる可能性があり、
        // そこへ破棄済みのトークンを渡すと ObjectDisposedException になる
        //（打ち切りは正常系なのに、失敗としてログへ出てしまう）。
        // タイマも待機ハンドルも使っていない CancellationTokenSource は
        // 解放が要る資源を持たないので、GC に任せてよい。
        try { previous.Cancel(); } catch { /* 既に打ち切り済み */ }
    }
}
