// ============================================================
//  PreviewMenuModel.cs — ヒエラルキーの右クリックに出す「プレビュー」の項目の組み立て
//
//  【役割】
//  右クリックした場所（普通のノード・プレビューの中のノード・空白）と状態（木にプレビューがあるか・
//  最近使ったプレハブ・Edit か）から、出す項目の並びを決める（docs/editor_screen_preview.md §1・§3）。
//  WPF の MenuItem へ写すのは HierarchyPanel.Preview.cs（ここは見出し・コマンド・押せるかだけを決める）。
//
//  【出し方】
//  - 普通のノード … 既存のメニューの末尾に足す（区切りは HierarchyPanel が入れる）:
//                   「プレハブをプレビュー」（最近使ったもの → 区切り → 「プロジェクトから選ぶ...」）
//                   ＋ 木にプレビューがあれば「すべてのプレビューを消す」
//  - プレビューの中 … メニューを丸ごとこれにする（既存の追加・コピー・削除・グループ・アクタファイル化は出さない）:
//                   「プレハブをプレビュー」（入れ子は可）・区切り・「元のプレハブを開く」・「プレビューを消す」・
//                   「すべてのプレビューを消す」
//  - 空白       … 木にプレビューがあれば「すべてのプレビューを消す」を足す
//  - Edit 以外  … プレビューの項目はすべて押せない（理由はツールチップ）
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace SEEDEditor.Preview;

/// <summary>項目の種類。</summary>
public enum PreviewMenuEntryKind
{
    /// <summary>子を持つ項目（サブメニュー）。</summary>
    SubMenu,
    /// <summary>押す項目。</summary>
    Item,
    /// <summary>区切り。</summary>
    Separator,
}

/// <summary>項目を押したときにすること。</summary>
public enum PreviewMenuCommand
{
    /// <summary>何もしない（サブメニュー・区切り）。</summary>
    None,
    /// <summary>プレハブを選ぶ窓を開いてプレビューする。</summary>
    PickPrefab,
    /// <summary>最近使ったプレハブ（<see cref="PreviewMenuEntry.Argument"/>）をプレビューする。</summary>
    PreviewRecent,
    /// <summary>右クリックしたノードを含むプレビューを消す。</summary>
    ClearThis,
    /// <summary>表示中のタブのプレビューを全部消す。</summary>
    ClearAll,
    /// <summary>元のプレハブ（<see cref="PreviewMenuEntry.Argument"/>）を開く。</summary>
    OpenSource,
}

/// <summary>メニューを丸ごと置き換えるか、既存のメニューへ足すか。</summary>
public enum PreviewMenuPlacement
{
    /// <summary>出す項目が無い。</summary>
    None,
    /// <summary>既存のメニューの末尾へ足す（前に区切りを入れる）。</summary>
    Append,
    /// <summary>メニューを丸ごとこの項目にする（プレビューの中のノード）。</summary>
    Replace,
}

/// <summary>
/// 項目 1 つ。
/// </summary>
/// <param name="Kind">種類。</param>
/// <param name="Header">見出し（区切りは空）。</param>
/// <param name="Command">押したときにすること。</param>
/// <param name="Argument">コマンドの引数（プレハブのパスなど。無ければ null）。</param>
/// <param name="IsEnabled">押せるか。</param>
/// <param name="ToolTip">ツールチップ（押せない理由・仮想パスなど。無ければ null）。</param>
/// <param name="Children">子（サブメニューのとき）。</param>
public sealed record PreviewMenuEntry(
    PreviewMenuEntryKind Kind,
    string Header,
    PreviewMenuCommand Command,
    string? Argument,
    bool IsEnabled,
    string? ToolTip,
    IReadOnlyList<PreviewMenuEntry> Children);

/// <summary>
/// 組み立てた結果。
/// </summary>
/// <param name="Placement">既存のメニューとの関係。</param>
/// <param name="Entries">項目の並び。</param>
public sealed record PreviewMenu(PreviewMenuPlacement Placement, IReadOnlyList<PreviewMenuEntry> Entries);

