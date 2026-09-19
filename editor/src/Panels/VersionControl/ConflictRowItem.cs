// ============================================================
//  ConflictRowItem.cs — 「競合」節の 1 行（WPF へ流す器）
//
//  【役割】
//  未解決の競合 1 件を **一覧に出すためだけ** の器。
//  持つのは表示に要るものだけ（ファイル名・フォルダ・アイコン・パス）で、
//  解決の手段は一切持たない。
//
//  【行からボタンを全部外した理由（2026-09-19 の指摘）】
//  以前はこの行に「比較… / 自分の変更を残す / リモートを採用 / 両方を取り込む /
//  編集した内容で解決」の 5 つを並べていた。パネルは細いので**ボタンが幅を食い切り、
//  肝心の「何のファイルが競合しているのか」が 1 文字も見えなかった**。
//  一覧の仕事は「どれが競合しているか」を伝えることに絞り、
//  どちらを残すかの選択は**マージエディタの中だけ**で行う
//  （行のダブルクリック／Enter で開く）。
//
//  【ファイル名を先に、フォルダは後ろに薄く】
//  幅が足りないときに削られてよいのはフォルダ側。名前を左に置き、
//  フォルダは右の可変幅の列に置いて省略させる（XAML の列定義がそれを担う）。
//
//  【ファイルを読まなくなった】
//  以前は「両方を取り込めるか」「印が残っていないか」を決めるために
//  1 件ごとにファイルを読み、ブロックごとに LCS まで回していた
//  （docs/backlog.md「競合の一覧を作るたびにファイルを読んで差分まで取る」）。
//  その判定を使うボタンが無くなったので、器の生成は **純粋に文字列操作だけ** になった。
//
//  【なぜ変更ツリーと別の型にするのか】
//  競合の行は「フォルダー階層で眺めるもの」ではない。
//  未解決のうちは送信できず、利用者は必ず 1 件ずつ片付けなければならないので、
//  階層に散らさず**平らな一覧**で出す。
// ============================================================

using System.IO;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// 「競合」節の 1 行（不変。表示用の値だけを持つ）。
/// </summary>
public sealed class ConflictRowItem
{
    /// <summary>パスの区切り文字（Lore は '/' を返すが、念のため '\' も見る）。</summary>
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>リポジトリ相対パス（解決を頼むときの鍵）。</summary>
    public string RelativePath { get; }

    /// <summary>作業コピー上の絶対パス（マージエディタへ渡す）。</summary>
    public string AbsolutePath { get; }

    /// <summary>ファイル名だけ（行の主役。通常の文字色で出す）。</summary>
    public string FileName { get; }

    /// <summary>
    /// ファイル名のうしろへ薄い色で続けるフォルダ。ルート直下なら空文字。
    /// **先頭にファイル名との間隔（空白）を含む**
    /// （1 つの TextBlock の中の Run なので Margin では空けられない）。
    /// </summary>
    public string DirectoryText { get; }

    /// <summary>ファイル種別のアイコン。</summary>
    public ImageSource? FileTypeIcon { get; }

    /// <summary>競合アイコンのキー。</summary>
    public string KindIconKey { get; }

    /// <summary>状態の日本語表示（競合アイコンのツールチップ）。</summary>
    public string KindText { get; }

    /// <summary>行全体のツールチップ（フルパス＋開き方の案内）。</summary>
    public string TooltipText { get; }

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

        // ファイル名とフォルダを分ける。区切りが無ければルート直下＝フォルダは空。
        var separator = file.Path.LastIndexOfAny(PathSeparators);
        FileName      = separator < 0 ? file.Path : file.Path[(separator + 1)..];
        DirectoryText = separator < 0
            ? string.Empty
            : string.Format(
                  VersionControlMessages.PANEL_CONFLICT_ROW_DIRECTORY_FORMAT,
                  file.Path[..separator]);

        KindIconKey = VersionControlDisplay.ToChangeIconKey(file.Kind, file.Conflict);
        KindText    = VersionControlDisplay.ToChangeText(file.Kind, file.Conflict);

        // ファイル種別アイコンは既存の対応表をそのまま使う（二重管理しない）。
        FileTypeIcon = FileTypeIcons.GetImage(Path.GetExtension(file.Path));

        TooltipText = BuildTooltip(file);
    }

    /// <summary>
    /// 行のツールチップを組み立てる。
    /// フルパス（行では省略され得るので必ず全部出す）＋開き方の案内。
    /// 移動・改名なら移動前のパスも添える（行には出さないため、ここが唯一の手がかり）。
    /// </summary>
    /// <param name="file">対象の変更。</param>
    /// <returns>ツールチップの文言。</returns>
    private string BuildTooltip(ChangedFile file)
    {
        var moved = file.FromPath.Length == 0
            ? string.Empty
            : string.Format(
                  VersionControlMessages.PANEL_CONFLICT_ROW_MOVED_FROM_FORMAT, file.FromPath);

        return string.Format(
            VersionControlMessages.PANEL_CONFLICT_ROW_TOOLTIP_FORMAT, AbsolutePath, moved);
    }
}
