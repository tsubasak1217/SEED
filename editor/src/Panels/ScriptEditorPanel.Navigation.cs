using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using SEEDEditor.Panels.ScriptEditor;
using SEEDEditor.Panels.ScriptEditor.DiskSync;
using SEEDEditor.Panels.ScriptEditor.Navigation;

namespace SEEDEditor.Panels;

/// <summary>
/// スクリプトエディタの「戻る／進む」（ナビゲーション履歴）部分。
///
/// Visual Studio の Navigate Backward / Forward を手本にしている。
/// 参照をたどって別ファイルへ飛んだあと元の場所に戻る、編集した箇所を順に辿る、
/// といった往復を、ファイルをまたいで行えるようにする。
///
/// 【設計】
/// - 履歴本体（積む・まとめる・戻る・進む・上限・消えたファイルを飛ばす）は
///   WPF / AvalonEdit に依存しない <see cref="ScriptNavigationHistory"/> が持つ（単体テスト対象）。
/// - ここは位置の解決（アンカー ↔ 行桁）と入力の受け口だけを担当するアダプタ。
/// - 履歴には「移動する直前の位置」を積む。移動した先は現在位置なので、
///   戻った瞬間に「進む」側へ乗る。これで両方を辿れる。
///
/// 正典は docs/editor_script_panel.md の「戻る／進む」節。
/// </summary>
public partial class ScriptEditorPanel
{
    /// <summary>桁の既定値（1 起点なので行頭は 1）。</summary>
    private const int NavDefaultColumn = 1;

    /// <summary>戻る／進むの履歴本体。</summary>
    private readonly ScriptNavigationHistory _navHistory = new();

    /// <summary>
    /// 履歴の記録を抑止している最中か。
    /// 履歴による移動・ディスクからの再読み込み・整形のように、
    /// 「利用者の移動ではないのにキャレットが飛ぶ」処理の間だけ立てる
    /// （そのままだと必ず偽の点が記録され、しかも「進む」履歴が捨てられる）。
    /// 直接代入せず <see cref="WithNavigationSuppressed"/> を使うこと。
    /// </summary>
    private bool _navSuppress;

    /// <summary>
    /// 履歴の記録を抑止して処理を実行する。
    ///
    /// 入れ子で呼ばれても外側の抑止を解除しない（直前の値へ戻す）。
    /// 履歴移動 → タブを開く → ディスク点検 → 再読み込み、のように
    /// 抑止区間が重なる経路が実在するため、単純な true/false 代入では壊れる。
    /// </summary>
    /// <param name="body">抑止中に実行する処理。</param>
    private void WithNavigationSuppressed(Action body)
    {
        bool previous = _navSuppress;
        _navSuppress = true;
        try { body(); }
        finally { _navSuppress = previous; }
    }

    /// <summary>
    /// 直前に履歴へ記録した「編集した箇所」。
    /// ここから十分離れた場所を編集したときだけ新しい点として記録する。
    /// </summary>
    private INavPosition? _lastEditPosition;

    // ── 位置の表現 ────────────────────────────────────────────

    /// <summary>
    /// 開いているドキュメントの中の位置を、AvalonEdit のアンカーで保持する履歴点。
    ///
    /// 上の行が編集されても行がずれないので、「編集しながら行き来する」使い方でも
    /// 記録した場所を指し続ける。ドキュメントが閉じる・本文が丸ごと差し替わる前に
    /// <see cref="Freeze"/> で行・桁の固定値へ落とす（アンカーはそこで壊れるため）。
    /// </summary>
    private sealed class AnchorNavPosition : INavPosition
    {
        /// <summary>本文に追従する位置。</summary>
        private readonly TextAnchor _anchor;

        /// <summary>ファイルの絶対パス。</summary>
        public string FilePath { get; }

        /// <summary>アンカーが指す現在の行（1 起点）。</summary>
        public int Line => _anchor.Line;

        /// <summary>アンカーが指す現在の桁（1 起点）。</summary>
        public int Column => _anchor.Column;

