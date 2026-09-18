// ============================================================
//  VersionControlSections.cs — 折りたたみ節の種類・見出し文言・既定の開閉
//
//  【役割】
//  パネルを縦に区切る 4 つの節（競合 / 変更 / ロック / 履歴）について、
//  「どんな見出しを出すか」「そもそも出すか」「最初は開いているか」を決める。
//
//  【なぜ純粋ロジックにするのか】
//  見出しの件数（例: 変更 (478)）と、競合が 0 件のときに節ごと隠す判断は、
//  間違えてもビルドが通り、GUI を起動しないと見えない類の分岐である。
//  さらに「節の保存キー」はエディタ設定ファイルへ書き込む文字列なので、
//  あとから勝手に変えられない（変えると利用者の開閉状態が失われる）。
//  どちらもここへ集めて単体テストで固定する。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// パネルを縦に区切る折りたたみ節。並び順もこの宣言順に合わせる。
/// </summary>
public enum VersionControlSection
{
    /// <summary>未解決の競合（1 件でもあるときだけ出す）。</summary>
    Conflicts,

    /// <summary>変更（フォルダー階層のツリー）。</summary>
    Changes,

    /// <summary>ロック。</summary>
    Locks,

    /// <summary>履歴。</summary>
    History,
}

/// <summary>
/// 節の見出し文言・表示可否・既定の開閉状態を決める純関数群。
/// </summary>
public static class VersionControlSections
{
    /// <summary>並び順（画面の上から下）。</summary>
    public static readonly VersionControlSection[] InDisplayOrder =
    {
        VersionControlSection.Conflicts,
        VersionControlSection.Changes,
        VersionControlSection.Locks,
        VersionControlSection.History,
    };

    // ── 保存キー ────────────────────────────────────────────
    //  エディタ設定の JSON に書く文字列。**一度決めたら変えない**
    //  （変えると利用者が開いていた節の状態が失われる）。

    /// <summary>「競合」節の保存キー。</summary>
    public const string KEY_CONFLICTS = "conflicts";

    /// <summary>「変更」節の保存キー。</summary>
    public const string KEY_CHANGES = "changes";

    /// <summary>「ロック」節の保存キー。</summary>
    public const string KEY_LOCKS = "locks";

    /// <summary>「履歴」節の保存キー。</summary>
    public const string KEY_HISTORY = "history";

    /// <summary>節に対応する保存キーを返す。</summary>
    /// <param name="section">節。</param>
    public static string ToKey(VersionControlSection section) => section switch
    {
        VersionControlSection.Conflicts => KEY_CONFLICTS,
        VersionControlSection.Changes   => KEY_CHANGES,
        VersionControlSection.Locks     => KEY_LOCKS,
        VersionControlSection.History   => KEY_HISTORY,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    /// <summary>保存キーから節を引く（未知のキーなら null）。</summary>
    /// <param name="key">保存キー。</param>
    public static VersionControlSection? FromKey(string? key) => key switch
    {
        KEY_CONFLICTS => VersionControlSection.Conflicts,
        KEY_CHANGES   => VersionControlSection.Changes,
        KEY_LOCKS     => VersionControlSection.Locks,
        KEY_HISTORY   => VersionControlSection.History,
        _             => null,
    };

    // ── 見出し ──────────────────────────────────────────────

    /// <summary>
    /// 節の見出し文言を作る。
    ///
    /// <para>
    /// 件数は見出しに必ず添える（開かずに規模が分かるため）。
    /// ただし「履歴」は「さらに読み込む」で増えていくので件数を書かない
    /// ——「履歴 (30)」と出すと、それが全件だと誤解させる。
    /// 中身をまだ取りに行っていない節（ロック・履歴は開いたときに取りに行く）は
    /// 0 件と断定せず <see cref="VersionControlMessages.PANEL_SECTION_COUNT_UNKNOWN"/> を出す。
    /// </para>
    /// </summary>
    /// <param name="section">節。</param>
    /// <param name="count">件数（まだ分からないときは null）。</param>
    public static string ToHeaderText(VersionControlSection section, int? count)
    {
        if (section == VersionControlSection.History)
        {
            return VersionControlMessages.PANEL_SECTION_HISTORY;
        }

        var countText = count is null
            ? VersionControlMessages.PANEL_SECTION_COUNT_UNKNOWN
            : count.Value.ToString(CultureInfo.CurrentCulture);

        var format = section switch
        {
            VersionControlSection.Conflicts => VersionControlMessages.PANEL_SECTION_CONFLICTS_FORMAT,
            VersionControlSection.Changes   => VersionControlMessages.PANEL_SECTION_CHANGES_FORMAT,
            VersionControlSection.Locks     => VersionControlMessages.PANEL_SECTION_LOCKS_FORMAT,
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
        };

        return string.Format(CultureInfo.CurrentCulture, format, countText);
    }

    // ── 表示可否 ────────────────────────────────────────────

    /// <summary>
    /// その節を画面に出すか。
    ///
    /// <para>
    /// 「競合」だけは 1 件も無ければ節ごと隠す。競合は例外的な状態であり、
    /// 常時「競合 (0)」が並んでいると、本当に競合したときの目立ち方が鈍る。
    /// 他の 3 つは 0 件でも出す（そこに何があるかを利用者が学べるように）。
    /// </para>
    /// </summary>
    /// <param name="section">節。</param>
    /// <param name="count">件数（まだ分からないときは null）。</param>
    public static bool IsVisible(VersionControlSection section, int? count)
        => section != VersionControlSection.Conflicts || (count ?? 0) > 0;

    // ── 既定の開閉 ──────────────────────────────────────────

    /// <summary>
    /// 保存が無いときの開閉状態。
    ///
    /// <para>
    /// 「競合」と「変更」は開く（利用者がまず見るもの）。
    /// 「ロック」と「履歴」は閉じる——どちらも開くとサーバ往復が走るので、
    /// パネルを出しただけで通信が始まらないようにする。
    /// </para>
    /// </summary>
    /// <param name="section">節。</param>
    public static bool DefaultIsExpanded(VersionControlSection section) => section switch
    {
        VersionControlSection.Conflicts => true,
        VersionControlSection.Changes   => true,
        VersionControlSection.Locks     => false,
        VersionControlSection.History   => false,
        _ => false,
    };

    /// <summary>
    /// 節を開いたときにサーバへ問い合わせが要るか
    /// （＝開いた瞬間に取りに行くべきか）。
    /// </summary>
    /// <param name="section">節。</param>
    public static bool NeedsFetchOnExpand(VersionControlSection section)
        => section is VersionControlSection.Locks or VersionControlSection.History;
}