/// <summary>
/// 組み立ての材料（右クリックした場所と状態）。
/// </summary>
public sealed record PreviewMenuInput
{
    /// <summary>ノードの上で右クリックしたか（false = 空白）。</summary>
    public bool HasNode { get; init; }

    /// <summary>右クリックしたノードがプレビューの部分木の中か（根を含む）。</summary>
    public bool NodeInPreview { get; init; }

    /// <summary>右クリックしたノードがプレビューの根か。</summary>
    public bool NodeIsPreviewRoot { get; init; }

    /// <summary>右クリックしたノードからいちばん近いプレビューの根の中身のプレハブ（外なら null）。</summary>
    public string? NearestRootSource { get; init; }

    /// <summary>表示中の木にプレビューがあるか。</summary>
    public bool TreeHasPreview { get; init; }

    /// <summary>最近使ったプレハブ（assets:// 仮想パス。最近の順）。</summary>
    public IReadOnlyList<string> RecentPrefabs { get; init; } = Array.Empty<string>();

    /// <summary>Edit か（Play 中などは false。プレビューの項目を押せなくする）。</summary>
    public bool IsEditMode { get; init; } = true;
}

/// <summary>
/// 右クリックの「プレビュー」の項目を組み立てる。状態を持たない。
/// </summary>
public static class PreviewMenuModel
{
    // ── 見出し・ツールチップ（マジック文字列にしない）──────────────

    /// <summary>「プレハブをプレビュー」サブメニューの見出し。</summary>
    public const string PreviewPrefabHeader = "プレハブをプレビュー";

    /// <summary>「プレハブをプレビュー」の説明。</summary>
    public const string PreviewPrefabToolTip =
        "このノードの子として、プレハブを保存されないプレビューで差し込みます（シーンには保存されません）";

    /// <summary>窓でプレハブを選ぶ項目の見出し。</summary>
    public const string PickPrefabHeader = "プロジェクトから選ぶ...";

    /// <summary>元のプレハブを開く項目の見出し。</summary>
    public const string OpenSourceHeader = "元のプレハブを開く";

    /// <summary>元のプレハブを開く項目の説明。</summary>
    public const string OpenSourceToolTip =
        "プレビューは読み取り専用です。直すときはプレハブを開いて保存してください（保存するとプレビューにも反映されます）";

    /// <summary>プレビューを 1 つ消す項目の見出し。</summary>
    public const string ClearThisHeader = "プレビューを消す";

    /// <summary>すべてのプレビューを消す項目の見出し。</summary>
    public const string ClearAllHeader = "すべてのプレビューを消す";

    /// <summary>すべてのプレビューを消す項目の説明。</summary>
    public const string ClearAllToolTip = "表示中のタブのプレビューをすべて消します（シーンは変わりません）";

    /// <summary>Edit 以外で押せない理由。</summary>
    public const string NotEditToolTip = "Play 中はプレビューを使えません";

    /// <summary>元のプレハブが分からないときの理由。</summary>
    public const string NoSourceToolTip = "元のプレハブが分かりません";

    /// <summary>項目が 1 つも無いときの結果。</summary>
    private static readonly PreviewMenu Empty = new(PreviewMenuPlacement.None, Array.Empty<PreviewMenuEntry>());

    /// <summary>
    /// 右クリックした場所と状態から、出す項目を組み立てる。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <returns>既存のメニューとの関係と、項目の並び。</returns>
    public static PreviewMenu Build(PreviewMenuInput input)
    {
        // ── 空白: プレビューがあれば「すべてのプレビューを消す」だけ ──
        if (!input.HasNode)
        {
            return input.TreeHasPreview
                ? new PreviewMenu(PreviewMenuPlacement.Append, [BuildClearAll(input)])
                : Empty;
        }

        // ── プレビューの中: メニューを丸ごとプレビューの項目にする ──
        if (input.NodeInPreview)
        {
            return new PreviewMenu(PreviewMenuPlacement.Replace,
            [
                BuildPreviewPrefabSubMenu(input),
                Separator(),
                BuildOpenSource(input),
                Item(ClearThisHeader, PreviewMenuCommand.ClearThis, argument: null,
                     input.IsEditMode, input.IsEditMode ? null : NotEditToolTip),
                BuildClearAll(input),
            ]);
        }

        // ── 普通のノード: 既存のメニューの末尾へ足す ──
        var entries = new List<PreviewMenuEntry> { BuildPreviewPrefabSubMenu(input) };
        if (input.TreeHasPreview) entries.Add(BuildClearAll(input));
        return new PreviewMenu(PreviewMenuPlacement.Append, entries);
    }

