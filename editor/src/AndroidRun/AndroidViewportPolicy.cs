// ============================================================
//  AndroidViewportPolicy.cs — Android の実行中にエディタのビューポート（シーンパネル）へ何を出すかの判断（純粋な処理）
//
//  【入力】Android の実行の写し（状態・実行先の表示名）
//  【出力】ビューポートに出す中身（PC のランタイムのまま／Android の案内）と案内の文言
//  MainWindow はこの結果を当てるだけ（MainWindow.AndroidRun.cs の ApplyAndroidViewport）。
//
//  【決まり】
//    1. Android の実行が動いている間（Building / Running / Paused / Stopping）は、PC のランタイム
//       （ビューポートに埋め込んだ Edit の子ウィンドウ）を隠し、「Android で実行中（端末: …）」などの案内を出す。
//       ゲームは端末で動いているのに、エディタのビューポートには Edit のシーンが映り続けて紛らわしかったため。
//       隠している間は子ウィンドウへ描画の依頼（WM_PAINT）が届かないので、PC の Edit の描画も止まる。
//    2. Idle に戻ったら（停止・アプリの終了・失敗）PC のランタイムの表示へ戻す。
//       PC の Play / Edit の表示はこの判断では何も変えない（Android が動いていなければ常に Runtime）。
//    3. 状態ごとの中身と文言は表（PhaseTable）で持つ。次の作業の「一時停止中に端末のシーンの写しを
//       ビューポートに出す」は、ViewportContent に種類を足して Paused の行を差し替えるだけで済む形にしてある。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

using System.Collections.Generic;

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
}

/// <summary>ビューポートの見せ方。</summary>
/// <param name="Content">中身の種類。</param>
/// <param name="NoticeText">案内の文言（Runtime のときは null）。</param>
public sealed record ViewportPresentation(ViewportContent Content, string? NoticeText)
{
    /// <summary>PC のランタイムを見せる（従来どおり）。</summary>
    public static readonly ViewportPresentation Runtime = new(ViewportContent.Runtime, null);

    /// <summary>PC のランタイムを隠すか（Runtime 以外なら隠す）。</summary>
    public bool HidesRuntime => Content != ViewportContent.Runtime;
}

/// <summary>Android の実行中のビューポートの判断。</summary>
public static class AndroidViewportPolicy
{
    // ── 案内の文言（{0}=実行先の表示名。例「Pixel_6a（実機）」）──────────────

    /// <summary>ビルド・インストール中の案内の書式。</summary>
    public const string BuildingNoticeFormat = "Android 向けにビルド中（端末: {0}）";

    /// <summary>端末で実行中の案内の書式。</summary>
    public const string RunningNoticeFormat = "Android で実行中（端末: {0}）";

    /// <summary>端末のアプリを一時停止中の案内の書式。</summary>
    public const string PausedNoticeFormat = "Android で一時停止中（端末: {0}）";

    /// <summary>止めている途中の案内の書式。</summary>
    public const string StoppingNoticeFormat = "Android の実行を止めています（端末: {0}）";

    /// <summary>実行先の表示名がまだ無いとき（写しの作り始めなど）の代わりの文言。</summary>
    public const string UnknownTargetText = "未定";

    /// <summary>状態 1 つぶんの見せ方（中身の種類と案内の書式）。</summary>
    /// <param name="Content">中身の種類。</param>
    /// <param name="NoticeFormat">案内の書式（{0}=実行先の表示名）。</param>
    private sealed record PhaseRow(ViewportContent Content, string NoticeFormat);

    /// <summary>
    /// Android の状態ごとの見せ方。表に無い状態（Idle）は PC のランタイムを見せる。
    /// 一時停止中に端末の画面の写しを出すときは、Paused の行の中身を差し替える。
    /// </summary>
    private static readonly IReadOnlyDictionary<AndroidRunPhase, PhaseRow> PhaseTable = new Dictionary<AndroidRunPhase, PhaseRow>
    {
        [AndroidRunPhase.Building] = new(ViewportContent.AndroidNotice, BuildingNoticeFormat),
        [AndroidRunPhase.Running]  = new(ViewportContent.AndroidNotice, RunningNoticeFormat),
        [AndroidRunPhase.Paused]   = new(ViewportContent.AndroidNotice, PausedNoticeFormat),
        [AndroidRunPhase.Stopping] = new(ViewportContent.AndroidNotice, StoppingNoticeFormat),
    };

    /// <summary>
    /// ビューポートの見せ方を決める。
    /// </summary>
    /// <param name="android">Android の実行の写し。</param>
    /// <returns>見せ方（Android が動いていなければ Runtime）。</returns>
    public static ViewportPresentation Compute(AndroidRunSnapshot android)
    {
        if (!android.IsActive || !PhaseTable.TryGetValue(android.Phase, out var row)) return ViewportPresentation.Runtime;
        var target = string.IsNullOrWhiteSpace(android.TargetText) ? UnknownTargetText : android.TargetText;
        return new ViewportPresentation(row.Content, string.Format(row.NoticeFormat, target));
    }
}