        /// <summary>
        /// ドキュメントの指定オフセットにアンカーを作って履歴点にする。
        /// </summary>
        /// <param name="filePath">ファイルの絶対パス。</param>
        /// <param name="document">対象ドキュメント。</param>
        /// <param name="offset">アンカーを置くオフセット。</param>
        public AnchorNavPosition(string filePath, TextDocument document, int offset)
        {
            FilePath = filePath;
            _anchor  = document.CreateAnchor(Math.Clamp(offset, 0, document.TextLength));
            // その行が消されてもアンカーごと消えないようにする
            // （消えたら Line の取得で例外になるため、履歴では必ず生かす）
            _anchor.SurviveDeletion = true;
        }

        /// <summary>現在の行・桁を固定値として切り出す。</summary>
        /// <returns>行・桁を固定した履歴点。</returns>
        public NavPosition Freeze() => new(FilePath, Line, Column);
    }

    /// <summary>
    /// 指定ドキュメントの指定行・桁に、本文へ追従する履歴点を作る。
    /// </summary>
    /// <param name="doc">対象ドキュメント。</param>
    /// <param name="line">行（1 起点）。</param>
    /// <param name="column">桁（1 起点）。</param>
    /// <returns>履歴点。</returns>
    private static INavPosition CreateNavPosition(DocTab doc, int line, int column)
    {
        var document    = doc.Editor.Document;
        int clampedLine = Math.Clamp(line, 1, Math.Max(1, document.LineCount));
        var docLine     = document.GetLineByNumber(clampedLine);
        int clampedCol  = Math.Clamp(column, NavDefaultColumn, docLine.Length + 1);
        return new AnchorNavPosition(
            doc.FilePath, document, docLine.Offset + (clampedCol - NavDefaultColumn));
    }

    /// <summary>いまアクティブなドキュメントのキャレット位置を履歴点にする（無ければ null）。</summary>
    /// <returns>現在位置の履歴点。</returns>
    private INavPosition? CurrentNavPosition()
    {
        var doc = _activeDoc;
        if (doc is null) return null;
        var caret = doc.Editor.TextArea.Caret;
        return CreateNavPosition(doc, caret.Line, caret.Column);
    }

    /// <summary>
    /// 指定ファイルの履歴点をすべて行・桁の固定値へ落とす。
    ///
    /// タブを閉じるとき・ディスクから本文を読み直すときに呼ぶ。
    /// アンカーは本文の総入れ替えで先頭へ寄ってしまうため、その前に確定させる。
    /// </summary>
    /// <param name="filePath">対象ファイルの絶対パス。</param>
    private void FreezeNavigationAnchors(string filePath)
    {
        _navHistory.Replace(position => FreezeIfSameFile(position, filePath));
        // 履歴の外に持っている「直前に記録した編集位置」も同じ扱いにする
        if (_lastEditPosition is not null)
            _lastEditPosition = FreezeIfSameFile(_lastEditPosition, filePath);
    }