    /// <summary>
    /// 「プレハブをプレビュー」サブメニュー（最近使ったもの → 区切り → 「プロジェクトから選ぶ...」）。
    /// </summary>
    /// <param name="input">材料。</param>
    /// <returns>サブメニュー。</returns>
    private static PreviewMenuEntry BuildPreviewPrefabSubMenu(PreviewMenuInput input)
    {
        var enabled  = input.IsEditMode;
        var children = new List<PreviewMenuEntry>();
        foreach (var path in input.RecentPrefabs)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            // 見出しはファイル名、ツールチップは仮想パス（同じ名前のプレハブを見分けるため）
            children.Add(Item(DisplayName(path), PreviewMenuCommand.PreviewRecent, path, enabled,
                              enabled ? path : NotEditToolTip));
        }
        if (children.Count > 0) children.Add(Separator());
        children.Add(Item(PickPrefabHeader, PreviewMenuCommand.PickPrefab, argument: null, enabled,
                          enabled ? null : NotEditToolTip));

        return new PreviewMenuEntry(PreviewMenuEntryKind.SubMenu, PreviewPrefabHeader, PreviewMenuCommand.None,
                                    Argument: null, enabled, enabled ? PreviewPrefabToolTip : NotEditToolTip, children);
    }

    /// <summary>「元のプレハブを開く」（中身のプレハブが分かるときだけ押せる）。</summary>
    /// <param name="input">材料。</param>
    /// <returns>項目。</returns>
    private static PreviewMenuEntry BuildOpenSource(PreviewMenuInput input)
    {
        var hasSource = !string.IsNullOrWhiteSpace(input.NearestRootSource);
        var enabled   = input.IsEditMode && hasSource;
        var toolTip   = !input.IsEditMode ? NotEditToolTip
                      : !hasSource        ? NoSourceToolTip
                      : $"{input.NearestRootSource}\n{OpenSourceToolTip}";
        return Item(OpenSourceHeader, PreviewMenuCommand.OpenSource, hasSource ? input.NearestRootSource : null,
                    enabled, toolTip);
    }

    /// <summary>「すべてのプレビューを消す」。</summary>
    /// <param name="input">材料。</param>
    /// <returns>項目。</returns>
    private static PreviewMenuEntry BuildClearAll(PreviewMenuInput input) =>
        Item(ClearAllHeader, PreviewMenuCommand.ClearAll, argument: null, input.IsEditMode,
             input.IsEditMode ? ClearAllToolTip : NotEditToolTip);

    /// <summary>押す項目を作る。</summary>
    private static PreviewMenuEntry Item(string header, PreviewMenuCommand command, string? argument,
                                         bool isEnabled, string? toolTip) =>
        new(PreviewMenuEntryKind.Item, header, command, argument, isEnabled, toolTip, Array.Empty<PreviewMenuEntry>());

    /// <summary>区切りを作る。</summary>
    private static PreviewMenuEntry Separator() =>
        new(PreviewMenuEntryKind.Separator, "", PreviewMenuCommand.None, Argument: null, IsEnabled: true,
            ToolTip: null, Array.Empty<PreviewMenuEntry>());

    /// <summary>
    /// 仮想パスから見出しに出すファイル名を取り出す（'/' と '\' の両方を区切りとして見る）。
    /// </summary>
    /// <param name="path">プレハブのパス。</param>
    /// <returns>ファイル名（取り出せなければパスそのもの）。</returns>
    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
