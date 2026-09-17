// ============================================================
//  ChangeRowTemplateSelector.cs — 変更一覧の行テンプレートの振り分け
//
//  【役割】
//  1 本の仮想化 ListBox に混ぜて流している 3 種類の行を、見た目のテンプレートへ振り分ける。
//    ・競合グループの見出し（「すべて」に対する 2 択ボタンを持つ）
//    ・通常グループの見出し（件数だけ）
//    ・ファイル行（競合なら 1 件ずつの 2 択ボタンを持つ）
//
//  【なぜ Visibility の切り替えではなく Selector なのか】
//  1 つの DataTemplate に両方を入れて Visibility で隠すと、
//  **見えない側のコントロールも毎行ぶん作られる**。仮想化していても
//  1 画面ぶんは実体化されるため、行あたりのコストが素直に倍になる。
//  ここは行数が多くなり得る場所なので、テンプレート自体を分ける。
// ============================================================

using System.Windows;
using System.Windows.Controls;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// <see cref="ChangeRowItem"/> の種類に応じて DataTemplate を選ぶ。
/// </summary>
public sealed class ChangeRowTemplateSelector : DataTemplateSelector
{
    /// <summary>競合グループの見出し用テンプレート。</summary>
    public DataTemplate? ConflictHeaderTemplate { get; set; }

    /// <summary>通常グループの見出し用テンプレート。</summary>
    public DataTemplate? GroupHeaderTemplate { get; set; }

    /// <summary>競合しているファイル行のテンプレート（2 択ボタンつき）。</summary>
    public DataTemplate? ConflictFileTemplate { get; set; }

    /// <summary>通常のファイル行のテンプレート。</summary>
    public DataTemplate? FileTemplate { get; set; }

    /// <summary>行の種類からテンプレートを選ぶ。</summary>
    /// <param name="item">行のデータ。</param>
    /// <param name="container">表示先のコンテナ。</param>
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ChangeRowItem row) return base.SelectTemplate(item, container);

        if (row.IsHeader)
        {
            return row.IsConflictGroup ? ConflictHeaderTemplate : GroupHeaderTemplate;
        }

        return row.IsConflictGroup ? ConflictFileTemplate : FileTemplate;
    }
}
