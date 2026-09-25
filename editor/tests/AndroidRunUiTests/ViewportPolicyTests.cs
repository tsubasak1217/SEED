using SEEDEditor.AndroidRun;
using SpriteRigTests;

namespace AndroidRunUiTests;

/// <summary>
/// Android の実行中のビューポート（シーンパネル）の判断（AndroidViewportPolicy。docs/android.md §20.16）。
/// Building / Running / Paused / Stopping の間は PC のランタイムを隠して案内を出し、Idle で元に戻す。
/// </summary>
public static class ViewportPolicyTests
{
    /// <summary>テストで使う実行先の表示名（RunTargetCatalogBuilder.DeviceText と同じ形）。</summary>
    private const string PhoneText = "Pixel_6a（実機）";

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("ビューポート: Android が動いていなければ PC のランタイムのまま（PC の Play / Edit の表示は変えない）", IdleShowsRuntime);
        harness.Add("ビューポート: ビルド中・実行中・一時停止中・停止中は PC のランタイムを隠して端末名入りの案内を出す", ActivePhasesShowNotice);
        harness.Add("ビューポート: Idle 以外のすべての状態に表の行がある（状態を足したら見せ方も決める）", EveryActivePhaseIsCovered);
        harness.Add("ビューポート: 実行先の表示名がまだ無いときは「未定」を出す", MissingTargetText);
        harness.Add("ビューポート: 実行 → 一時停止 → 再開 → 停止の流れで、隠すのは動いている間だけ", RunLifecycle);
    }

    /// <summary>状態と実行先の表示名から写しを作る。</summary>
    private static AndroidRunSnapshot Snapshot(AndroidRunPhase phase, string? targetText = PhoneText) =>
        new() { Phase = phase, TargetText = targetText };

    /// <summary>Android が動いていないとき。</summary>
    private static void IdleShowsRuntime()
    {
        var view = AndroidViewportPolicy.Compute(AndroidRunSnapshot.Idle);
        Check.Equal(ViewportContent.Runtime, view.Content, "中身は PC のランタイム");
        Check.True(!view.HidesRuntime, "PC のランタイムを隠さない");
        Check.True(view.NoticeText is null, "案内は出さない");
        Check.True(ReferenceEquals(ViewportPresentation.Runtime, view), "既定の見せ方をそのまま返す");
    }

    /// <summary>動いている各状態の案内。</summary>
    private static void ActivePhasesShowNotice()
    {
        var expected = new (AndroidRunPhase Phase, string Text)[]
        {
            (AndroidRunPhase.Building, "Android 向けにビルド中（端末: Pixel_6a（実機））"),
            (AndroidRunPhase.Running,  "Android で実行中（端末: Pixel_6a（実機））"),
            (AndroidRunPhase.Paused,   "Android で一時停止中（端末: Pixel_6a（実機））"),
            (AndroidRunPhase.Stopping, "Android の実行を止めています（端末: Pixel_6a（実機））"),
        };
        foreach (var (phase, text) in expected)
        {
            var view = AndroidViewportPolicy.Compute(Snapshot(phase));
            Check.Equal(ViewportContent.AndroidNotice, view.Content, $"{phase}: 中身は Android の案内");
            Check.True(view.HidesRuntime, $"{phase}: PC のランタイムを隠す");
            Check.Equal(text, view.NoticeText, $"{phase}: 案内の文言");
        }
    }

    /// <summary>Idle 以外の全状態に行があること。</summary>
    private static void EveryActivePhaseIsCovered()
    {
        foreach (var phase in Enum.GetValues<AndroidRunPhase>())
        {
            var view = AndroidViewportPolicy.Compute(Snapshot(phase));
            Check.Equal(phase != AndroidRunPhase.Idle, view.HidesRuntime, $"{phase}: 隠すのは動いている間だけ");
            Check.Equal(phase != AndroidRunPhase.Idle, view.NoticeText is not null, $"{phase}: 案内の有無");
        }
    }

    /// <summary>実行先の表示名が無いとき。</summary>
    private static void MissingTargetText()
    {
        foreach (var missing in new[] { null, "", "  " })
        {
            var view = AndroidViewportPolicy.Compute(Snapshot(AndroidRunPhase.Building, missing));
            Check.Equal(
                string.Format(AndroidViewportPolicy.BuildingNoticeFormat, AndroidViewportPolicy.UnknownTargetText),
                view.NoticeText,
                $"表示名「{missing ?? "null"}」の代わり");
        }
    }

    /// <summary>1 回の実行の流れ。</summary>
    private static void RunLifecycle()
    {
        var flow = new[]
        {
            AndroidRunPhase.Idle, AndroidRunPhase.Building, AndroidRunPhase.Running, AndroidRunPhase.Paused,
            AndroidRunPhase.Running, AndroidRunPhase.Stopping, AndroidRunPhase.Idle,
        };
        var hidden = flow.Select(p => AndroidViewportPolicy.Compute(Snapshot(p)).HidesRuntime).ToArray();
        Check.Equal("F,T,T,T,T,T,F", string.Join(",", hidden.Select(h => h ? "T" : "F")), "隠す区間（動き出してから止まり終えるまで）");
    }
}
