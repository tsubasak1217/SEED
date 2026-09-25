// ============================================================
//  EditorReadOnlyPolicy.cs — エディタの「読み取り専用・閲覧専用」の判断を 1 か所にまとめたもの（純粋な処理）
//
//  【2 つの原因】（強い方を採る）
//    1. 端末の一時停止の写しを表示中（DeviceSnapshotView。docs/android.md §20.17）
//       … シーンパネルに出ているのは端末から取り出した写しで、編集中のシーンは編集用ランタイムのメモリへ退避してある。
//          保存（上書き・名前を付けて・Ctrl+S）・編集（ヒエラルキー・インスペクタ・シーンビューのギズモ・地形）・
//          別のシーンを開くことを拒否し、編集の UI を無効表示にし、ビューポートの上にバナーを出す。
//          写しへの編集を端末へ反映する機能は無い（閲覧専用。docs/backlog.md）。
//    2. 別のエディタがこのシーンを開いている（SceneLockedByOtherEditor。従来の読み取り専用。SceneLock）
//       … 保存だけを拒否する（編集は従来どおりできる。後から保存した方が相手の変更を消すため）。
//  MainWindow はこの結果を当てるだけ（保存の入口の RefuseSaveIfReadOnly・タイトル・各パネルの SetReadOnly・バナー）。
//  ランタイム側でも写しの表示中は保存・編集の命令を捨てる（二重の守り。runtime の app/snapshot_view_ops.rs）。
//
//  WPF に依存しない（editor/tests/AndroidRunUiTests からリンクされる）。
// ============================================================

namespace SEEDEditor.Scene;

/// <summary>読み取り専用・閲覧専用の原因。</summary>
public enum EditorReadOnlyCause
{
    /// <summary>どちらでもない（保存・編集できる）。</summary>
    None,

    /// <summary>別のエディタがこのシーンを開いている（保存だけを拒否）。</summary>
    SceneLockedByOtherEditor,

    /// <summary>端末の一時停止の写しを表示している（保存・編集・シーンの切り替えを拒否）。</summary>
    DeviceSnapshotView,
}

/// <summary>判断の材料。</summary>
public sealed record EditorReadOnlyInput
{
    /// <summary>別のエディタがこのシーンのロックを持っているか（従来の読み取り専用）。</summary>
    public bool SceneLocked { get; init; }

    /// <summary>ロックのときの保存の拒否理由（SceneLock.DENY_LOCKED_FORMAT で作ったもの。無ければ既定の文言）。</summary>
    public string? SceneLockReason { get; init; }

    /// <summary>端末の一時停止の写しを表示しているか（出し始め・戻している途中を含む）。</summary>
    public bool SnapshotViewActive { get; init; }

    /// <summary>写しを取った端末の表示名（例「Pixel_6a（実機）」。分からなければ null）。</summary>
    public string? SnapshotTargetText { get; init; }
}

/// <summary>判断の結果。</summary>
/// <param name="Cause">原因（強い方）。</param>
/// <param name="SaveDenialReason">保存を拒否する理由（null なら保存できる）。</param>
/// <param name="SaveDeniedToast">保存を拒否したときのトーストの短い文言（保存できるなら null）。</param>
/// <param name="EditDenialReason">編集を拒否する理由（null なら編集できる）。</param>
/// <param name="EditingUiEnabled">編集の UI（インスペクタ・ヒエラルキーの編集・ギズモ・地形）を有効にするか。</param>
/// <param name="SceneSwitchAllowed">別のシーンを開いてよいか。</param>
/// <param name="BannerText">ビューポートの上に出すバナーの文言（出さなければ null）。</param>
/// <param name="TitleMark">ウィンドウのタイトルに付ける印（付けなければ null）。</param>
public sealed record EditorReadOnlyState(
    EditorReadOnlyCause Cause,
    string? SaveDenialReason,
    string? SaveDeniedToast,
    string? EditDenialReason,
    bool EditingUiEnabled,
    bool SceneSwitchAllowed,
    string? BannerText,
    string? TitleMark)
{
    /// <summary>保存も編集もできる状態。</summary>
    public static readonly EditorReadOnlyState Writable = new(
        EditorReadOnlyCause.None, null, null, null, EditingUiEnabled: true, SceneSwitchAllowed: true, BannerText: null, TitleMark: null);

    /// <summary>保存を拒否するか。</summary>
    public bool DeniesSave => SaveDenialReason is not null;

    /// <summary>編集を拒否するか。</summary>
    public bool DeniesEdit => EditDenialReason is not null;
}