    /// <summary>
    /// 指定ファイルのアンカー位置なら行・桁の固定値へ落とす（違えばそのまま返す）。
    /// </summary>
    /// <param name="position">対象の履歴点。</param>
    /// <param name="filePath">落とし込む対象のファイル絶対パス。</param>
    /// <returns>変換後の履歴点。</returns>
    private static INavPosition FreezeIfSameFile(INavPosition position, string filePath)
        => position is AnchorNavPosition anchored
           && string.Equals(anchored.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
            ? anchored.Freeze()
            : position;

    // ── 記録 ──────────────────────────────────────────────────

    /// <summary>
    /// いまの位置を「移動する直前の位置」として履歴に記録する。
    /// 定義へ移動（F12）・エラー一覧からのジャンプ・デバッガの行表示・検索など、
    /// 明示的なジャンプの直前に呼ぶ。
    /// </summary>
    private void RecordCurrentNavPoint()
    {
        if (_navSuppress) return;
        var position = CurrentNavPosition();
        if (position is not null) _navHistory.Record(position);
    }

    /// <summary>
    /// タブを切り替える直前に、離れる側の現在位置を履歴へ残す。
    /// </summary>
    /// <param name="next">これからアクティブにするドキュメント（閉じる場合は null）。</param>
    private void RecordNavPointOnLeavingActiveDoc(DocTab? next)
    {
        if (_navSuppress) return;
        var leaving = _activeDoc;
        if (leaving is null || ReferenceEquals(leaving, next)) return;
        // 閉じられた直後のタブ（すでに一覧から外れている）にはアンカーを張らない
        if (!_docs.Contains(leaving)) return;
        RecordCurrentNavPoint();
    }

    /// <summary>
    /// キャレットが一定行数以上離れた位置へ飛んだら、飛ぶ直前の位置を履歴に残す。
    /// クリックでの移動・検索の次へ／前へ・PageDown などがここに入る。
    /// 1 行ずつの移動や通常の入力では記録しない。
    /// </summary>
    /// <param name="doc">キャレットが動いたドキュメント。</param>
    private void OnCaretMovedForNavigation(DocTab doc)
    {
        var caret = doc.Editor.TextArea.Caret;
        int previousLine   = doc.NavLastCaretLine;
        int previousColumn = doc.NavLastCaretColumn;

        // 「直前の位置」は抑止中でも更新する（履歴移動の直後に
        //  その移動自体をジャンプとして記録しないため）
        doc.NavLastCaretLine   = caret.Line;
        doc.NavLastCaretColumn = caret.Column;

        if (_navSuppress) return;
        if (previousLine <= 0) return;   // 開いた直後（基準がまだ無い）
        if (Math.Abs(caret.Line - previousLine) < ScriptNavigationHistory.JumpLineThreshold) return;

        _navHistory.Record(CreateNavPosition(doc, previousLine, previousColumn));
    }

    /// <summary>
    /// 直前に記録した編集位置から十分離れた場所を編集したら、そこを履歴に残す。
    /// 「さっきまで直していた箇所」へ戻れるようにするための記録。
    /// </summary>
    /// <param name="doc">編集されたドキュメント。</param>
    private void RecordEditPointIfFar(DocTab doc)
    {
        if (_navSuppress) return;

        var caret  = doc.Editor.TextArea.Caret;
        var here   = new NavPosition(doc.FilePath, caret.Line, caret.Column);
        // 同じ場所を打ち続けている間は記録しない
        if (_lastEditPosition is not null
            && ScriptNavigationHistory.IsSamePlace(_lastEditPosition, here)) return;

        var point = CreateNavPosition(doc, caret.Line, caret.Column);
        _navHistory.Record(point);
        _lastEditPosition = point;
    }

    /// <summary>
    /// ドキュメントの「直前のキャレット位置」を現在値で塗り直す。
    /// 履歴移動や再読み込みでキャレットを動かした直後に呼び、
    /// その移動をジャンプとして記録させない。
    /// </summary>
    /// <param name="doc">対象ドキュメント。</param>
    private static void ResetNavCaretTracking(DocTab doc)
    {
        var caret = doc.Editor.TextArea.Caret;
        doc.NavLastCaretLine   = caret.Line;
        doc.NavLastCaretColumn = caret.Column;
    }

    // ── 移動 ──────────────────────────────────────────────────

    /// <summary>
    /// 指定ファイルの指定位置へ、履歴を記録せずにジャンプする（移動処理の実体）。
    ///
    /// デバッガのステップインでエンジン側ソース（ScriptBridge.cs など、ユーザーの
    /// アセット配下でないスクリプト）へ飛んだ場合は、閲覧は許可するが編集はさせない
    /// （機能保証のため読み取り専用で開く）。ユーザースクリプト（アセット配下）は編集可。
    ///
    /// 利用者が明示的に開こうとしたものではないため、ファイルが無いときは
    /// 「ファイルが見つかりません」タブを作らずに何もしない。
    /// </summary>
    /// <param name="filePath">移動先ファイル。</param>
    /// <param name="line">移動先の行（1 起点。範囲外は丸める）。</param>
    /// <param name="column">移動先の桁（1 起点。範囲外は丸める）。</param>
    /// <returns>移動できたドキュメント。開けなかったら null。</returns>
    private DocTab? JumpTo(string filePath, int line, int column)
    {
        if (IsUserScriptPath(filePath)) OpenFileInternal(filePath, readOnly: false, allowMissing: false);
        else                            OpenFileInternal(filePath, readOnly: true,  allowMissing: false);

        var doc = FindDoc(filePath);
        if (doc is null) return null;

        MoveCaretTo(doc, line, column);
        doc.Editor.Focus();
        return doc;
    }

    /// <summary>
    /// ドキュメント内の指定行・桁へキャレットを移動し、その行を表示する。
    /// 行・桁は現在の本文の範囲へ丸める（再読み込みで短くなっている場合に備える）。
    /// </summary>
    /// <param name="doc">対象ドキュメント。</param>
    /// <param name="line">行（1 起点）。</param>
    /// <param name="column">桁（1 起点）。</param>
    private static void MoveCaretTo(DocTab doc, int line, int column)
    {
        var document = doc.Editor.Document;
        int clampedLine = Math.Clamp(line, 1, Math.Max(1, document.LineCount));
        var docLine     = document.GetLineByNumber(clampedLine);
        // 桁は「行末の 1 つ先」まで許す（キャレットは行末に置けるため）
        int clampedCol  = Math.Clamp(column, NavDefaultColumn, docLine.Length + 1);
        doc.Editor.CaretOffset = docLine.Offset + (clampedCol - NavDefaultColumn);
        doc.Editor.ScrollToLine(clampedLine);
    }

    /// <summary>
    /// 履歴を 1 つ戻る／進む。
    /// 移動そのものは新しい点として記録しない（抑止フラグで止める）。
    /// </summary>
    /// <param name="forward">true なら進む、false なら戻る。</param>
    private void NavigateHistory(bool forward)
    {
        // タブを全て閉じた直後は現在位置が無い。その場合も履歴は辿れる
        // （積むものが無いだけで、閉じたファイルを開き直して移動できる）。
        var current = CurrentNavPosition();

        var target = forward
            ? _navHistory.GoForward(current, IsNavPositionAvailable)
            : _navHistory.GoBack(current, IsNavPositionAvailable);
        if (target is null) return;

        WithNavigationSuppressed(() =>
        {
            // 消えたファイルは IsNavPositionAvailable で弾いてあるので、
            // ここで「ファイルが見つかりません」タブが増えることはない。
            var doc = JumpTo(target.FilePath, target.Line, target.Column);
            if (doc is not null) ResetNavCaretTracking(doc);
        });
    }

    /// <summary>
    /// その履歴点へ移動できるか（ファイルが実在するか）。
    ///
    /// 開いているタブが「ファイルが見つかりません」状態なら移動先として無効にする。
    /// 閉じているファイルはディスクを見る。
    /// </summary>
    /// <param name="position">判定する履歴点。</param>
    /// <returns>移動できるなら true。</returns>
    private bool IsNavPositionAvailable(INavPosition position)
    {
        var open = FindDoc(position.FilePath);
        if (open is not null) return open.DiskStatus != ScriptDiskStatus.Missing;
        return File.Exists(position.FilePath);
    }

    // ── 入力 ──────────────────────────────────────────────────

    /// <summary>
    /// マウスのサイドボタンで戻る／進む（Visual Studio と同じ割り当て）。
    /// パネル全体のトンネリングで拾うので、パネルの外では反応しない。
    /// </summary>
    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        switch (e.ChangedButton)
        {
            case MouseButton.XButton1:   // 手前側 = 戻る
                NavigateHistory(forward: false);
                e.Handled = true;
                break;
            case MouseButton.XButton2:   // 奥側 = 進む
                NavigateHistory(forward: true);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 戻る／進むのキー割り当てを処理する。
    ///
    /// - Ctrl+-        : 戻る（Visual Studio と同じ）
    /// - Ctrl+Shift+-  : 進む（Visual Studio と同じ）
    /// - Alt+←／Alt+→ : 戻る／進む（ブラウザ風。既存の割り当てと衝突しない）
    ///
    /// Alt+↑↓ は行移動、Ctrl+Alt+↑↓ は垂直カーソルで使用済みなので、
    /// Alt 単独の左右だけを取る。
    /// </summary>
    /// <param name="e">キーイベント。</param>
    /// <returns>ここで処理したら true（呼び出し側は以降の処理を行わない）。</returns>
    private bool TryHandleNavigationKey(KeyEventArgs e)
    {
        // Alt 併用時、WPF は e.Key=Key.System・実キーを e.SystemKey に入れる
        var key       = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Alt 単独 + ← / →
        if (modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right)
        {
            NavigateHistory(forward: key == Key.Right);
            e.Handled = true;
            return true;
        }

        // Ctrl（+Shift）+ - （テンキーの - も同じ扱いにする）
        bool isMinus = key is Key.OemMinus or Key.Subtract;
        if (isMinus && modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
        {
            NavigateHistory(forward: modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return true;
        }

        return false;
    }
}
