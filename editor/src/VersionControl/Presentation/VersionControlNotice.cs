// ============================================================
//  VersionControlNotice.cs — 操作結果をパネルの 1 行メッセージへ畳む
//
//  【役割】
//  <see cref="VersionControlOutcome"/> は 8 通りあるが、利用者に見せるのは
//  「何色で、何と書くか」と「どのボタンを強調するか」の 2 点だけ。
//  その対応表をここ 1 か所に閉じ込める。
//
//  【なぜ WPF に依存させないのか】
//  色（Brush）まで含めてしまうと単体テストへリンクできなくなる。
//  ここでは「重大度」という抽象までを決め、実際の配色はビュー側が持つ。
//  そうしておけば「どの Outcome が注意色になるか」をテストで固定できる。
//
//  【Canceled を黙って戻す理由】
//  中断はタイムアウトか利用者の操作でしか起きず、どちらも「利用者が原因を
//  知りたい出来事」ではない。赤いエラーを出すと不安にさせるだけなので
//  重大度 None（何も表示しない）にする。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// パネルに出す 1 行メッセージの重大度。実際の色はビュー側が決める。
/// </summary>
public enum VersionControlNoticeSeverity
{
    /// <summary>何も表示しない。</summary>
    None,

    /// <summary>成功（緑系）。</summary>
    Success,

    /// <summary>情報。失敗ではない（灰・青系）。</summary>
    Info,

    /// <summary>注意。利用者の操作が必要（黄系）。</summary>
    Warning,

    /// <summary>失敗（赤系）。</summary>
    Error,
}

/// <summary>
/// パネルに出す 1 行メッセージ（不変）。
/// </summary>
public sealed class VersionControlNotice
{
    /// <summary>重大度。</summary>
    public VersionControlNoticeSeverity Severity { get; }

    /// <summary>表示する文字列。</summary>
    public string Text { get; }

    /// <summary>
    /// 「最新を取得」ボタンを強調すべきか。
    /// <see cref="VersionControlOutcome.NeedsSync"/> のときだけ真になる。
    /// </summary>
    public bool EmphasizeFetchLatest { get; }

    /// <summary>表示すべき文字列があるか。</summary>
    public bool HasText => Text.Length > 0;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="severity">重大度。</param>
    /// <param name="text">表示する文字列。</param>
    /// <param name="emphasizeFetchLatest">「最新を取得」を強調するか。</param>
    public VersionControlNotice(
        VersionControlNoticeSeverity severity, string? text, bool emphasizeFetchLatest = false)
    {
        Severity             = severity;
        Text                 = text ?? string.Empty;
        EmphasizeFetchLatest = emphasizeFetchLatest;
    }

    /// <summary>何も表示しない状態。</summary>
    public static VersionControlNotice None { get; } =
        new(VersionControlNoticeSeverity.None, string.Empty);

    /// <summary>情報として 1 行出す。</summary>
    /// <param name="text">表示する文字列。</param>
    public static VersionControlNotice Info(string text)
        => new(VersionControlNoticeSeverity.Info, text);

    /// <summary>失敗として 1 行出す。</summary>
    /// <param name="text">表示する文字列。</param>
    public static VersionControlNotice Error(string text)
        => new(VersionControlNoticeSeverity.Error, text);

    /// <summary>
    /// 操作結果を 1 行メッセージへ変換する。
    ///
    /// <para>
    /// 文言は基本的に結果が持っているものをそのまま使う（中核層が
    /// <see cref="VersionControlMessages"/> から取っているため二重管理にならない）。
    /// ただし <see cref="VersionControlOutcome.NeedsSync"/> と
    /// <see cref="VersionControlOutcome.RequiresConnection"/> だけは、
    /// パネルの狭い 1 行に収まる短い言い回しへ差し替える。
    /// </para>
    /// </summary>
    /// <param name="result">操作結果。</param>
    public static VersionControlNotice FromResult(VersionControlResult? result)
    {
        if (result is null) return None;

        return result.Outcome switch
        {
            // 成功・情報はそのままの文言で。
            VersionControlOutcome.Success =>
                new VersionControlNotice(VersionControlNoticeSeverity.Success, result.Message),

            VersionControlOutcome.NothingToDo =>
                new VersionControlNotice(VersionControlNoticeSeverity.Info, result.Message),

            // 先に「最新を取得」が要る。ボタンを強調して次の一手を示す。
            VersionControlOutcome.NeedsSync =>
                new VersionControlNotice(
                    VersionControlNoticeSeverity.Warning,
                    VersionControlMessages.PANEL_NOTICE_NEEDS_SYNC,
                    emphasizeFetchLatest: true),

            // 競合は失敗ではなく「選んでください」。一覧の競合グループが本体。
            VersionControlOutcome.Conflicted =>
                new VersionControlNotice(VersionControlNoticeSeverity.Warning, result.Message),

            // サーバへ届かない。短く言い切る。
            VersionControlOutcome.RequiresConnection =>
                new VersionControlNotice(
                    VersionControlNoticeSeverity.Error,
                    VersionControlMessages.PANEL_NOTICE_REQUIRES_CONNECTION),

            // バージョン管理が無い場合はパネル自体が操作 UI を出さないので、
            // ここへ来ても通常は表示されない。念のため文言は持たせる。
            VersionControlOutcome.Unavailable =>
                new VersionControlNotice(VersionControlNoticeSeverity.Error, result.Message),

            // 中断は黙って戻す（利用者に知らせる価値が無い）。
            VersionControlOutcome.Canceled => None,

            _ => new VersionControlNotice(VersionControlNoticeSeverity.Error, result.Message),
        };
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"{Severity}: {Text}";
}
