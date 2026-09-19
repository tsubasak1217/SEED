using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SEEDEditor.Panels.ScriptEditor;
using SEEDEditor.Panels.ScriptEditor.DiskSync;

namespace SEEDEditor.Panels;

/// <summary>
/// スクリプトエディタの「開いているタブをディスクの状態へ追従させる」部分。
///
/// 【なぜ必要か】
/// バージョン管理（Lore）でブランチをマージ・切り替え・最新取得すると、
/// 開いているスクリプトがディスク上で書き換わったり消えたりする。
/// タブが開いた瞬間の中身を持ち続けると、
/// 「別ブランチの中身を編集して保存 → 取り込んだ変更を丸ごと巻き戻す」事故になる。
///
/// 【仕組み】
/// 1. 監視 — 開いているタブのフォルダを <see cref="ScriptDiskWatcher"/> で見張り、
///    デバウンスした 1 回の通知で全タブを点検する。
///    取りこぼし対策として、エディタのウィンドウが再びアクティブになったときにも点検する。
/// 2. 判定 — ［ファイルの有無］×［内容が基準と同じか］×［未保存の編集］から
///    <see cref="ScriptDiskState.Decide"/>（純粋関数・単体テスト対象）が操作を 1 つ決める。
/// 3. 適用 — ここが表示の切り替えと本文の読み直しを行う。
///
/// 正典は docs/editor_script_panel.md の「ディスク追従」節。
/// </summary>
public partial class ScriptEditorPanel
{
    /// <summary>開いているタブのファイルを見張る監視役（パネルと同じ寿命）。</summary>
    private ScriptDiskWatcher? _diskWatcher;

    /// <summary>Activated を購読しているウィンドウ（二重購読を防ぐために覚えておく）。</summary>
    private Window? _hostWindow;

    /// <summary>
    /// ディスク追従を開始する（コンストラクタから 1 回だけ呼ぶ）。
    ///
    /// ウィンドウのアクティブ化の購読は、まだ親が決まっていないので Loaded まで遅らせる。
    /// パネル自体はアプリと同じ寿命なので、監視の破棄はプロセス終了に委ねる
    /// （ドッキングの入れ替えで Unloaded が飛ぶたびに監視を切ると取りこぼす）。
    /// </summary>
    private void InitDiskSync()
    {
        _diskWatcher = new ScriptDiskWatcher(Dispatcher, CheckDocsAgainstDisk);
        Loaded += OnPanelLoadedForDiskSync;
    }

    /// <summary>
    /// パネルが画面に載ったら、親ウィンドウのアクティブ化を購読する。
    /// ドッキングの入れ替えで複数回呼ばれるため、同じウィンドウなら何もしない。
    /// </summary>
    private void OnPanelLoadedForDiskSync(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null || ReferenceEquals(window, _hostWindow)) return;

