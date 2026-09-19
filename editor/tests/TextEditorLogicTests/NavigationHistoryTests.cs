using System;
using System.Collections.Generic;
using SEEDEditor.Panels.ScriptEditor.Navigation;
using SpriteRigTests;                // TestHarness / Check（テストランナーは共用）

namespace TextEditorLogicTests;

/// <summary>
/// スクリプトエディタの「戻る／進む」履歴の単体テスト
/// （docs/editor_script_panel.md の「戻る／進む」節）。
///
/// 守りたい性質:
///   1. 「移動する直前の位置」と「移動した先」の両方を辿れる
///   2. 近い点の連続が 1 点にまとまる（1 行ずつの移動で履歴が埋まらない）
///   3. 戻った状態で新しい移動をしたら「進む」側は捨てられる
///   4. 消えたファイルの点は読み飛ばす（「見つかりません」タブを量産しない）
///   5. 上限を超えたら古いものから捨てる（際限なく溜まらない）
/// </summary>
public static class NavigationHistoryTests
{
    /// <summary>テストで使う架空のファイルパス（実在しなくてよい）。</summary>
    private const string FileA = @"C:\proj\assets\Scripts\PlayerMove.cs";
    private const string FileB = @"C:\proj\assets\Scripts\EnemyAI.cs";

    /// <summary>このファイルのテストをランナーへ登録する。</summary>
    /// <param name="harness">登録先のテストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 積む・まとめる ──────────────────────────────────
        harness.Add("離れた点は積み上がる",                                 RecordStacksDistantPoints);
        harness.Add("同じファイルの近い点は 1 点にまとめる",                RecordMergesNearbyPoints);
        harness.Add("別ファイルなら行が近くても別の点として積む",           RecordKeepsDifferentFiles);
        harness.Add("上限を超えたら古いものから捨てる",                     RecordTrimsOldest);

        // ── 戻る・進む ──────────────────────────────────────
        harness.Add("戻ると直前の点へ移り、現在位置が「進む」側へ乗る",     GoBackMovesCurrentToForward);
        harness.Add("戻る → 進む で元の位置に戻れる（往復できる）",         BackThenForwardReturns);
        harness.Add("ファイルをまたいで戻れる",                             GoBackAcrossFiles);
        harness.Add("戻り先が無ければ何も変えずに失敗する",                 GoBackOnEmptyDoesNothing);
        harness.Add("進み先が無ければ何も変えずに失敗する",                 GoForwardOnEmptyDoesNothing);
        harness.Add("タブを全て閉じていても（現在位置が無くても）戻れる",   GoBackWithoutCurrentPosition);

        // ── 「進む」の破棄 ──────────────────────────────────
        harness.Add("戻った状態で新しい移動をすると「進む」側は捨てられる", RecordClearsForward);

        // ── 読み飛ばし ──────────────────────────────────────
        harness.Add("消えたファイルの点は飛ばして次の点へ進む",             GoBackSkipsMissingFiles);
        harness.Add("全部消えていたら戻れない",                             GoBackAllMissing);
        harness.Add("いま居る場所と同じ点は飛ばす",                         GoBackSkipsCurrentPlace);

        // ── 位置の置き換え ──────────────────────────────────
        harness.Add("Replace で全ての点を行・桁の固定値へ落とせる",         ReplaceConvertsAllPoints);

