// ============================================================
//  UpgradeUnsavedPolicy.cs — 一括アップグレードの前後の、開いているシーンの扱い（WPF 非依存の判定）
//
//  【なぜ要るか】（docs/reviews/2026-10-02_code_review.md の #8）
//  一括アップグレードはディスクの .actor を書き換え、.scene の prefab_hash もディスクの上で貼り直す。
//  開いているシーンに未保存の編集があると、シーンの自動再読込は「破棄になるので読み直さない」で見送り、
//  そのまま Ctrl+S するとメモリの古い prefab_hash で .scene を上書きしてしまう。次に開いたとき
//  PREFAB_STATUS が版ずれを数えて偽の「プレハブが更新されています」を出し、インスタンスの変更を
//  上書きする再展開へ誘う。そこで
//    ・実行の前に、未保存なら「保存してから」「破棄して」「やめる」を選ばせる
//    ・実行した後は、開いているシーンをディスクから読み直す（貼り直した版を取り込む）
//  判定だけをここに置き（MigrationTests が確かめる）、ダイアログ・保存・読み直しは MainWindow.Migration.cs。
// ============================================================

namespace SEEDEditor.Migration;

/// <summary>未保存のシーンがあるときに利用者が選んだこと。</summary>
public enum UpgradeUnsavedChoice
{
    /// <summary>保存してからアップグレードする。</summary>
    SaveFirst,

    /// <summary>未保存の変更を破棄してアップグレードする（実行したらディスクから読み直す）。</summary>
    Discard,

    /// <summary>アップグレードをやめる。</summary>
    Cancel,
}

/// <summary>アップグレードの窓をどう開くか。</summary>
public enum UpgradeStartAction
{
    /// <summary>すぐ開く。</summary>
    OpenNow,

    /// <summary>保存を送り、保存が終わってから開く（失敗したら開かない）。</summary>
    SaveThenOpen,

    /// <summary>開かない。</summary>
    Abort,
}

/// <summary>
/// 一括アップグレードの前後の、開いているシーンの扱いの判定（純関数）。
/// </summary>
public static class UpgradeUnsavedPolicy
{
    /// <summary>
    /// アップグレードの窓をどう開くかを決める。
    /// </summary>
    /// <param name="isDirty">開いているシーン（またはアクタタブ）に未保存の編集があるか。</param>
    /// <param name="choice">未保存のときに利用者が選んだこと（未保存でなければ見ない）。</param>
    public static UpgradeStartAction Decide(bool isDirty, UpgradeUnsavedChoice choice)
    {
        if (!isDirty) return UpgradeStartAction.OpenNow;
        return choice switch
        {
            UpgradeUnsavedChoice.SaveFirst => UpgradeStartAction.SaveThenOpen,
            UpgradeUnsavedChoice.Discard   => UpgradeStartAction.OpenNow,
            _                              => UpgradeStartAction.Abort,
        };
    }

    /// <summary>
    /// アップグレードの窓を閉じた後に、開いているシーンをディスクから読み直すか。
    /// 実行した（ディスクが書き換わった）ときだけ。保存したことの無いシーン（パスが無い）は読み直せない。
    /// </summary>
    /// <param name="executed">窓の中でアップグレードを実行したか（失敗した実行も含む。途中まで書いていることがあるため）。</param>
    /// <param name="hasScenePath">開いているシーンにファイルのパスがあるか。</param>
    public static bool ShouldReloadScene(bool executed, bool hasScenePath) => executed && hasScenePath;
}