        if (_hostWindow is not null) _hostWindow.Activated -= OnHostWindowActivated;
        _hostWindow = window;
        _hostWindow.Activated += OnHostWindowActivated;
    }

    /// <summary>
    /// エディタのウィンドウが再びアクティブになったときの全タブ点検。
    ///
    /// 監視の取りこぼし対策。外部ツール（バージョン管理クライアント・別のエディタ）で
    /// 作業して戻ってきた瞬間に必ず整合が取れるようにする。フォルダごと消された場合は
    /// 監視を張れないため、その復帰もここが拾う。
    /// </summary>
    private void OnHostWindowActivated(object? sender, EventArgs e) => CheckDocsAgainstDisk();

    /// <summary>
    /// 監視対象のフォルダを、いま開いているタブの構成に合わせて張り直す。
    /// タブを開いた・閉じた・保存した直後に呼ぶ。
    /// </summary>
    private void RefreshDiskWatchTargets()
        => _diskWatcher?.SetWatchedFiles(_docs.Select(d => d.FilePath));

    // ── 点検と適用 ────────────────────────────────────────────

    /// <summary>
    /// 開いている全タブをディスクと突き合わせて、必要な操作を適用する。
    /// 監視のデバウンス後とウィンドウのアクティブ化から呼ばれる。
    /// </summary>
    private void CheckDocsAgainstDisk()
    {
        // 監視の張り直しもここで通す。フォルダごと消されて無効になった監視や、
        // フォルダが後から作られて張れるようになった分を拾い直すため
        // （点検が走る＝何かが変わった瞬間なので、張り直す契機としてちょうどよい）。
        RefreshDiskWatchTargets();
        // 適用の途中でタブが閉じられても安全なようにスナップショットで回す
        foreach (var doc in _docs.ToList()) CheckDocAgainstDisk(doc);
    }

    /// <summary>
    /// 1 つのタブをディスクと突き合わせて、必要な操作を適用する。
    /// </summary>
    /// <param name="doc">点検するタブ。</param>
    private void CheckDocAgainstDisk(DocTab doc)
    {
        bool    exists = File.Exists(doc.FilePath);
        string? hash   = exists ? TryComputeFileHash(doc.FilePath) : null;

        // 実体はあるのに読めない（保存の途中・ロック中）。
        // ここで「変わった」と決め打つと中途半端な内容を読み込みかねないので判断を保留し、
        // 次の監視イベントかウィンドウのアクティブ化で改めて点検する。
        if (exists && hash is null) return;

        var action = ScriptDiskState.Decide(new ScriptDiskInputs(
            ExistsOnDisk:        exists,
            DiskMatchesBaseline: exists && hash == doc.DiskBaseline,
            HasUnsavedEdits:     doc.IsDirty,
            Current:             doc.DiskStatus));

        ApplyDiskAction(doc, action);
    }

    /// <summary>
    /// 判定結果をタブへ適用する。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    /// <param name="action">適用する操作。</param>
    private void ApplyDiskAction(DocTab doc, ScriptDiskAction action)
    {
        switch (action)
        {
            case ScriptDiskAction.None:
                break;

            case ScriptDiskAction.Reload:
                ReloadFromDisk(doc);
                break;

            case ScriptDiskAction.Restore:
                // 実際の結果（成功・失敗）は ReloadFromDisk 側が出すので、ここは意図だけを書く
                EditorLog.Write($"ファイルが戻ったので再読み込みします: {doc.FilePath}");
                ReloadFromDisk(doc);
                break;

            case ScriptDiskAction.ShowChangedNotice:
                ApplyDiskStatus(doc, ScriptDiskStatus.ChangedOnDisk);
                break;

            case ScriptDiskAction.ShowDeletedNotice:
                EditorLog.Write(
                    "ファイルがディスク上から削除されましたが、未保存の編集があるため"
                  + $"タブを残しています: {doc.FilePath}");
                ApplyDiskStatus(doc, ScriptDiskStatus.DeletedOnDisk);
                break;

            case ScriptDiskAction.ShowMissing:
                EditorLog.Write($"ファイルがディスク上から見つかりません: {doc.FilePath}");
                ApplyDiskStatus(doc, ScriptDiskStatus.Missing);
                break;

            case ScriptDiskAction.ClearNotice:
                ApplyDiskStatus(doc, ScriptDiskStatus.Normal);
                break;
        }
    }

    /// <summary>
    /// タブの表示状態を切り替える（何度呼んでも同じ結果になる冪等な処理）。
    ///
    /// - 本文の差し替え（エディタ ↔「ファイルが見つかりません」）
    /// - 上部の通知帯の出し入れ
    /// - 編集可否の更新
    /// - 「タブ」パネルへの通知
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    /// <param name="status">切り替え先の状態。</param>
    private void ApplyDiskStatus(DocTab doc, ScriptDiskStatus status)
    {
        if (doc.DiskStatus == status) return;
        doc.DiskStatus = status;

        // 本文（エディタ or 見つかりません表示）
        doc.Body.Child = status == ScriptDiskStatus.Missing ? doc.MissingView : doc.EditorArea;

        // 通知帯
        switch (status)
        {
            case ScriptDiskStatus.ChangedOnDisk:
                doc.Notice.ShowChangedOnDisk(
                    reload:      () => ForceReloadFromDisk(doc),
                    keepEditing: () => AcceptDiskContentWithoutReload(doc));
                break;
            case ScriptDiskStatus.DeletedOnDisk:
                doc.Notice.ShowDeletedOnDisk();
                break;
            default:
                doc.Notice.HideNotice();
                break;
        }

        // 消えたタブは編集不可にする（表示していなくても状態として揃えておく）
        UpdateEditability();
        // 「タブ」パネルの行の見た目（薄い色・ツールチップ）を更新する
        DocumentsChanged?.Invoke();
    }

    /// <summary>
    /// 通知帯の［再読み込み（編集を捨てる）］。
    /// 未保存の編集を捨てて、ディスクの内容で作り直す。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    private void ForceReloadFromDisk(DocTab doc)
    {
        if (!File.Exists(doc.FilePath))
        {
            // 帯を出してから消えた。判定し直せば「削除」の帯へ移る。
            CheckDocAgainstDisk(doc);
            return;
        }
        ReloadFromDisk(doc);
    }

    /// <summary>
    /// 通知帯の［このまま編集を続ける］。
    ///
    /// 本文は一切触らず、ディスクの現在の内容を「了承済み」として基準値に据える。
    /// こうしないと、次の監視イベントやウィンドウのアクティブ化のたびに
    /// 同じ帯が出続けて作業の邪魔になる。ディスクがさらに変わればまた帯が出る。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    private void AcceptDiskContentWithoutReload(DocTab doc)
    {
        // 読めなければ基準値は据え置く（次の点検で改めて判定される）
        var hash = TryComputeFileHash(doc.FilePath);
        if (hash is not null) doc.DiskBaseline = hash;
        ApplyDiskStatus(doc, ScriptDiskStatus.Normal);
    }

    /// <summary>
    /// ディスクの内容でタブの本文を作り直す。
    ///
    /// キャレットとスクロール位置はできるだけ保つ。Undo 履歴は意味を失うので捨てる。
    /// ブレークポイントは行番号で控えてから張り直す（本文の総入れ替えで
    /// アンカーが 1 行目へ寄ってしまうため）。
    /// 読み込み直後の後処理（ワークスペース同期・診断・意味着色）も通す。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    private void ReloadFromDisk(DocTab doc)
    {
        string text;
        try { text = File.ReadAllText(doc.FilePath); }
        catch (Exception ex)
        {
            EditorLog.Write($"再読み込みに失敗しました [{Path.GetFileName(doc.FilePath)}]: {ex.Message}");
            return;
        }

        var editor = doc.Editor;

        // 本文の総入れ替えはキャレットを端へ飛ばすため、Caret.PositionChanged 経由で
        // 「ジャンプした」と誤認されて履歴に偽の点が積まれる。しかも記録は
        // 「進む」履歴を捨てるので、ブランチ切り替えで何タブも再読込すると
        // 戻る／進むが壊れる。IsSyncingFromDisk は TextChanged しか止めないため、
        // ここでは履歴側の抑止フラグも併せて立てる。
        WithNavigationSuppressed(() => ReloadDocumentBody(doc, editor, text));

        RunPostLoadPasses(doc);
    }

    /// <summary>
    /// 再読み込みの本体（本文の差し替えと、本文に紐づく位置情報の復元）。
    /// 呼び出し側が履歴記録の抑止を掛けた状態で呼ぶこと。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    /// <param name="editor">対象タブのエディタ。</param>
    /// <param name="text">ディスクから読んだ新しい本文。</param>
    private void ReloadDocumentBody(DocTab doc, ICSharpCode.AvalonEdit.TextEditor editor, string text)
    {
        // ── 差し替え前に、本文へ紐づいている位置情報を控える ──
        int    caretLine   = editor.TextArea.Caret.Line;
        int    caretColumn = editor.TextArea.Caret.Column;
        double verticalOffset   = editor.VerticalOffset;
        double horizontalOffset = editor.HorizontalOffset;
        var    breakpointLines  = doc.Breaks?.Lines().ToList();
        // 履歴のアンカーは総入れ替えで壊れるので、行・桁の固定値へ落とす
        FreezeNavigationAnchors(doc.FilePath);

        // ── 本文を差し替える ──
        doc.IsSyncingFromDisk = true;
        try
        {
            editor.Document.Text = text;
            // 差し替え前の編集を「元に戻す」で復活させられてしまうと
            // ディスクとの食い違いが分からなくなるため、履歴ごと捨てる
            editor.Document.UndoStack.ClearAll();
        }
        finally
        {
            doc.IsSyncingFromDisk = false;
        }

        // ── 差し替え後の状態を整える ──
        // 診断（波線・エラー一覧）は差し替え前の本文に対する結果で、オフセットも行番号も
        // もう合っていない。後処理の再解析が終わるまで古い結果を見せないよう先に消す。
        ApplyDiagnostics(doc, Array.Empty<MarkedDiagnostic>());
        doc.DiskBaseline = HashOfText(text);
        SetDirty(doc, false);
        // 保存済みの内容になったので、クラッシュ復元の退避は不要
        _recovery?.Remove(doc.FilePath);
        // 「見つかりません」「変更されました」の表示を解除して通常表示へ戻す
        ApplyDiskStatus(doc, ScriptDiskStatus.Normal);

        // ブレークポイントを控えた行で張り直す（新しい行数を超える行は落ちる）
        if (breakpointLines is not null && doc.Breaks is not null)
        {
            doc.Breaks.ResetTo(breakpointLines);
            doc.BpMargin?.InvalidateVisual();

            // 行が落ちた／ずれた結果をデバッガと永続化へ伝える。
            // これをしないと、デバッグ中に再読み込みが走ったときに
            // netcoredbg 側だけ古い行を持ち続けて別の行で止まる。
            var lines = doc.Breaks.Lines();
            _breakpointStore?.Set(doc.FilePath, lines);
            _breakpointStore?.Save();
            BreakpointsChanged?.Invoke(doc.FilePath, lines);
        }

        // キャレットを元の行・桁へ戻す（範囲外なら丸められる）
        MoveCaretTo(doc, caretLine, caretColumn);
        // 直前のキャレット位置を現在値に揃える（呼び出し側の抑止と合わせて、
        // この移動が「ジャンプ」として記録されないようにする）
        ResetNavCaretTracking(doc);

        // スクロール位置の復元は、新しい本文でレイアウトが終わってから行う
        // （差し替え直後は行数が未確定で、指定しても丸められてしまう）
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_docs.Contains(doc)) return;
            editor.ScrollToVerticalOffset(verticalOffset);
            editor.ScrollToHorizontalOffset(horizontalOffset);
        });
    }

    // ── 内容ハッシュ ──────────────────────────────────────────

    /// <summary>
    /// ファイルの内容ハッシュを、例外を投げずに求める。
    ///
    /// 保存中・ロック中でも読めるように共有指定を緩める。読めなければ null を返し、
    /// 呼び出し側が判断を保留する。改行コードや BOM の違いを「変更」として扱いたいが
    /// エンコーディングの解釈は揃えたいので、復号したテキストに対して取る
    /// （書き込み側も同じテキストからハッシュを取るため、自分の保存が必ず一致する）。
    /// </summary>
    /// <param name="path">対象ファイルの絶対パス。</param>
    /// <returns>16 進のハッシュ文字列。読めなければ null。</returns>
    private static string? TryComputeFileHash(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return HashOfText(reader.ReadToEnd());
        }
        catch (Exception)
        {
            // 無い / ロック中 / 権限不足
            return null;
        }
    }

    /// <summary>テキストの内容ハッシュ（SHA-256 の 16 進表記）を求める。</summary>
    /// <param name="text">対象テキスト。</param>
    /// <returns>16 進のハッシュ文字列。</returns>
    private static string HashOfText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
