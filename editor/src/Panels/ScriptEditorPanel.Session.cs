// ============================================================
//  ScriptEditorPanel.Session.cs — 開いていたタブのセッション復元（保存の契機とエディタへの再適用）
//
//  【役割】
//  スクリプトエディタが開いていたタブを、エディタの再起動をまたいで保つ。
//  保存する内容は「開いていたタブのパス（表示順）・アクティブなタブ・
//  各タブのキャレット（行・桁）と縦スクロール位置・読み取り専用だったか」。
//
//  【責務の分け方】
//    ・JSON の読み書き、パスの相対化・絶対化、値の正規化、復元枚数の頭打ち
//        → SEEDEditor.Panels.ScriptEditor.Session.ScriptEditorSessionStore
//          （WPF 非依存・editor/tests/TextEditorLogicTests の対象）
//    ・いつ保存するか（デバウンス）、どうエディタへ戻すか
//        → このファイル（WPF / AvalonEdit に依存する部分だけ）
//
//  【保存の契機】
//  タブを「開いた」「閉じた」「切り替えた」の 3 つだけ。
//  スクリプトエディタに**タブの並べ替え機能は無い**（表示順 ＝ _docs の挿入順 ＝ 開いた順。
//  タブ一覧 UI である OpenDocumentsPanel も並べ替えを持たない）ので、
//  「並べ替えた」という契機は存在しない。並べ替えを足すときはここに契機を追加すること。
//
//  キャレット移動のたびには保存しない（1 文字動かすたびにファイル I/O が走る）。
//  代わりに **書く瞬間に現在のキャレット・スクロールを読み直す**ので、
//  デバウンス後に書かれる値は常に最新になる。
//
//  【復元の契機】
//  「アセットルートの設定（SetAssetsPath）」と「書式・配色の読み込み（InitSettings）」の
//  **両方が済んだ直後**。MainWindow は SetAssetsPath → InitSettings の順に 1 度ずつ呼ぶが、
//  片方だけ済んだ時点で復元すると次のものを取りこぼす:
//    ・SetAssetsPath 前 … Roslyn ワークスペースが無く、復元したタブだけ補完・F12 が効かない
//    ・InitSettings 前 … BreakpointStore がまだ無く、復元したタブにブレークポイントが戻らない
//                        （書式・配色は InitSettings が全タブへ配り直すので自動的に直る）
//  そのため呼び出し順に依存しない「両方そろったら 1 度だけ走る」形にしてある。
//  いずれにせよ LoadLayout より前なので、AvalonDock のレイアウト復元とは競合しない。
//
//  復元では次の 2 つを必ず守る:
//    ・復元で開いたタブが保存を誘発しないよう _suppressSessionSave を立てる
//      （立てないと、復元 → ActivateDoc → 保存要求、が延々と回る）
//    ・エディタへフォーカスを移さない（_suppressEditorFocus）。
//      AvalonDock のアクティブドキュメントも触らない。起動直後に
//      シーンビューから前面を奪うのは「前回の続きから始める」という目的を超えている。
//
//  【ヘッドレス起動では何もしない】
//  MCP・CI から起動したヘッドレスエディタがセッションを書くと、
//  利用者が対話エディタで開いていたタブ構成を黙って壊す。読み書きとも行わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using SEEDEditor.Panels.ScriptEditor.Session;

namespace SEEDEditor.Panels;

public partial class ScriptEditorPanel
{
    // ── 定数（マジックナンバー回避）────────────────────────────

    /// <summary>
    /// 保存デバウンス時間（ms）。
    /// タブを連続で開く・閉じる（復元直後やブランチ切り替え後）に毎回書かず、
    /// 手が止まってから 1 回だけ書く。プロジェクトパネル（500ms）より少し長いのは、
    /// こちらの契機が「タブ操作」だけで、取りこぼしても終了時の Flush で必ず書けるため。
    /// </summary>
    private const int SessionSaveDebounceMs = 700;

    // ── 状態 ─────────────────────────────────────────────────

    /// <summary>
    /// セッションの保存・復元を行ってよいか。
    /// <see cref="RestoreSession"/> が「ヘッドレスでない」「プロジェクトルートが分かる」を
    /// 確かめたときだけ true になる。false の間は**一切ファイルへ触らない**
    /// （ヘッドレス起動が利用者のセッションを壊さないための要）。
    /// </summary>
    private bool _sessionEnabled;

    /// <summary>保存先の絶対パス（<see cref="_sessionEnabled"/> が true のときだけ有効）。</summary>
    private string? _sessionFilePath;

    /// <summary>保存時にパスを相対化する基準（プロジェクトルート）。</summary>
    private string? _sessionProjectRoot;

