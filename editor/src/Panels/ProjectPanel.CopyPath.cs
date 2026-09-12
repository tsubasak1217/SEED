using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Assets;

namespace SEEDEditor.Panels;

/// <summary>
/// ProjectPanel の「パスをコピー」機能を担当する部分クラス。
///
/// 取り回すパスは 2 種類あり、用途が違う:
///   - <b>絶対パス</b>   : エクスプローラーや外部ツールへ渡す用
///   - <b>assets:// パス</b> : スクリプト／シーン／各種参照フィールドへそのまま貼る用
///     （変換規則は <see cref="AssetUriPath"/> が唯一の正典）
///
/// 右クリックメニューでは「パスをコピー」サブメニューにこの 2 つを並べる。
/// 複数選択時は選択順ではなく一覧の並び順で、1 行 1 パスの改行区切りにする。
/// アセットルート外のファイル（プロジェクト外を一時的に開いている場合など）は
/// assets:// パスを持てないので、その項目を無効化して理由をツールチップで示す。
/// </summary>
public partial class ProjectPanel
{
    // ── メニュー表示文字列（マジック文字列をここへ集約）────────────

    /// <summary>サブメニューの見出し。</summary>
    private const string CopyPathMenuHeader = "パスをコピー";

    /// <summary>絶対パスをコピーする項目の見出し。</summary>
    private const string CopyAbsolutePathHeader = "絶対パス";

    /// <summary>assets:// パスをコピーする項目の見出し。</summary>
    private const string CopyAssetUriHeader = "assets:// パス";

    /// <summary>フォルダ背景の右クリックで出す「現在のフォルダの」ぶんの見出し。</summary>
    private const string CopyCurrentFolderPathMenuHeader = "現在のフォルダのパスをコピー";

    /// <summary>assets:// パスを作れないときに、無効化した項目へ出す理由。</summary>
    private const string OutsideAssetsRootReason = "アセットルートの外にあるため assets:// パスを作れません";

    // ── メニュー組み立て ──────────────────────────────────────────

    /// <summary>
    /// 選択中のアイテム（ファイル／フォルダ）向けの「パスをコピー」サブメニューを足す。
    /// 選択が無ければ何も足さない。
    /// </summary>
    /// <param name="menu">項目を足す対象のコンテキストメニュー。</param>
    private void AddCopyPathMenuItems(ContextMenu menu)
    {
        var paths = GetSelectedPaths();
        if (paths.Count == 0) return;

        menu.Items.Add(BuildCopyPathSubMenu(CopyPathMenuHeader, paths));
    }

    /// <summary>
    /// 何も選択していない状態（フォルダの空白部分）向けに、
    /// 「現在開いているフォルダ」のパスをコピーするサブメニューを足す。
    ///
    /// 区切り線までここで面倒を見る（フォルダ未確定で項目を足さなかったときに
    /// 区切り線だけが 2 本並ばないようにするため）。
    /// </summary>
    /// <param name="menu">項目を足す対象のコンテキストメニュー。</param>
    private void AddCurrentFolderCopyPathMenuItems(ContextMenu menu)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;

        menu.Items.Add(BuildCopyPathSubMenu(
            CopyCurrentFolderPathMenuHeader, new List<string> { _currentPath }));
        menu.Items.Add(new Separator());
    }

    /// <summary>
    /// 「絶対パス」「assets:// パス」の 2 項目を持つサブメニューを作る。
    /// </summary>
    /// <param name="header">サブメニューの見出し。</param>
    /// <param name="paths">対象パス（絶対パス）。複数なら改行区切りでコピーする。</param>
    /// <returns>組み立てたサブメニュー。</returns>
    private MenuItem BuildCopyPathSubMenu(string header, IReadOnlyList<string> paths)
    {
        var root = new MenuItem { Header = header };

        // 絶対パスは常にコピーできる
        var absoluteText = AssetUriPath.JoinLines(paths);
        var absoluteItem = new MenuItem { Header = CopyAbsolutePathHeader };
        absoluteItem.Click += (_, _) => CopyTextToClipboard(absoluteText);
        root.Items.Add(absoluteItem);

        // assets:// パスはアセットルート配下のものだけ作れる
        var assetUris = paths.Select(p => AssetUriPath.ToAssetUri(_assetsRoot, p)).ToList();
        var assetText = AssetUriPath.JoinLines(assetUris);
        var assetItem = new MenuItem
        {
            Header    = CopyAssetUriHeader,
            IsEnabled = !string.IsNullOrEmpty(assetText),
            ToolTip   = string.IsNullOrEmpty(assetText) ? OutsideAssetsRootReason : assetText,
        };
        if (!string.IsNullOrEmpty(assetText))
            assetItem.Click += (_, _) => CopyTextToClipboard(assetText);
        root.Items.Add(assetItem);

        return root;
    }

    // ── クリップボード ────────────────────────────────────────────

    /// <summary>クリップボードが他プロセスに掴まれていたときの再試行回数。</summary>
    private const int ClipboardRetryCount = 3;

    /// <summary>再試行の間隔（ミリ秒）。</summary>
    private const int ClipboardRetryDelayMs = 30;

    /// <summary>
    /// 文字列をクリップボードへ入れる。
    ///
    /// クリップボードは OS 共有資源で、他プロセスが開いている瞬間は
    /// <see cref="System.Runtime.InteropServices.ExternalException"/> で失敗する。
    /// 数十 ms 待てばほぼ通るので数回だけ再試行し、それでも駄目ならログに残して諦める
    /// （パスのコピーに失敗したくらいでダイアログを出して作業を止めない）。
    /// </summary>
    /// <param name="text">コピーする文字列。空なら何もしない。</param>
    private static void CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        for (int attempt = 0; attempt < ClipboardRetryCount; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                EditorLog.Write($"[ProjectPanel] パスをコピー: {text.Replace(Environment.NewLine, " / ")}");
                return;
            }
            catch
            {
                System.Threading.Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        EditorLog.Write("[ProjectPanel] パスのコピーに失敗しました（クリップボードを開けません）");
    }
}
