// ============================================================
//  ProjectPanel.TextEdit.cs — 右クリック「テキストエディタで開く」
//
//  【役割】
//  テキストとして編集できるファイル（editor/config/text_editable_extensions.json）を
//  内蔵スクリプトエディタのタブで開くメニュー項目を提供する。
//
//  【なぜダブルクリックと別に要るのか】
//  .anim（アニメーションタイムライン）・.inputmap（入力マップ）のように
//  **専用エディタを持つ形式**は、ダブルクリックでそちらを開くのが正しい。
//  一方で「中の JSON を直接直したい」場面もあるため、専用エディタを壊さずに
//  テキスト編集へ入る道をここに用意する。
//  専用エディタを持たない形式（.json / .txt / .csv / .md / .icons …）は
//  ダブルクリックでもここでも同じタブが開く。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using SEEDEditor.Panels.ScriptEditor;

namespace SEEDEditor.Panels;

/// <summary>
/// ProjectPanel のテキスト編集メニューを担当する部分クラス。
/// </summary>
public partial class ProjectPanel
{
    /// <summary>メニュー項目のラベル。</summary>
    private const string OpenAsTextMenuHeader = "テキストエディタで開く";

    /// <summary>
    /// 右クリックメニューへ「テキストエディタで開く」を足す。
    ///
    /// 単一選択かつ、カタログに載っている拡張子のファイルのときだけ出す
    /// （画像・モデル・地形データなどバイナリでは何も足さない）。
    /// </summary>
    /// <param name="menu">項目を足す対象のコンテキストメニュー。</param>
    private void AddTextEditMenuItems(ContextMenu menu)
    {
        if (_selectedItems.Count != 1) return;
        if (_selectedItems.First().Tag is not string path) return;
        if (!File.Exists(path)) return;
        if (!EditorLanguages.IsEditableExtension(Path.GetExtension(path))) return;

        var item = new MenuItem { Header = OpenAsTextMenuHeader };
        item.Click += (_, _) => ScriptFileOpened?.Invoke(path);
        menu.Items.Add(item);
        menu.Items.Add(new Separator());
    }
}
