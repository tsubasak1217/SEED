// ============================================================
//  ConflictRowItem.cs — 「競合」節の 1 行（WPF へ流す器）
//
//  【役割】
//  未解決の競合 1 件を、解決の手段つきの行として出すための器。
//  手段は 4 つ:
//    ・比較…                 … マージエディタを開く（行はダブルクリックでも開く）
//    ・両方を取り込む         … 同じ場所へ追加し合ったときだけ
//    ・自分の変更を残す / リモートを採用 … 従来の 2 択
//    ・編集した内容で解決     … 外部のエディタで直した場合
//
//  【なぜ変更ツリーと別の型にするのか】
//  競合の行は「フォルダー階層で眺めるもの」ではない。
//  未解決のうちは送信できず、利用者は必ず 1 件ずつ選択しなければならないので、
//  階層に散らさず**平らな一覧**でフルパスを出し、行ごとに手段を並べる。
//
//  【ボタンの文言を直書きしない】
//  「自分の変更を残す」「リモートを採用」は取り違えると利用者の作業が消える。
//  対応表（LoreConflictResolutionMap）から引いた文言だけを使い、
//  XAML にも日本語を書かない。
//
//  【ここでファイルを読む理由】
//  「両方を取り込む」が成り立つか・印が残っていないかは **中身を見ないと分からない**。
//  押してから「できません」と言うより、押せない理由をツールチップに出す方がよい。
//  読むのは未解決の競合だけ（普通は数件）で、上限つき（MergeFileText）。
// ============================================================

using System.IO;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Merge;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// 「競合」節の 1 行（不変）。
/// </summary>
public sealed class ConflictRowItem
{
    /// <summary>リポジトリ相対パス。</summary>
    public string RelativePath { get; }

    /// <summary>作業コピー上の絶対パス（マージエディタへ渡す）。</summary>
    public string AbsolutePath { get; }

    /// <summary>行に出すパス（移動・改名なら「移動前 → 移動後」）。</summary>
    public string PathText { get; }

    /// <summary>ファイル種別のアイコン。</summary>
    public ImageSource? FileTypeIcon { get; }

    /// <summary>競合アイコンのキー。</summary>
    public string KindIconKey { get; }

    /// <summary>状態の日本語表示（ツールチップ）。</summary>
    public string KindText { get; }

    /// <summary>「両方を取り込む」が使えるか。</summary>
    public bool CanTakeBoth { get; }

    /// <summary>「両方を取り込む」のツールチップ（使えないときは理由）。</summary>
    public string TakeBothToolTip { get; }

    /// <summary>「編集した内容で解決」が使えるか（印が残っていないか）。</summary>
    public bool CanResolveAsIs { get; }

    /// <summary>「編集した内容で解決」のツールチップ（使えないときは理由）。</summary>
    public string ResolveAsIsToolTip { get; }

    // ── ボタンの文言（XAML から x:Static で引く）────────────

    /// <summary>「自分の変更を残す」ボタンの文言（対応表から取る）。</summary>
    public static string KeepMineText { get; } =
        LoreConflictResolutionMap.ToDisplayName(ConflictResolutionChoice.KeepMine);

    /// <summary>「リモートを採用」ボタンの文言（対応表から取る）。</summary>
    public static string TakeRemoteText { get; } =
        LoreConflictResolutionMap.ToDisplayName(ConflictResolutionChoice.TakeRemote);

    /// <summary>「すべて自分の変更を残す」ボタンの文言。</summary>
    public static string KeepMineAllText { get; } =
        string.Format(VersionControlMessages.PANEL_RESOLVE_ALL_FORMAT, KeepMineText);

    /// <summary>「すべてリモートを採用」ボタンの文言。</summary>
    public static string TakeRemoteAllText { get; } =
        string.Format(VersionControlMessages.PANEL_RESOLVE_ALL_FORMAT, TakeRemoteText);

    /// <summary>「比較…」ボタンの文言。</summary>
    public static string CompareText { get; } = VersionControlMessages.PANEL_CONFLICT_COMPARE;

