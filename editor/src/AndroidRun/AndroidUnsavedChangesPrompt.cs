// ============================================================
//  AndroidUnsavedChangesPrompt.cs — Android で実行する前の「未保存の変更」の確認（文言と選択肢。純粋な処理。段階C-3）
//
//  【なぜ要るか】
//  PC の Play は保存しなくても編集中の状態で動く（埋め込み Play はその場で Play 化、ウィンドウ Play は一時ファイル
//  _play_temp.scene。MainWindow.OnPlayPause）。Android は保存済みのファイルから APK（pak）を作るので、保存していない変更は
//  端末に届かない。そこで実行の前に、エディタの他の「保存しますか？」（シーンの切り替え・終了）と同じ言い回しで尋ねる。
//
//  【選択肢】
//    保存して実行 … Ctrl+S と同じ保存をして、保存が終わってから実行する（保存したファイルの指紋が変わるので pak を作り直す）
//    保存せず実行 … 保存済みの内容で実行する（Output に警告を 1 行）
//    キャンセル   … 何もしない（Escape・閉じるも同じ。ヘッドレスでは常にこれ）
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。ダイアログの窓は Dialogs/ActionChoiceWindow。
// ============================================================

namespace SEEDEditor.AndroidRun;

/// <summary>未保存の変更の確認の答え。</summary>
public enum AndroidUnsavedChoice
{
    /// <summary>何もしない。</summary>
    Cancel,

    /// <summary>保存してから実行する。</summary>
    SaveAndRun,

    /// <summary>保存せずに実行する。</summary>
    RunWithoutSaving,
}

/// <summary>未保存の変更の確認（文言と選択肢）。</summary>
public static class AndroidUnsavedChangesPrompt
{
    /// <summary>ダイアログのタイトル。</summary>
    public const string Title = "SEED Editor — Android で実行";

    /// <summary>
    /// 本文（エディタの他の未保存の確認「未保存の変更があります。…保存しますか？」と同じ言い回し）。
    /// </summary>
    public const string Message =
        "未保存の変更があります。Android で実行する前に保存しますか？\n\n" +
        "Android の実行は保存済みのファイルから APK を作るため、保存していない変更は端末に届きません" +
        "（PC の Play は保存しなくても編集中の状態で動きます）。";

    /// <summary>「保存して実行」の文言（主操作。変更を失わない側）。</summary>
    public const string SaveAndRunText = "保存して実行";

    /// <summary>「保存せず実行」の文言。</summary>
    public const string RunWithoutSavingText = "保存せず実行";

    /// <summary>「キャンセル」の文言。</summary>
    public const string CancelText = "キャンセル";

    /// <summary>確認が要るか（未保存の変更があるときだけ）。</summary>
    /// <param name="isDirty">未保存の変更があるか。</param>
    /// <returns>確認が要れば true。</returns>
    public static bool NeedsPrompt(bool isDirty) => isDirty;
}
