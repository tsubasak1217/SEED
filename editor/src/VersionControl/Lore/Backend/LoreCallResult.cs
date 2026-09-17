// ============================================================
//  LoreCallResult.cs — Lore 呼び出し 1 回分の生の結末
//
//  【役割】
//  LoreVcs は失敗を LoreError 例外（ReturnCode と Messages を持つ）で返すが、
//  それをそのまま上へ流すと呼び出し側が全箇所で try/catch を書くことになる。
//  バックエンド境界で必ずこの型へ畳み、例外を境界の外へ出さない。
//
//  【なぜ Lore の語彙のまま持つのか】
//  ここはまだ「Lore の生の結果」の層。利用者向けの言い換え（送信 / 最新を取得）は
//  1 つ上の LoreProvider が行う。層を跨いで意味を変換しないことで、
//  「どこで意味が変わったか」を追えるようにする。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// 競合解決でどちらの親リビジョンを採るか（Lore の語彙のまま）。
///
/// <para>
/// 利用者向けの「自分の変更を残す / リモートを採用」との対応付けは
/// <see cref="LoreConflictResolutionMap"/> だけが知っている。
/// この enum を直接 UI へ見せないこと（Lore 側で意味が逆転しているため）。
/// </para>
/// </summary>
public enum LoreResolveSide
{
    /// <summary>Lore の <c>resolve mine</c>（parents()[0] = parent_self）。</summary>
    Mine,

    /// <summary>Lore の <c>resolve theirs</c>（parents()[1] = parent_other）。</summary>
    Theirs,
}

/// <summary>
/// Lore 呼び出し 1 回分の結末（不変）。
/// </summary>
public sealed class LoreCallResult
{
    /// <summary>Lore が成功を表すときに返す値。</summary>
    public const int RETURN_CODE_SUCCESS = 0;

    /// <summary>
    /// 呼び出し側の中断・期限切れで実行しなかったことを表す内部コード。
    ///
    /// <para>
    /// ★Lore が返し得ない値にしておくこと。実測で Lore は一般的な失敗に
    /// <c>-1</c> を返す（分岐による push 拒否も -1 だった）ため、-1 を
    /// 中断の印にすると「分岐で弾かれた」が「中断された」に化け、
    /// 利用者に「最新を取得してください」と案内できなくなる。
    /// </para>
    /// </summary>
    public const int RETURN_CODE_CANCELED = int.MinValue;

    /// <summary>
    /// Lore の例外ではない失敗（ネイティブ DLL 不在など）を表す内部コード。
    /// これも Lore が返し得ない値にする。
    /// </summary>
    public const int RETURN_CODE_INTERNAL_FAILURE = int.MinValue + 1;

    /// <summary>成功したか。</summary>
    public bool Succeeded { get; }

    /// <summary>Lore の戻り値コード（成功なら 0）。</summary>
    public int ReturnCode { get; }

    /// <summary>Lore が返したメッセージ（英語。診断用）。</summary>
    public IReadOnlyList<string> Messages { get; }

    /// <summary>中断・期限切れで実行しなかったか。</summary>
    public bool WasCanceled => ReturnCode == RETURN_CODE_CANCELED;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="succeeded">成功したか。</param>
    /// <param name="returnCode">Lore の戻り値コード。</param>
    /// <param name="messages">Lore が返したメッセージ。</param>
    private LoreCallResult(bool succeeded, int returnCode, IReadOnlyList<string>? messages)
    {
        Succeeded  = succeeded;
        ReturnCode = returnCode;
        Messages   = messages ?? Array.Empty<string>();
    }

    /// <summary>成功。</summary>
    public static LoreCallResult Success { get; } =
        new(true, RETURN_CODE_SUCCESS, Array.Empty<string>());

    /// <summary>失敗（Lore の戻り値コードとメッセージつき）。</summary>
    /// <param name="returnCode">Lore の戻り値コード。</param>
    /// <param name="messages">Lore が返したメッセージ。</param>
    public static LoreCallResult Failure(int returnCode, IReadOnlyList<string>? messages)
        => new(false, returnCode, messages);

    /// <summary>中断・期限切れ。</summary>
    /// <param name="reason">中断の理由（診断用）。</param>
    public static LoreCallResult Canceled(string reason)
        => new(false, RETURN_CODE_CANCELED, new[] { reason });

    /// <summary>
    /// メッセージのどれかに指定した部分文字列が含まれるか（大文字小文字を区別しない）。
    /// push の拒否理由の判定などに使う。
    /// </summary>
    /// <param name="marker">探す部分文字列。</param>
    public bool MessagesContain(string marker)
        => !string.IsNullOrEmpty(marker)
           && Messages.Any(m => m is not null
                                && m.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => Succeeded
            ? "ok"
            : $"rc={ReturnCode} {string.Join(" / ", Messages)}";
}
