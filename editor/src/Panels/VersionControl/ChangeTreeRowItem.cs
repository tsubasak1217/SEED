// ============================================================
//  ChangeTreeRowItem.cs — 変更ツリーの 1 行（WPF へ流す器）
//
//  【役割】
//  WPF 非依存の <see cref="ChangeTreeRow"/>（節点 + 深さ + 開閉）を、
//  XAML が素直にバインドできる形へ写す。
//  インデント幅・アイコン画像・状態の 1 文字は、ここで確定させる。
//
//  【なぜ行を作る時点で確定させるのか】
//  DataTemplate の中で変換（種類 → 1 文字、拡張子 → 画像、深さ → 余白）を行うと
//  コンバータが増えて追いづらくなり、しかも行ごとに毎回走る。
//  変更が数百件並ぶ場所なので、変換は 1 行 1 回に抑える。
//
//  【開閉は行が持たない】
//  開閉状態の実体はパネル側の「畳んだパスの集合」であって、行ではない。
//  行は「いまどう見えているか」を持つだけの使い捨てで、
//  開閉のたびに作り直される（不変にしておく方が取り違えが起きない）。
// ============================================================

using System.IO;
using System.Windows;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// 変更ツリーの 1 行（不変）。
/// </summary>
public sealed class ChangeTreeRowItem
{
    /// <summary>インデント 1 段ぶんの幅（px）。開閉ハンドル 16px と釣り合う値。</summary>
    private const double IndentPerDepthPx = 14.0;

    /// <summary>元の行（節点・深さ・開閉）。</summary>
    public ChangeTreeRow Row { get; }

    /// <summary>この行が表す節点。</summary>
    public ChangeTreeNode Node => Row.Node;

    /// <summary>リポジトリ相対パス（根は空文字）。</summary>
    public string RelativePath => Row.Node.RelativePath;

    /// <summary>ファイル行か（右クリックの対象になるのはこれだけ）。</summary>
    public bool IsFile => Row.Node.Kind == ChangeTreeNodeKind.File;

    /// <summary>根の行か（作業コピーのパスを出す行）。</summary>
    public bool IsRoot => Row.Node.Kind == ChangeTreeNodeKind.Root;

    /// <summary>畳める行か（根・フォルダーで、かつ子を持つ）。</summary>
    public bool IsContainer => Row.Node.IsContainer && Row.Node.HasChildren;

    /// <summary>開いているか（開閉ハンドルの向き）。</summary>
    public bool IsExpanded => Row.IsExpanded;

    /// <summary>開閉ハンドルを出すか（子を持たない行では場所だけ空ける）。</summary>
    public Visibility HandleVisibility => IsContainer ? Visibility.Visible : Visibility.Hidden;

    /// <summary>インデントの余白（深さ × 1 段ぶん）。</summary>
    public Thickness Indent { get; }

    /// <summary>行に出す名前。</summary>
    public string NameText { get; }

    /// <summary>行のツールチップ（フルパス、移動なら「移動前 → 移動後」）。</summary>
    public string TooltipText { get; }

    /// <summary>ファイル種別・フォルダーのアイコン。</summary>
    public ImageSource? Icon { get; }

    /// <summary>状態の 1 文字（A / M / D / R / C / ! / ?）。ファイル行以外は空文字。</summary>
    public string StatusLetter { get; }

    /// <summary>状態の日本語表示（1 文字のツールチップに使う）。ファイル行以外は空文字。</summary>
    public string StatusText { get; }

    /// <summary>未解決の競合の行か（配色を変える）。</summary>
    public bool IsConflict { get; }

    /// <summary>
    /// 行を作る。
    /// </summary>
    /// <param name="row">元の行。</param>
    public ChangeTreeRowItem(ChangeTreeRow row)
    {
        Row    = row;
        Indent = new Thickness(row.Depth * IndentPerDepthPx, 0, 0, 0);

        var node = row.Node;

        // ── 根: 作業コピーのパスを出す（分からなければ代替の文言）──
        if (node.Kind == ChangeTreeNodeKind.Root)
        {
            NameText     = node.Name.Length == 0
                ? VersionControlMessages.PANEL_TREE_ROOT_UNKNOWN
                : node.Name;
            TooltipText  = NameText;
            Icon         = FileTypeIcons.GetFolderImage(isEmpty: !node.HasChildren);
            StatusLetter = string.Empty;
            StatusText   = string.Empty;
            return;
        }

        // ── フォルダー ──
        if (node.Kind == ChangeTreeNodeKind.Folder)
        {
            NameText     = node.Name;
            TooltipText  = node.RelativePath;
            Icon         = FileTypeIcons.GetFolderImage(isEmpty: !node.HasChildren);
            StatusLetter = string.Empty;
            StatusText   = string.Empty;
            return;
        }

        // ── ファイル ──
        var file = node.File!;

        NameText = node.Name;
        Icon     = FileTypeIcons.GetImage(Path.GetExtension(node.Name));

        // 移動・改名は「移動前 → 移動後」をツールチップに出す
        // （履歴が繋がっていることを利用者が確認できるようにするため）。
        TooltipText = file.FromPath.Length == 0
            ? file.Path
            : $"{file.FromPath} → {file.Path}";

        StatusLetter = VersionControlDisplay.ToChangeLetter(file.Kind, file.Conflict);
        StatusText   = VersionControlDisplay.ToChangeText(file.Kind, file.Conflict);
        IsConflict   = file.Conflict == FileConflictState.Unresolved;
    }
}