        // ── 判定の境界 ──────────────────────────────────────
        harness.Add("同じ場所とみなす境界（閾値ちょうどは同じ場所）",       SamePlaceBoundary);
        harness.Add("履歴を空にできる（プロジェクト切り替え用）",           ClearEmptiesBothStacks);
    }

    /// <summary>テスト用の位置を作る（桁は行頭固定でよい）。</summary>
    /// <param name="file">ファイルパス。</param>
    /// <param name="line">行（1 起点）。</param>
    /// <returns>位置。</returns>
    private static NavPosition At(string file, int line) => new(file, line, 1);

    /// <summary>どのファイルも実在する扱いにする述語。</summary>
    private static bool AllAvailable(INavPosition _) => true;

    /// <summary>指定ファイルだけ「消えている」扱いにする述語を作る。</summary>
    /// <param name="missingFile">消えている扱いにするファイル。</param>
    /// <returns>移動可否の述語。</returns>
    private static Func<INavPosition, bool> MissingOnly(string missingFile)
        => position => !string.Equals(position.FilePath, missingFile, StringComparison.OrdinalIgnoreCase);

    // ── 積む・まとめる ───────────────────────────────────────

    /// <summary>十分離れた点はそれぞれ別の点として積まれる。</summary>
    private static void RecordStacksDistantPoints()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileA, 100));
        history.Record(At(FileA, 200));
        Check.Equal(3, history.BackCount, "積まれた件数");
    }

    /// <summary>
    /// 同じファイルで閾値以内の点は 1 点にまとめる。
    /// これが無いと、クリックやキー操作のたびに履歴が埋まって使い物にならない。
    /// </summary>
    private static void RecordMergesNearbyPoints()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileA, 12));
        history.Record(At(FileA, 15));
        Check.Equal(1, history.BackCount, "まとめられた件数");

        // まとめた結果は「最新の位置」で置き換わっている
        var target = history.GoBack(At(FileA, 500), AllAvailable);
        Check.True(target is not null, "戻れるはず");
        Check.Equal(15, target!.Line, "置き換え後の行");
    }

    /// <summary>行が近くてもファイルが違えば別の点。</summary>
    private static void RecordKeepsDifferentFiles()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileB, 11));
        Check.Equal(2, history.BackCount, "積まれた件数");
    }

    /// <summary>上限を超えた分は古い方から捨てられ、件数が際限なく増えない。</summary>
    private static void RecordTrimsOldest()
    {
        var history = new ScriptNavigationHistory();
        int overflow = ScriptNavigationHistory.MaxEntries + 10;
        // 必ず別の点になるよう、閾値より広い間隔で積む
        for (int i = 1; i <= overflow; i++)
            history.Record(At(FileA, i * (ScriptNavigationHistory.MergeLineDistance + 1)));

        Check.Equal(ScriptNavigationHistory.MaxEntries, history.BackCount, "上限で頭打ちになる件数");

        // 残っているのは新しい方（最後に積んだ点が直前の点）
        var target = history.GoBack(At(FileB, 1), AllAvailable);
        Check.Equal(overflow * (ScriptNavigationHistory.MergeLineDistance + 1),
            target!.Line, "直前の点は最後に積んだ点");
    }

    // ── 戻る・進む ───────────────────────────────────────────

    /// <summary>戻ると、現在位置が「進む」側へ乗る（＝移動先も辿れる）。</summary>
    private static void GoBackMovesCurrentToForward()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));

        var current = At(FileA, 300);
        var target  = history.GoBack(current, AllAvailable);

        Check.Equal(10, target!.Line, "戻り先の行");
        Check.Equal(0, history.BackCount, "戻る側は空になる");
        Check.Equal(1, history.ForwardCount, "現在位置が進む側へ乗る");
    }

    /// <summary>戻ってから進むと、元の位置に戻ってこられる。</summary>
    private static void BackThenForwardReturns()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));

        var current = At(FileA, 300);
        var back    = history.GoBack(current, AllAvailable);
        var forward = history.GoForward(back!, AllAvailable);

        Check.Equal(300, forward!.Line, "進んだ先の行");
        Check.Equal(1, history.BackCount, "戻る側へ積み直される");
        Check.Equal(0, history.ForwardCount, "進む側は空になる");
    }

    /// <summary>ファイルをまたぐ移動でも戻れる（この機能の主目的）。</summary>
    private static void GoBackAcrossFiles()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 42));      // F12 で飛ぶ直前の位置

        var current = At(FileB, 7);         // 定義先
        var target  = history.GoBack(current, AllAvailable);

        Check.Equal(FileA, target!.FilePath, "戻り先のファイル");
        Check.Equal(42, target.Line, "戻り先の行");
    }

    /// <summary>戻り先が無いときは現在位置を「進む」側へ積まない（押しても何も起きない）。</summary>
    private static void GoBackOnEmptyDoesNothing()
    {
        var history = new ScriptNavigationHistory();
        var target  = history.GoBack(At(FileA, 10), AllAvailable);

        Check.True(target is null, "戻れないので null");
        Check.Equal(0, history.ForwardCount, "進む側は増えない");
        Check.True(!history.CanGoBack, "戻れない");
    }

    /// <summary>進み先が無いときも同様に何も起きない。</summary>
    private static void GoForwardOnEmptyDoesNothing()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));

        var target = history.GoForward(At(FileA, 300), AllAvailable);

        Check.True(target is null, "進めないので null");
        Check.Equal(1, history.BackCount, "戻る側は増えない");
    }

    /// <summary>
    /// タブを全て閉じた直後は「現在位置」が無い。それでも履歴は辿れて、
    /// 閉じたファイルを開き直して移動できること（積むものが無いので進む側は増えない）。
    /// </summary>
    private static void GoBackWithoutCurrentPosition()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileB, 80));

        var target = history.GoBack(current: null, AllAvailable);

        Check.Equal(FileB, target!.FilePath, "戻り先のファイル");
        Check.Equal(80, target.Line, "戻り先の行");
        Check.Equal(0, history.ForwardCount, "積む現在位置が無いので進む側は増えない");
    }

    // ── 「進む」の破棄 ───────────────────────────────────────

    /// <summary>
    /// 戻った状態から新しい移動をしたら、それまでの「進む」側は意味を失うので捨てる
    /// （ブラウザ・Visual Studio と同じ挙動）。
    /// </summary>
    private static void RecordClearsForward()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.GoBack(At(FileA, 300), AllAvailable);
        Check.Equal(1, history.ForwardCount, "前提: 進む側に 1 件ある");

        history.Record(At(FileB, 50));      // 新しい移動

        Check.Equal(0, history.ForwardCount, "進む側は捨てられる");
        Check.True(history.CanGoBack, "戻る側には積まれている");
    }

    // ── 読み飛ばし ───────────────────────────────────────────

    /// <summary>
    /// マージで消えたファイルの点は飛ばして、その先の点へ進む。
    /// これが無いと、戻るたびに「ファイルが見つかりません」タブが増える。
    /// </summary>
    private static void GoBackSkipsMissingFiles()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));      // 生きている
        history.Record(At(FileB, 20));      // 消えている
        history.Record(At(FileB, 200));     // 消えている

        var target = history.GoBack(At(FileA, 900), MissingOnly(FileB));

        Check.Equal(FileA, target!.FilePath, "消えた点を飛ばした先のファイル");
        Check.Equal(10, target.Line, "消えた点を飛ばした先の行");
        Check.Equal(0, history.BackCount, "飛ばした点は履歴から捨てられる");
    }

    /// <summary>全ての点が消えたファイルなら、戻れない（現在位置も動かさない）。</summary>
    private static void GoBackAllMissing()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileB, 20));
        history.Record(At(FileB, 200));

        var target = history.GoBack(At(FileA, 5), MissingOnly(FileB));

        Check.True(target is null, "戻れないので null");
        Check.Equal(0, history.ForwardCount, "現在位置は進む側へ乗らない");
    }

    /// <summary>
    /// いま居る場所と同じ点（＝押しても動かない点）は飛ばして、その先へ進む。
    /// 編集した箇所を記録した直後に「戻る」を押したときの空振りを防ぐ。
    /// </summary>
    private static void GoBackSkipsCurrentPlace()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileB, 300));     // いま居る場所と同じ

        var target = history.GoBack(At(FileB, 302), AllAvailable);

        Check.Equal(FileA, target!.FilePath, "空振りする点を飛ばした先のファイル");
        Check.Equal(10, target.Line, "空振りする点を飛ばした先の行");
    }

    // ── 位置の置き換え ───────────────────────────────────────

    /// <summary>
    /// タブを閉じる・本文を読み直すときに、そのファイルの点を固定値へ落とし込めること
    /// （アンカーは本文の総入れ替えで壊れるため、事前に確定させる必要がある）。
    /// </summary>
    private static void ReplaceConvertsAllPoints()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileB, 100));
        history.GoBack(At(FileB, 400), AllAvailable);   // 進む側にも 1 件作る

        var converted = new List<string>();
        history.Replace(position =>
        {
            converted.Add(position.FilePath);
            return new NavPosition(position.FilePath, position.Line + 1, position.Column);
        });

        Check.Equal(2, converted.Count, "両方のスタックが変換対象になる");

        var target = history.GoBack(At(FileB, 900), AllAvailable);
        Check.Equal(11, target!.Line, "変換後の行が使われる");
    }

    // ── 判定の境界 ───────────────────────────────────────────

    /// <summary>「同じ場所」の閾値がちょうどの行差を含むこと（以内＝同じ場所）。</summary>
    private static void SamePlaceBoundary()
    {
        int threshold = ScriptNavigationHistory.MergeLineDistance;
        Check.True(
            ScriptNavigationHistory.IsSamePlace(At(FileA, 10), At(FileA, 10 + threshold)),
            "閾値ちょうどは同じ場所");
        Check.True(
            !ScriptNavigationHistory.IsSamePlace(At(FileA, 10), At(FileA, 10 + threshold + 1)),
            "閾値を 1 超えたら別の場所");
        Check.True(
            !ScriptNavigationHistory.IsSamePlace(At(FileA, 10), At(FileB, 10)),
            "別ファイルは同じ場所ではない");
    }

    /// <summary>
    /// プロジェクトを切り替えたときに、前のプロジェクトのパスを指す点を
    /// 両方のスタックから捨てられること。
    /// </summary>
    private static void ClearEmptiesBothStacks()
    {
        var history = new ScriptNavigationHistory();
        history.Record(At(FileA, 10));
        history.Record(At(FileB, 100));
        history.GoBack(At(FileB, 400), AllAvailable);   // 進む側にも 1 件作る

        history.Clear();

        Check.Equal(0, history.BackCount, "戻る側");
        Check.Equal(0, history.ForwardCount, "進む側");
        Check.True(!history.CanGoBack && !history.CanGoForward, "どちらにも動けない");
    }
}
