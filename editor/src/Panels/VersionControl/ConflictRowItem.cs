// ============================================================
//  ConflictRowItem.cs — 「競合」節の 1 行（WPF へ流す器）
//
//  【役割】
//  未解決の競合 1 件を、2 択ボタン（自分の変更を残す / リモートを採用）つきの
//  行として出すための器。
//
//  【なぜ変更ツリーと別の型にするのか】
//  競合の行は「フォルダー階層で眺めるもの」ではない。
//  未解決のうちは送信できず、利用者は必ず 1 件ずつ選択しなければならないので、
//  階層に散らさず**平らな一覧**でフルパスを出し、行ごとに 2 択を並べる。
//  役割が違うので型も分ける（変更ツリー側に「競合かどうか」の分岐を持ち込まない）。
//
//  【ボタンの文言を直書きしない】
//  「自分の変更を残す」「リモートを採用」は取り違えると利用者の作業が消える。
//  対応表（LoreConflictResolutionMap）から引いた文言だけを使い、
//  XAML にも日本語を書かない。
// ============================================================

using System.IO;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
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

    /// <summary>行に出すパス（移動・改名なら「移動前 → 移動後」）。</summary>
    public string PathText { get; }

    /// <summary>ファイル種別のアイコン。</summary>
    public ImageSource? FileTypeIcon { get; }

    /// <summary>競合アイコンのキー。</summary>
    public string KindIconKey { get; }

    /// <summary>状態の日本語表示（ツールチップ）。</summary>
    public string KindText { get; }

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

    /// <summary>競合したファイルから行を作る。</summary>
    /// <param name="file">未解決の競合になっている変更。</param>
    public ConflictRowItem(ChangedFile file)
    {
        RelativePath = file.Path;

        PathText = file.FromPath.Length == 0
            ? file.Path
            : $"{file.FromPath} → {file.Path}";

        KindIconKey = VersionControlDisplay.ToChangeIconKey(file.Kind, file.Conflict);
        KindText    = VersionControlDisplay.ToChangeText(file.Kind, file.Conflict);

        // ファイル種別アイコンは既存の対応表をそのまま使う（二重管理しない）。
        FileTypeIcon = FileTypeIcons.GetImage(Path.GetExtension(file.Path));
    }
}
