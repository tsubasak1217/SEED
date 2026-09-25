// ============================================================
//  AndroidViewportPolicy.cs — Android の実行中にエディタのビューポート（シーンパネル）へ何を出すかの判断（純粋な処理）
//
//  【入力】Android の実行の写し（状態・実行先の表示名・一時停止中の端末のシーンの写しの状態）と、
//          写しをシーンパネルへ出す段取り（SceneSnapshotViewSession）の状態
//  【出力】ビューポートに出す中身（PC のランタイムのまま／Android の案内／端末の写し）と、案内・バナーの文言
//  MainWindow はこの結果を当てるだけ（MainWindow.AndroidRun.cs の ApplyAndroidViewport）。
//
//  【決まり】
//    1. Android の実行が動いている間（Building / Running / Paused / Stopping）は、PC のランタイム
//       （ビューポートに埋め込んだ Edit の子ウィンドウ）を隠し、「Android で実行中（端末: …）」などの案内を出す。
//       ゲームは端末で動いているのに、エディタのビューポートには Edit のシーンが映り続けて紛らわしかったため。
//       隠している間は子ウィンドウへ描画の依頼（WM_PAINT）が届かないので、PC の Edit の描画も止まる。
//    2. Idle に戻ったら（停止・アプリの終了・失敗）PC のランタイムの表示へ戻す。
//       PC の Play / Edit の表示はこの判断では何も変えない（Android が動いていなければ常に Runtime）。
//    3. 一時停止中（Paused。docs/android.md §20.17）は、端末のシーンの写しを編集用ランタイムへ閲覧専用で読み込めたら
//       PC のランタイムを見せ（中身＝AndroidSnapshot）、ビューポートの上にバナー「端末の一時停止の写し（閲覧専用）」を出す。
//       写しを取り出している・読み込んでいる途中はその旨の案内、取り出せなかったときは従来の「一時停止中」の案内のまま
//       （理由は Output パネル）。状態ごとの中身と文言は表（PhaseTable・PausedTable）で持つ。
//    4. 写しを戻している途中（再開・停止の直後）は、Android の状態の行（実行中・止めています）の案内を出す
//       （戻し終えるまで編集用ランタイムは写しのままなので見せない）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;
using SEEDEditor.Scene;
using SEEDEditor.SceneSnapshot;

namespace SEEDEditor.AndroidRun;

/// <summary>ビューポート（シーンパネル）に出す中身の種類。</summary>
public enum ViewportContent
{
    /// <summary>PC のランタイム（埋め込みの Edit / Play の画面）。従来どおり。</summary>
    Runtime,

    /// <summary>
    /// Android の実行の案内（PC のランタイムを隠し、起動中の画面と同じ置き場に文言を出す）。
    /// </summary>
    AndroidNotice,

    /// <summary>
    /// 一時停止中の端末のシーンの写し（PC の編集用ランタイムに閲覧専用で読み込んだもの）。ランタイムを見せ、
    /// ビューポートの上にバナーを出す（docs/android.md §20.17）。
    /// </summary>
    AndroidSnapshot,
}

/// <summary>ビューポートの見せ方。</summary>
/// <param name="Content">中身の種類。</param>
/// <param name="NoticeText">案内の文言（AndroidNotice のときだけ。それ以外は null）。</param>
/// <param name="BannerText">ビューポートの上のバナーの文言（AndroidSnapshot のときだけ。それ以外は null）。</param>
public sealed record ViewportPresentation(ViewportContent Content, string? NoticeText, string? BannerText = null)
{
    /// <summary>PC のランタイムを見せる（従来どおり）。</summary>
    public static readonly ViewportPresentation Runtime = new(ViewportContent.Runtime, null);

    /// <summary>PC のランタイムを隠すか（案内のときだけ隠す。写しはランタイムに出ているので見せる）。</summary>
    public bool HidesRuntime => Content == ViewportContent.AndroidNotice;

    /// <summary>Android の実行がビューポートを使っているか（案内か写し。Idle に戻ったら PC の表示へ戻す判断に使う）。</summary>
    public bool IsAndroidOwned => Content != ViewportContent.Runtime;
}

/// <summary>Android の実行中のビューポートの判断。</summary>
public static class AndroidViewportPolicy
{
    // ── 案内の文言（{0}=実行先の表示名。例「Pixel_6a（実機）」）──────────────

    /// <summary>ビルド・インストール中の案内の書式。</summary>
    public const string BuildingNoticeFormat = "Android 向けにビルド中（端末: {0}）";

    /// <summary>端末で実行中の案内の書式。</summary>
    public const string RunningNoticeFormat = "Android で実行中（端末: {0}）";

    /// <summary>端末のアプリを一時停止中の案内の書式（写しを出せないとき・写しを使わないとき）。</summary>
    public const string PausedNoticeFormat = "Android で一時停止中（端末: {0}）";

    /// <summary>一時停止中に端末のシーンの写しを取り出している途中の案内の書式。</summary>
    public const string SnapshotFetchingNoticeFormat = "Android で一時停止中 — 端末のシーンの写しを取り出しています（端末: {0}）";

    /// <summary>取り出した写しを編集用ランタイムへ読み込んでいる途中の案内の書式。</summary>
    public const string SnapshotLoadingNoticeFormat = "Android で一時停止中 — 写しをシーンパネルへ読み込んでいます（端末: {0}）";