    /// <summary>保存デバウンス用タイマ（初回の保存要求で UI スレッド上に作る）。</summary>
    private DispatcherTimer? _sessionSaveTimer;

    /// <summary>
    /// 保存要求を無視する区間か。復元中（タブをプログラムから開いている最中）に
    /// 保存が走ると、復元途中の中途半端な状態を書いてしまう。
    /// </summary>
    private bool _suppressSessionSave;

    /// <summary>
    /// エディタへのフォーカス移動を抑止している最中か。
    /// <see cref="ActivateDoc"/> は通常エディタへフォーカスを移すが、
    /// 復元は「利用者がタブを選んだ」わけではないので、起動直後の入力先を奪わない。
    /// </summary>
    private bool _suppressEditorFocus;

    /// <summary>アセットルートの設定（<see cref="SetAssetsPath"/>）が済んだか。</summary>
    private bool _sessionAssetsReady;

    /// <summary>書式・配色とストア類の初期化（<see cref="InitSettings"/>）が済んだか。</summary>
    private bool _sessionSettingsReady;

    /// <summary>復元を 1 度試したか（二重に開き直さないための一方通行のフラグ）。</summary>
    private bool _sessionRestoreAttempted;

    /// <summary>
    /// このセッションで 1 枚でもタブを持ったことがあるか。
    ///
    /// <para>
    /// 「タブが 0 枚」になったときに保存ファイルを消してよいかの判断に使う。
    /// 利用者が全部閉じた結果の 0 枚なら消すのが正しいが、
    /// 復元しようとした全タブが開けなかった（ファイルがロック中・権限不足）結果の 0 枚で消すと、
    /// **一時的な理由で前回のタブ構成を永久に失う**。後者では前回の記録をそのまま残す。
    /// </para>
    /// </summary>
    private bool _sessionHadTabs;

    // ── 初期化の待ち合わせ ────────────────────────────────────

    /// <summary>
    /// アセットルートが設定されたことを伝える（<see cref="SetAssetsPath"/> の末尾から呼ぶ）。
    /// </summary>
    private void NotifyAssetsPathReadyForSession()
    {
        _sessionAssetsReady = true;
        TryRestoreSession();
    }

    /// <summary>
    /// 書式・配色とストア類が初期化されたことを伝える（<see cref="InitSettings"/> の末尾から呼ぶ）。
    /// </summary>
    private void NotifySettingsReadyForSession()
    {
        _sessionSettingsReady = true;
        TryRestoreSession();
    }

    /// <summary>
    /// 前提（アセットルート・設定）が両方そろっていれば、1 度だけ復元を走らせる。
    /// </summary>
    private void TryRestoreSession()
    {
        if (!_sessionAssetsReady || !_sessionSettingsReady) return;
        if (_sessionRestoreAttempted) return;
        _sessionRestoreAttempted = true;
        RestoreSession();
    }

    // ── 保存 ─────────────────────────────────────────────────

    /// <summary>
    /// 保存を予約する（デバウンス）。UI スレッドから呼ぶこと。
    /// タブを開く・閉じる・切り替えるの 3 経路がここへ集まる。
    /// </summary>
    private void RequestSessionSave()
    {
        if (!_sessionEnabled || _suppressSessionSave) return;

        // 「全部閉じた」と「そもそも開けなかった」を区別するための記録
        //（詳細は _sessionHadTabs のコメント）。
        if (_docs.Count > 0) _sessionHadTabs = true;

        if (_sessionSaveTimer is null)
        {
            _sessionSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(SessionSaveDebounceMs),
            };
            _sessionSaveTimer.Tick += (_, _) => FlushSessionState();
        }