/// <summary>エディタの読み取り専用・閲覧専用の判断。</summary>
public static class EditorReadOnlyPolicy
{
    // ── 端末の一時停止の写し（閲覧専用）──────────────────────────

    /// <summary>写しの表示中に保存を拒否する理由。</summary>
    public const string SnapshotSaveDenialReason =
        "端末の一時停止の写し（閲覧専用）を表示しているため保存できません。実行バーで再開するか停止すると、編集中のシーンへ戻ります。";

    /// <summary>写しの表示中に保存を拒否したときのトースト。</summary>
    public const string SnapshotSaveDeniedToast = "端末の写し（閲覧専用）は保存できません";

    /// <summary>写しの表示中に編集を拒否する理由。</summary>
    public const string SnapshotEditDenialReason =
        "端末の一時停止の写し（閲覧専用）は編集できません。再開するか停止すると、編集中のシーンへ戻ります。";

    /// <summary>写しの表示中のバナーの書式（{0}=端末の表示名）。</summary>
    public const string SnapshotBannerFormat = "端末の一時停止の写し（閲覧専用）— {0}。再開・停止で編集中のシーンへ戻ります";

    /// <summary>端末の表示名が分からないときの代わりの文言。</summary>
    public const string UnknownTargetText = "端末";

    /// <summary>写しの表示中のタイトルの印。</summary>
    public const string SnapshotTitleMark = "[端末の写し・閲覧専用]";

    // ── 別のエディタのロック（従来の読み取り専用）──────────────────────

    /// <summary>ロックの拒否理由が渡らなかったときの文言。</summary>
    public const string DefaultLockDenialReason = "別のエディタがこのシーンを開いているため保存できません。";

    /// <summary>ロックで保存を拒否したときのトースト（従来の文言）。</summary>
    public const string LockSaveDeniedToast = "読み取り専用のため保存できません";

    /// <summary>ロックのときのタイトルの印（従来の文言）。</summary>
    public const string LockTitleMark = "[読み取り専用]";

    /// <summary>
    /// 読み取り専用・閲覧専用を決める（写しの表示がロックより強い）。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <returns>結果。</returns>
    public static EditorReadOnlyState Decide(EditorReadOnlyInput input)
    {
        if (input.SnapshotViewActive)
        {
            var target = string.IsNullOrWhiteSpace(input.SnapshotTargetText) ? UnknownTargetText : input.SnapshotTargetText;
            return new EditorReadOnlyState(
                EditorReadOnlyCause.DeviceSnapshotView,
                SnapshotSaveDenialReason,
                SnapshotSaveDeniedToast,
                SnapshotEditDenialReason,
                EditingUiEnabled: false,
                SceneSwitchAllowed: false,
                BannerText: string.Format(SnapshotBannerFormat, target),
                TitleMark: SnapshotTitleMark);
        }
        if (input.SceneLocked)
        {
            return new EditorReadOnlyState(
                EditorReadOnlyCause.SceneLockedByOtherEditor,
                string.IsNullOrWhiteSpace(input.SceneLockReason) ? DefaultLockDenialReason : input.SceneLockReason,
                LockSaveDeniedToast,
                EditDenialReason: null,
                EditingUiEnabled: true,
                SceneSwitchAllowed: true,
                BannerText: null,
                TitleMark: LockTitleMark);
        }
        return EditorReadOnlyState.Writable;
    }
}