    /// <summary>止めている途中の案内の書式。</summary>
    public const string StoppingNoticeFormat = "Android の実行を止めています（端末: {0}）";

    /// <summary>実行先の表示名がまだ無いとき（写しの作り始めなど）の代わりの文言。</summary>
    public const string UnknownTargetText = "未定";

    /// <summary>状態 1 つぶんの見せ方（中身の種類と文言の書式）。</summary>
    /// <param name="Content">中身の種類。</param>
    /// <param name="TextFormat">文言の書式（{0}=実行先の表示名。案内なら案内の、写しならバナーの文言）。</param>
    private sealed record PhaseRow(ViewportContent Content, string TextFormat);

    /// <summary>一時停止中のビューポートの場面。</summary>
    private enum PausedScene
    {
        /// <summary>写しを出さない（取り出せなかった・取り出していない）。</summary>
        Notice,

        /// <summary>写しを取り出している途中。</summary>
        Fetching,

        /// <summary>取り出した写しを編集用ランタイムへ読み込んでいる途中。</summary>
        Loading,

        /// <summary>写しを出している（閲覧専用）。</summary>
        Showing,
    }

    /// <summary>
    /// Android の状態ごとの見せ方（Paused 以外）。表に無い状態（Idle）は PC のランタイムを見せる。
    /// </summary>
    private static readonly IReadOnlyDictionary<AndroidRunPhase, PhaseRow> PhaseTable = new Dictionary<AndroidRunPhase, PhaseRow>
    {
        [AndroidRunPhase.Building] = new(ViewportContent.AndroidNotice, BuildingNoticeFormat),
        [AndroidRunPhase.Running]  = new(ViewportContent.AndroidNotice, RunningNoticeFormat),
        [AndroidRunPhase.Stopping] = new(ViewportContent.AndroidNotice, StoppingNoticeFormat),
    };

    /// <summary>
    /// 一時停止中（Paused）の場面ごとの見せ方（docs/android.md §20.17）。写しを出している間だけランタイムを見せてバナーを出す。
    /// </summary>
    private static readonly IReadOnlyDictionary<PausedScene, PhaseRow> PausedTable = new Dictionary<PausedScene, PhaseRow>
    {
        [PausedScene.Notice]   = new(ViewportContent.AndroidNotice, PausedNoticeFormat),
        [PausedScene.Fetching] = new(ViewportContent.AndroidNotice, SnapshotFetchingNoticeFormat),
        [PausedScene.Loading]  = new(ViewportContent.AndroidNotice, SnapshotLoadingNoticeFormat),
        [PausedScene.Showing]  = new(ViewportContent.AndroidSnapshot, EditorReadOnlyPolicy.SnapshotBannerFormat),
    };

    /// <summary>
    /// ビューポートの見せ方を決める。
    /// </summary>
    /// <param name="android">Android の実行の写し。</param>
    /// <param name="view">端末の写しをシーンパネルへ出す段取りの状態（既定は出していない）。</param>
    /// <param name="givenUpGeneration">
    /// 出すのをあきらめた写しの回数（読み込めなかった・出せる状態でなかった。AndroidPauseSnapshotViewCoordinator が持つ。
    /// 既定の 0 は「あきらめたものは無い」）。その回数の写しは「読み込み中」ではなく一時停止中の案内にする。
    /// </param>
    /// <returns>見せ方（Android が動いていなければ Runtime）。</returns>
    public static ViewportPresentation Compute(
        AndroidRunSnapshot android, SceneSnapshotViewPhase view = SceneSnapshotViewPhase.Idle, int givenUpGeneration = 0)
    {
        if (!android.IsActive) return ViewportPresentation.Runtime;
        var row = android.Phase == AndroidRunPhase.Paused
            ? PausedTable[DecidePausedScene(android.PauseSnapshot, view, givenUpGeneration)]
            : PhaseTable.TryGetValue(android.Phase, out var phaseRow) ? phaseRow : null;
        if (row is null) return ViewportPresentation.Runtime;

        var target = string.IsNullOrWhiteSpace(android.TargetText) ? UnknownTargetText : android.TargetText;
        var text = string.Format(row.TextFormat, target);
        return row.Content == ViewportContent.AndroidSnapshot
            ? new ViewportPresentation(row.Content, null, text)
            : new ViewportPresentation(row.Content, text);
    }

    /// <summary>
    /// 一時停止中の場面を決める。写しを出している（Showing）ときだけ写しを見せる。出し始め・差し替えの途中は読み込み中、
    /// 取り出している途中は取り出し中、それ以外（取り出せなかった・あきらめた・戻している途中）は一時停止中の案内。
    /// </summary>
    private static PausedScene DecidePausedScene(
        AndroidPauseSnapshotState snapshot, SceneSnapshotViewPhase view, int givenUpGeneration) => view switch
    {
        SceneSnapshotViewPhase.Showing => PausedScene.Showing,
        SceneSnapshotViewPhase.Beginning => PausedScene.Loading,
        _ when snapshot.Status == AndroidPauseSnapshotStatus.Fetching => PausedScene.Fetching,
        // 取り出せて、まだ読み込みを始めていない一瞬も読み込み中として見せる（案内がちらつかないように）。
        // 出すのをあきらめた写しは一時停止中の案内へ戻す（読み込み中のまま残さない）
        SceneSnapshotViewPhase.Idle when snapshot.IsReady && snapshot.Generation != givenUpGeneration => PausedScene.Loading,
        _ => PausedScene.Notice,
    };
}