        // Stop → Start で「最後の変更から SessionSaveDebounceMs 後」に倒す。
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    /// <summary>
    /// 予約中の保存を打ち切って即座に書き出す。
    /// エディタ終了時（MainWindow の Closing）から呼ぶ。
    /// </summary>
    public void FlushSessionState()
    {
        _sessionSaveTimer?.Stop();
        if (!_sessionEnabled || _sessionFilePath is null) return;

        try
        {
            // タブを 1 枚も開いていないのも「前回の状態」。
            // 古い内容を残すと、全部閉じて終了したのに次回また開いてしまう。
            // ただし「このセッションで一度もタブを持てなかった」場合は、
            // 復元に失敗しただけの可能性があるので前回の記録を残す（_sessionHadTabs 参照）。
            if (_docs.Count == 0)
            {
                if (!_sessionHadTabs) return;
                if (!ScriptEditorSessionStore.Delete(_sessionFilePath, out var deleteError))
                    EditorLog.Write($"スクリプトエディタのセッション削除に失敗しました: {deleteError}");
                return;
            }

            // ★ここで現在のキャレット・スクロールを読み直す。
            //   保存契機（タブ操作）から時間が経っていても、書かれる値は常に最新になる。
            int activeIndex = _activeDoc is null ? -1 : _docs.IndexOf(_activeDoc);
            var document = ScriptEditorSessionStore.Build(
                _sessionProjectRoot, BuildSessionSnapshots(), activeIndex);
            if (document is null) return;   // 保存できるタブが無い（前回の内容はそのまま残す）

            if (!ScriptEditorSessionStore.Save(_sessionFilePath, document, out var saveError))
                EditorLog.Write($"スクリプトエディタのセッション保存に失敗しました: {saveError}");
        }
        catch (Exception ex)
        {
            // セッションの保存失敗でエディタの終了処理を止めない。
            EditorLog.Write($"スクリプトエディタのセッション保存で予期しない失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// 現在のタブ一覧を保存用スナップショット（絶対パス・現在のキャレットとスクロール）へ写し取る。
    /// </summary>
    /// <returns>左から順のスナップショット。</returns>
    private List<ScriptEditorSessionTabSnapshot> BuildSessionSnapshots()
        => _docs.Select(doc => new ScriptEditorSessionTabSnapshot(
                doc.FilePath,
                doc.Editor.TextArea.Caret.Line,
                doc.Editor.TextArea.Caret.Column,
                doc.Editor.VerticalOffset,
                doc.IsReadOnly))
            .ToList();

    // ── 復元 ─────────────────────────────────────────────────

    /// <summary>
    /// 前回開いていたタブを復元する。<see cref="TryRestoreSession"/> から 1 度だけ呼ばれる。
    ///
    /// <para>
    /// 失敗しても例外を外へ出さない。この機能の失敗でエディタが起動しないのは論外なので、
    /// 何か起きたら「復元なし」で静かに続行し、理由だけログへ残す。
    /// </para>
    /// </summary>
    private void RestoreSession()
    {
        // ヘッドレス起動（MCP・CI）は利用者のセッションに触らない。
        if (SEEDEditor.Headless.EditorStartupOptions.IsHeadless) return;

        var projectRoot = SEEDEditor.Project.ProjectContext.RootDir;
        var filePath    = ScriptEditorSessionStore.FilePathFor(projectRoot);
        // プロジェクト未確定（RootDir が空）なら置き場が決まらない＝保存も復元もしない。
        if (filePath is null) return;

        _sessionProjectRoot = projectRoot;
        _sessionFilePath    = filePath;
        _sessionEnabled     = true;

        var document = ScriptEditorSessionStore.LoadFile(filePath, out var warning);
        if (warning is not null) EditorLog.Write(warning);

        var state = ScriptEditorSessionStore.Resolve(document, projectRoot);
        if (state is null) return;   // 前回の記録が無い・全部無効だった → 何も開かない

        // 復元中はタブをプログラムから開くので、保存要求とフォーカス移動を止める。
        _suppressSessionSave = true;
        _suppressEditorFocus = true;
        try
        {
            // 復元は利用者の移動ではないので、戻る／進む履歴に偽の点を積まない。
            WithNavigationSuppressed(() => OpenRestoredTabs(state));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"スクリプトエディタのセッション復元に失敗しました: {ex.Message}");
        }
        finally
        {
            _suppressSessionSave = false;
            _suppressEditorFocus = false;
        }

        // 復元中は RequestSessionSave を止めているので、ここで「タブを持てた」ことを記録する。
        if (_docs.Count > 0) _sessionHadTabs = true;

        EditorLog.Write(
            $"スクリプトエディタのタブを復元しました: {_docs.Count} 枚 / 記録 {state.Tabs.Count} 枚");
    }

    /// <summary>
    /// 復元指示のとおりにタブを開き、アクティブタブを選び直す。
    /// </summary>
    /// <param name="state">復元指示（1 枚以上のタブを含む）。</param>
    private void OpenRestoredTabs(ScriptEditorSessionState state)
    {
        foreach (var tab in state.Tabs)
        {
            if (tab.IsReadOnly)
            {
                // OpenFileReadOnly は allowMissing:false。実体が無ければ何も開かない
                //（＝このタブは黙って消える）ので、開く前に実在を確かめて意図を明示しておく。
                // 読み取り専用タブの中身はエンジン API のソースなど「参照していただけ」のもので、
                // 消えたことを「見つかりません」タブで知らせる価値が薄い。
                if (!SafeFileExists(tab.FilePath)) continue;
                OpenFileReadOnly(tab.FilePath);
            }
            else
            {
                // 実体が無くても開く（OpenFile は allowMissing:true なので
                // 自動的に「ファイルが見つかりません」タブになる）。
                // ブランチを戻せばディスク追従がそのまま通常表示へ戻してくれる。
                OpenFile(tab.FilePath);
            }

            // 読めなかった（ロック中・権限不足）場合はタブが作られない。そのときは諦める。
            var doc = FindDoc(tab.FilePath);
            if (doc is null) continue;

            ApplyRestoredViewState(doc, tab.CaretLine, tab.CaretColumn, tab.ScrollOffset);
        }

        // 保存時にアクティブだったタブを、パネル内部のアクティブタブとして選び直す。
        // ★ドッキング側（AvalonDock）の前面化は行わない。
        //   MainWindow.EnsureScriptEditorDocument / FocusScriptEditorDocument のような
        //   「ドキュメントを前面に出す」処理を呼ぶと、起動直後にシーンビューから前面を奪う。
        //   どのドキュメントを前面にするかは layout.xml が覚えている側の役目。
        var activeDoc = FindDoc(state.Tabs[state.ActiveTabIndex].FilePath);
        if (activeDoc is not null) ActivateDoc(activeDoc);
    }

    /// <summary>
    /// 復元したタブへキャレットとスクロール位置を戻す。
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    /// <param name="line">キャレットの行（1 起点。本文の範囲へ丸められる）。</param>
    /// <param name="column">キャレットの桁（1 起点。本文の範囲へ丸められる）。</param>
    /// <param name="verticalOffset">縦スクロール位置（px）。</param>
    private void ApplyRestoredViewState(DocTab doc, int line, int column, double verticalOffset)
    {
        // キャレットは既存の共通処理へ通す。行・桁を現在の本文の範囲へ丸めてくれるので、
        // 前回より短くなったファイルでも落ちない。行の表示も EditorReveal 経由になる。
        MoveCaretTo(doc, line, column);
        // この移動を「利用者がジャンプした」と誤認して履歴へ積まないよう、追跡値を現在値に揃える。
        ResetNavCaretTracking(doc);

        // スクロール位置は最後に上書きする（EditorReveal はキャレット行を画面中央へ寄せるが、
        // 復元で戻したいのは「前回見えていた範囲」そのもの）。
        ApplyRestoredScroll(doc, verticalOffset);
    }

    /// <summary>
    /// 縦スクロール位置を、エディタのレイアウトが済んでから適用する。
    ///
    /// <para>
    /// AvalonEdit の ScrollTo* 系は、内部の ScrollViewer をテンプレート適用時
    /// （＝最初のレイアウト）に掴む。**一度もレイアウトされていないエディタ**へ呼んでも
    /// 何も起きない（例外も出ない。詳しくは ScriptEditor/EditorReveal.cs の冒頭）。
    /// 復元で作ったタブは、起動直後でまだ画面に出ていない・非アクティブで
    /// ビジュアルツリーに繋がってすらいないため、必ずこの待ち方を通す。
    /// </para>
    ///
    /// <para>
    /// <c>EditorReveal.RevealCaretLine</c> を使い回せないのは、あちらが
    /// 「キャレット行を画面中央へ」に固定されていて、任意のオフセットを取れないため。
    /// 待ち方（Loaded を 1 回だけ購読 → DispatcherPriority.Loaded でもう 1 段遅らせる）は
    /// 意図的に同じにしてある。
    /// </para>
    /// </summary>
    /// <param name="doc">対象タブ。</param>
    /// <param name="verticalOffset">縦スクロール位置（px）。</param>
    private void ApplyRestoredScroll(DocTab doc, double verticalOffset)
    {
        var editor = doc.Editor;

        // 実際のスクロール。タブが既に閉じられていたら何もしない
        //（待っている間に利用者が閉じる可能性がある）。
        void Apply()
        {
            if (!_docs.Contains(doc)) return;
            editor.ScrollToVerticalOffset(verticalOffset);
        }

        // レイアウト済みなら、同じフレームの後続処理（EditorReveal のスクロール）より
        // 後に効かせたいので 1 段だけ遅らせて実行する。
        if (editor.IsLoaded && editor.ActualHeight > 0)
        {
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Apply);
            return;
        }

        // 未レイアウト。Loaded を 1 回だけ待つ（非アクティブタブなら、
        // 利用者がそのタブへ切り替えて初めて発火する＝そのときに正しい位置で出る）。
        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            editor.Loaded -= onLoaded;
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Apply);
        };
        editor.Loaded += onLoaded;
    }

    /// <summary>
    /// ファイルの存在を、例外を投げずに確かめる。
    /// </summary>
    /// <param name="filePath">絶対パス。</param>
    /// <returns>実在すれば true。</returns>
    private static bool SafeFileExists(string filePath)
    {
        try { return File.Exists(filePath); }
        catch { return false; }
    }
}
