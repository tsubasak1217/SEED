// ============================================================
//  ChangeRowItem.cs — 変更一覧の 1 行（見出し行 / ファイル行）
//
//  【役割】
//  グループ見出しとファイルを **1 本の仮想化 ListBox** へ流し込むための行の器。
//
//  【なぜ 1 本のリストに畳むのか】
//  「見出し + 入れ子の ListBox」という素直な組み方にすると、外側の ItemsControl が
//  内側の中身を全部実体化してしまい **仮想化が効かなくなる**。
//  アセットを数千件抱えるプロジェクトで「最新を取得」した直後は変更が数百〜数千件
//  並び得るので、ここは平坦な 1 本のリストにして仮想化を保つ。
//  見出しかファイルかは <see cref="IsHeader"/> で分け、
//  <see cref="ChangeRowTemplateSelector"/> がテンプレートを選ぶ。
//
//  【表示文字列をここで確定させる理由】
//  DataTemplate の中で変換（種類 → 日本語、アイコンキー → 画像）を行うと
//  コンバータが増えて追いづらくなる。行を作る時点で確定した文字列・画像にしておき、
//  XAML 側は素直にバインドするだけにする。
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
/// 変更一覧の 1 行（不変）。見出し行とファイル行の両方を表す。
/// </summary>
public sealed class ChangeRowItem
{
    /// <summary>見出し行か（偽ならファイル行）。</summary>
    public bool IsHeader { get; }

    /// <summary>見出しの文字列（ファイル行では空文字）。</summary>
    public string HeaderText { get; }

    /// <summary>競合グループに属する行か（見出し・ファイルの両方で真になる）。</summary>
    public bool IsConflictGroup { get; }

    /// <summary>対応する変更（見出し行では null）。</summary>
    public ChangedFile? File { get; }

    /// <summary>リポジトリ相対パス（見出し行では空文字）。</summary>
    public string RelativePath { get; }

    /// <summary>
    /// 一覧に出すパスの表示。移動・改名なら「移動前 → 移動後」と書く
    /// （履歴が繋がっていることを利用者が確認できるようにするため）。
    /// </summary>
    public string PathText { get; }

    /// <summary>変更の種類の表示名（追加 / 変更 / 削除 / 移動 / 複製 / 競合）。</summary>
    public string KindText { get; }

    /// <summary>変更の種類のアイコンキー（Icons.xaml の x:Key）。</summary>
    public string KindIconKey { get; }

    /// <summary>ファイル種別のアイコン（既存の FileTypeIcons を使う）。</summary>
    public ImageSource? FileTypeIcon { get; }

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

    /// <summary>見出し行を作る。</summary>
    /// <param name="headerText">見出しの文字列。</param>
    /// <param name="isConflictGroup">競合グループの見出しか。</param>
    private ChangeRowItem(string headerText, bool isConflictGroup)
    {
        IsHeader        = true;
        HeaderText      = headerText;
        IsConflictGroup = isConflictGroup;
        RelativePath    = string.Empty;
        PathText        = string.Empty;
        KindText        = string.Empty;
        KindIconKey     = string.Empty;
    }

    /// <summary>ファイル行を作る。</summary>
    /// <param name="file">対応する変更。</param>
    /// <param name="isConflictGroup">競合グループに属するか。</param>
    private ChangeRowItem(ChangedFile file, bool isConflictGroup)
    {
        IsHeader        = false;
        HeaderText      = string.Empty;
        IsConflictGroup = isConflictGroup;
        File            = file;
        RelativePath    = file.Path;

        PathText = file.FromPath.Length == 0
            ? file.Path
            : $"{file.FromPath} → {file.Path}";

        KindText    = VersionControlDisplay.ToChangeText(file.Kind, file.Conflict);
        KindIconKey = VersionControlDisplay.ToChangeIconKey(file.Kind, file.Conflict);

        // ファイル種別アイコンは既存の対応表をそのまま使う（二重管理しない）。
        FileTypeIcon = FileTypeIcons.GetImage(Path.GetExtension(file.Path));
    }

    /// <summary>見出し行を作る。</summary>
    /// <param name="headerText">見出しの文字列。</param>
    /// <param name="isConflictGroup">競合グループの見出しか。</param>
    public static ChangeRowItem Header(string headerText, bool isConflictGroup)
        => new(headerText, isConflictGroup);

    /// <summary>ファイル行を作る。</summary>
    /// <param name="file">対応する変更。</param>
    /// <param name="isConflictGroup">競合グループに属するか。</param>
    public static ChangeRowItem ForFile(ChangedFile file, bool isConflictGroup)
        => new(file, isConflictGroup);
}
