// ============================================================
//  VersionControlResult.cs — 操作結果（上の層が受け取る唯一の戻り値）
//
//  【役割】
//  「成功したか」だけでは足りない結果を 1 つの語彙で表す。
//  具体的には次を区別できる必要がある:
//    ・送るものが無かった（空コミット防止で止めた）
//    ・push が「ブランチが分岐している」で弾かれた（先に最新を取得すべき）
//    ・競合が残っている
//    ・サーバに繋がっていないのでできない（履歴など）
//    ・バージョン管理そのものが無い
//
//  【例外を投げない理由】
//  パネル（UI）から見ると、Lore の失敗は「異常」ではなく「日常的に起こる分岐」。
//  LoreError を例外のまま上へ流すと、呼び出し側が全箇所で try/catch を書くことになり、
//  しかも ReturnCode / Messages の解釈が各所に散る。プロバイダ境界で
//  すべてこの型へ畳み込み、例外は境界の外へ出さない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 操作の結末。UI の分岐はこの enum だけを見れば書ける。
/// </summary>
public enum VersionControlOutcome
{
    /// <summary>成功した。</summary>
    Success,

    /// <summary>
    /// 行うべきことが無かった。
    /// 「送信」で変更が 1 件も無い場合がこれ（Lore は未 stage のまま commit すると
    /// 空リビジョンを作ってしまうため、ここで止める）。失敗ではない。
    /// </summary>
    NothingToDo,

    /// <summary>
    /// 先に「最新を取得」が必要。
    /// push がリモートとの分岐で弾かれたときに返る。
    /// </summary>
    NeedsSync,

    /// <summary>
    /// 競合が残っている。<see cref="SyncReport.Conflicts"/> に対象ファイルが入る。
    /// 解決するまで「送信」はできない。
    /// </summary>
    Conflicted,

    /// <summary>このプロジェクトにはバージョン管理が無い（NullProvider）。</summary>
    Unavailable,

    /// <summary>
    /// サーバに接続できないため実行できない。
    /// 履歴のようにサーバ必須の操作でオフラインだったときに返る。
    /// </summary>
    RequiresConnection,

    /// <summary>呼び出し側の要求または期限切れで中断した。</summary>
    Canceled,

    /// <summary>上記以外の失敗。<see cref="VersionControlResult.Details"/> に原因が入る。</summary>
    Failed,
}

/// <summary>
/// 値を返さない操作の結果（不変）。
/// </summary>
public class VersionControlResult
{
    /// <summary>結末。</summary>
    public VersionControlOutcome Outcome { get; }

    /// <summary>利用者にそのまま見せられる 1 行の日本語メッセージ。</summary>
    public string Message { get; }

    /// <summary>
    /// 診断用の生メッセージ（Lore が返した文言など）。ログ向けで、UI には出さなくてよい。
    /// </summary>
    public IReadOnlyList<string> Details { get; }

    /// <summary>成功したか（<see cref="VersionControlOutcome.Success"/> のときだけ真）。</summary>
    public bool IsSuccess => Outcome == VersionControlOutcome.Success;

    /// <summary>
    /// 「異常」として扱うべきか。
    /// NothingToDo / NeedsSync / Conflicted は想定内の分岐なので偽になる。
    /// </summary>
    public bool IsError =>
        Outcome is VersionControlOutcome.Failed
                or VersionControlOutcome.Unavailable
                or VersionControlOutcome.RequiresConnection;

    /// <summary>結末・メッセージ・詳細を指定して生成する。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    protected VersionControlResult(
        VersionControlOutcome outcome, string message, IReadOnlyList<string>? details)
    {
        Outcome = outcome;
        Message = message ?? string.Empty;
        Details = details ?? Array.Empty<string>();
    }

    /// <summary>任意の結末で結果を作る。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    public static VersionControlResult Create(
        VersionControlOutcome outcome, string message, IReadOnlyList<string>? details = null)
        => new(outcome, message, details);

    /// <summary>成功。</summary>
    /// <param name="message">利用者向けメッセージ。</param>
    public static VersionControlResult Success(string message)
        => new(VersionControlOutcome.Success, message, null);

    /// <summary>行うべきことが無かった。</summary>
    /// <param name="message">利用者向けメッセージ。</param>
    public static VersionControlResult NothingToDo(string message)
        => new(VersionControlOutcome.NothingToDo, message, null);

    /// <summary>失敗。</summary>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    public static VersionControlResult Failed(string message, IReadOnlyList<string>? details = null)
        => new(VersionControlOutcome.Failed, message, details);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"{Outcome}: {Message}";
}

/// <summary>
/// 値を返す操作の結果（不変）。
///
/// <para>
/// <see cref="Value"/> は成功時のみ意味を持つ。失敗時は既定値（参照型なら null）。
/// </para>
/// </summary>
/// <typeparam name="T">戻り値の型。</typeparam>
public sealed class VersionControlResult<T> : VersionControlResult
{
    /// <summary>成功時の値。失敗時は既定値。</summary>
    public T? Value { get; }

    /// <summary>結末・値・メッセージ・詳細を指定して生成する。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="value">成功時の値。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    private VersionControlResult(
        VersionControlOutcome outcome, T? value, string message, IReadOnlyList<string>? details)
        : base(outcome, message, details)
    {
        Value = value;
    }

    /// <summary>任意の結末で結果を作る。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="value">値（失敗時は既定値でよい）。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    public static VersionControlResult<T> Create(
        VersionControlOutcome outcome, T? value, string message,
        IReadOnlyList<string>? details = null)
        => new(outcome, value, message, details);

    /// <summary>成功（値つき）。</summary>
    /// <param name="value">結果の値。</param>
    /// <param name="message">利用者向けメッセージ。</param>
    public static VersionControlResult<T> Ok(T value, string message)
        => new(VersionControlOutcome.Success, value, message, null);

    /// <summary>失敗（値なし）。</summary>
    /// <param name="message">利用者向けメッセージ。</param>
    /// <param name="details">診断用の生メッセージ。</param>
    public static new VersionControlResult<T> Failed(
        string message, IReadOnlyList<string>? details = null)
        => new(VersionControlOutcome.Failed, default, message, details);

    /// <summary>
    /// 値を返さない結果から、同じ結末の値つき結果を作る（失敗の転送用）。
    /// </summary>
    /// <param name="source">転送元（成功以外を想定）。</param>
    /// <param name="value">値（省略時は既定値）。</param>
    public static VersionControlResult<T> From(VersionControlResult source, T? value = default)
        => new(source.Outcome, value, source.Message, source.Details);
}