    /// <summary>「比較…」ボタンのツールチップ。</summary>
    public static string CompareToolTip { get; } =
        VersionControlMessages.PANEL_CONFLICT_COMPARE_TOOLTIP;

    /// <summary>「両方を取り込む」ボタンの文言。</summary>
    public static string TakeBothText { get; } = VersionControlMessages.PANEL_CONFLICT_TAKE_BOTH;

    /// <summary>「すべて両方を取り込む」ボタンの文言。</summary>
    public static string TakeBothAllText { get; } =
        VersionControlMessages.PANEL_CONFLICT_TAKE_BOTH_ALL;

    /// <summary>「編集した内容で解決」ボタンの文言。</summary>
    public static string ResolveAsIsText { get; } =
        VersionControlMessages.PANEL_CONFLICT_RESOLVE_AS_IS;

    /// <summary>
    /// 競合したファイルから行を作る。
    /// </summary>
    /// <param name="file">未解決の競合になっている変更。</param>
    /// <param name="workingCopyRoot">作業コピーのルート（絶対パスの組み立てに使う）。</param>
    public ConflictRowItem(ChangedFile file, string workingCopyRoot)
    {
        RelativePath = file.Path;
        AbsolutePath = workingCopyRoot.Length == 0
            ? file.Path
            : Path.Combine(workingCopyRoot, file.Path);

        PathText = file.FromPath.Length == 0
            ? file.Path
            : $"{file.FromPath} → {file.Path}";

        KindIconKey = VersionControlDisplay.ToChangeIconKey(file.Kind, file.Conflict);
        KindText    = VersionControlDisplay.ToChangeText(file.Kind, file.Conflict);

        // ファイル種別アイコンは既存の対応表をそのまま使う（二重管理しない）。
        FileTypeIcon = FileTypeIcons.GetImage(Path.GetExtension(file.Path));

        // 中身を 1 回だけ読んで、2 つの可否をまとめて決める。
        var read = MergeFileText.Read(AbsolutePath);

        var takeBoth = read.Succeeded
            ? MergeTakeBothRule.Evaluate(read.Text)
            : MergeTakeBothVerdict.No(read.Reason);
        CanTakeBoth     = takeBoth.Allowed;
        TakeBothToolTip = takeBoth.Allowed
            ? VersionControlMessages.PANEL_CONFLICT_TAKE_BOTH_TOOLTIP
            : takeBoth.Reason;

        var asIs = EvaluateResolveAsIs(AbsolutePath, read);
        CanResolveAsIs     = asIs.Allowed;
        ResolveAsIsToolTip = asIs.Allowed
            ? VersionControlMessages.PANEL_CONFLICT_RESOLVE_AS_IS_TOOLTIP
            : string.Format(
                VersionControlMessages.PANEL_CONFLICT_RESOLVE_AS_IS_BLOCKED_FORMAT, asIs.Reason);
    }

    /// <summary>
    /// 「編集した内容で解決」が使えるかを決める。
    ///
    /// <para>
    /// テキストとして読めたなら「印が残っていないこと」が条件。
    /// 読めなかった場合、ファイルが実在するなら **バイナリ等で印が入り得ない** ので使える
    /// （Lore は印をテキストにしか書かない）。実在しないなら使えない。
    /// </para>
    /// </summary>
    /// <param name="absolutePath">対象の絶対パス。</param>
    /// <param name="read">読み込み結果。</param>
    /// <returns>使えるか、と使えない理由。</returns>
    private static (bool Allowed, string Reason) EvaluateResolveAsIs(
        string absolutePath, MergeFileReadResult read)
    {
        if (read.Succeeded)
        {
            var markerLine = MergeValidation.FindMarkerLine(read.Text);
            return markerLine == MergeValidation.NO_MARKER_LINE
                ? (true, string.Empty)
                : (false, string.Format(
                    VersionControlMessages.MERGE_VALIDATE_MARKERS_REMAIN_FORMAT, markerLine));
        }

        return File.Exists(absolutePath)
            ? (true, string.Empty)
            : (false, read.Reason);
    }
}
