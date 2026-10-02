// ============================================================
//  PrefabPreviewPickerRow.cs — プレハブを選ぶ窓の一覧の 1 行（表示用の形）
//
//  【役割】
//  PrefabPreviewPickerWindow の一覧の行。プレハブ 1 つ（PrefabPreviewItem）と、どの区切り
//  （「最近使ったもの」／「プロジェクトのプレハブ」）に入るかを持つ。区切りは一覧のグループ分けに使う。
// ============================================================

namespace SEEDEditor.Preview;

/// <summary>
/// プレハブを選ぶ窓の一覧の 1 行。
/// </summary>
/// <param name="Item">プレハブ。</param>
/// <param name="Section">区切りの見出し（一覧のグループ）。</param>
public sealed record PrefabPreviewPickerRow(PrefabPreviewItem Item, string Section)
{
    /// <summary>アセットルートの直下にあるときにフォルダの代わりに出す文字。</summary>
    public const string RootFolderText = "assets の直下";

    /// <summary>表示名（ファイル名）。</summary>
    public string DisplayName => Item.DisplayName;

    /// <summary>淡色で添えるフォルダ（直下なら <see cref="RootFolderText"/>）。</summary>
    public string FolderText => Item.Folder.Length == 0 ? RootFolderText : Item.Folder;

    /// <summary>行のツールチップ（仮想パス）。</summary>
    public string RowToolTip => Item.VirtualPath;
}
