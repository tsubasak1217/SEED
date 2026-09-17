// ============================================================
//  LoreRevisionMetadataTranslator.cs — リビジョンのメタデータ → 履歴の表示項目
//
//  【なぜ要るのか】
//  Lore v0.9.0 の `revision history` が返すイベント（REVISION_HISTORY_ENTRY）は
//  リビジョン番号・ハッシュ・親しか持たず、**コミットメッセージ・作者・日時が無い**。
//  それらはリビジョンに付いた「メタデータ」（キー → 型つきの値）として保存されており、
//  `revision metadata list` で 1 リビジョンずつ引く必要がある。
//  この変換は「引いてきたキー・値の並び」から履歴表示の 3 項目を取り出す純関数。
//
//  【キー名を対応表にしている理由】
//  どのキーにメッセージ・作者・日時が入るかは Lore の実装が決めており、
//  版が上がれば増減し得る。呼び出し側に `if (key == "message")` を散らすと
//  1 か所直し忘れた瞬間に履歴が空欄に戻る。候補キーの並びをここ 1 か所へ集め、
//  単体テストで固定する（データドリブン: キーが変わってもこの配列だけを直す）。
//  候補は「先に並んだものを優先」。未知のキーは無視する。
//
//  【時刻の単位を推定する理由】
//  Lore が Unix 秒・ミリ秒・マイクロ秒・ナノ秒のどれで持つかは版により違い得る。
//  桁数で単位を判定し、秒へ丸める。判定できない値は「不明（0）」にして、
//  **でたらめな日付を表示しない**（1970 年や 3 万年後の日付が並ぶ方が害が大きい）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// リビジョンのメタデータから取り出した、履歴表示に使う項目（不変）。
/// </summary>
/// <param name="Author">コミットした人（見つからなければ空文字）。</param>
/// <param name="Message">コミットメッセージ（見つからなければ空文字）。</param>
/// <param name="UnixTimeSeconds">コミット時刻の Unix 秒（見つからなければ 0）。</param>
public readonly record struct RevisionMetadataFields(
    string Author, string Message, long UnixTimeSeconds)
{
    /// <summary>何も取れなかったことを表す値。</summary>
    public static RevisionMetadataFields Empty { get; } = new(string.Empty, string.Empty, 0L);

    /// <summary>1 項目でも取れたか（取れていなければ metadata を引く意味が無かった）。</summary>
    public bool HasAny
        => Author.Length > 0 || Message.Length > 0 || UnixTimeSeconds > 0;
}

/// <summary>
/// メタデータのキー・値から履歴の表示項目を取り出す純関数群。
/// </summary>
public static class LoreRevisionMetadataTranslator
{
    // ── キーの対応表（ここだけを直せばよい）────────────────────
    //  比較は大文字小文字・区切り（`_` `-` `.` 空白）を無視して行うため、
    //  候補は正規化済みの形（小文字・区切り無し）で並べる。
    //  並び順 = 優先順位。先に見つかったものを採用する。

    /// <summary>
    /// コミットメッセージが入り得るキーの候補（優先順）。
    /// ★実機で確認した実際のキーは <c>message</c>。残りは版が変わったときの保険。
    /// </summary>
    private static readonly string[] MESSAGE_KEYS =
    {
        "message",
        "commitmessage",
        "revisionmessage",
        "description",
        "comment",
        "summary",
        "msg",
    };

    /// <summary>
    /// コミットした人が入り得るキーの候補（優先順）。
    ///
    /// <para>
    /// ★実機（Lore v0.9.0 + loreserver）で確認した実際のキーは
    /// <c>committed-by</c> と <c>created-by</c> の 2 つ（どちらも identity の文字列）。
    /// 履歴に出したいのは「そのリビジョンを作った人」なので <c>committed-by</c> を優先する。
    /// amend されたリビジョンでは両者が食い違い得る。
    /// </para>
    /// </summary>
    private static readonly string[] AUTHOR_KEYS =
    {
        "committedby",
        "createdby",
        "author",
        "committer",
        "identity",
        "user",
        "username",
        "owner",
        "email",
    };

    /// <summary>
    /// コミット時刻が入り得るキーの候補（優先順）。
    /// ★実機で確認した実際のキーは <c>timestamp</c>で、値は **Unix ミリ秒**
    /// （例 1789661083806）。単位は桁から推定する（<see cref="ToPlausibleUnixSeconds"/>）。
    /// </summary>
    private static readonly string[] TIMESTAMP_KEYS =
    {
        "timestamp",
        "committimestamp",
        "committedat",
        "createdat",
        "datetime",
        "date",
        "time",
    };

    // ── 時刻の単位判定に使う境界 ────────────────────────────────
    //  「現実的な日時か」を桁で判定する。単位の取り違えは
    //  1970 年や遠い未来の日付として必ず目に見える形で出るので、
    //  判定できない値は 0（不明）にして表示しない。

    /// <summary>これより小さい秒は「不明」とみなす下限（1990-01-01 の Unix 秒）。</summary>
    private const long MIN_PLAUSIBLE_UNIX_SECONDS = 631_152_000L;

    /// <summary>これより大きい秒は「不明」とみなす上限（2100-01-01 の Unix 秒）。</summary>
    private const long MAX_PLAUSIBLE_UNIX_SECONDS = 4_102_444_800L;

    /// <summary>ミリ秒 → 秒の除数。</summary>
    private const long MILLISECONDS_PER_SECOND = 1_000L;

    /// <summary>マイクロ秒 → 秒の除数。</summary>
    private const long MICROSECONDS_PER_SECOND = 1_000_000L;

    /// <summary>ナノ秒 → 秒の除数。</summary>
    private const long NANOSECONDS_PER_SECOND = 1_000_000_000L;

    /// <summary>
    /// 試す除数の並び（秒・ミリ秒・マイクロ秒・ナノ秒）。
    /// 小さい単位から順に試し、最初に「現実的な日時」になったものを採る。
    /// </summary>
    private static readonly long[] TIME_DIVISORS =
    {
        1L,
        MILLISECONDS_PER_SECOND,
        MICROSECONDS_PER_SECOND,
        NANOSECONDS_PER_SECOND,
    };

    /// <summary>
    /// 1 リビジョン分のメタデータ行から、履歴の表示項目を取り出す。
    /// </summary>
    /// <param name="rows">`revision metadata list` が返した行。</param>
    /// <returns>取り出せた項目（取れなかったものは空・0）。</returns>
    public static RevisionMetadataFields Extract(IReadOnlyList<LoreMetadataRow>? rows)
    {
        if (rows is null || rows.Count == 0) return RevisionMetadataFields.Empty;

        // 候補キーごとに「何番目の候補で見つかったか」を持ち、より優先度の高い
        // 候補が後から出てきたら置き換える（メタデータの並び順に依存しないため）。
        var message   = string.Empty;
        var messageAt = int.MaxValue;
        var author    = string.Empty;
        var authorAt  = int.MaxValue;
        var timestamp = 0L;
        var timeAt    = int.MaxValue;

        foreach (var row in rows)
        {
            var key = NormalizeKey(row.Key);
            if (key.Length == 0) continue;

            // ── 文字列で来る項目（メッセージ・作者）──
            if (row.Kind == LoreMetadataValueKind.String)
            {
                var text = (row.StringValue ?? string.Empty).Trim();
                if (text.Length == 0) continue;

                var messageRank = RankOf(MESSAGE_KEYS, key);
                if (messageRank < messageAt)
                {
                    message   = text;
                    messageAt = messageRank;
                    continue;
                }

                var authorRank = RankOf(AUTHOR_KEYS, key);
                if (authorRank < authorAt)
                {
                    author   = text;
                    authorAt = authorRank;
                }

                continue;
            }

            // ── 数値で来る項目（日時）──
            if (row.Kind == LoreMetadataValueKind.Numeric)
            {
                var timeRank = RankOf(TIMESTAMP_KEYS, key);
                if (timeRank >= timeAt) continue;

                var seconds = ToPlausibleUnixSeconds(row.NumericValue);
                if (seconds <= 0) continue;

                timestamp = seconds;
                timeAt    = timeRank;
            }
        }

        return new RevisionMetadataFields(author, message, timestamp);
    }

    /// <summary>
    /// キーを比較用に正規化する（小文字化し、区切り文字を落とす）。
    ///
    /// <para>
    /// Lore が <c>commit_message</c> と <c>commitMessage</c> のどちらで持っていても
    /// 同じ候補で拾えるようにするため。
    /// </para>
    /// </summary>
    /// <param name="key">元のキー。</param>
    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;

        // 英数字だけを残して小文字にする（`_` `-` `.` 空白・名前空間の区切りを無視）。
        var buffer = new char[key.Length];
        var length = 0;
        foreach (var c in key)
        {
            if (char.IsLetterOrDigit(c)) buffer[length++] = char.ToLowerInvariant(c);
        }

        return length == 0 ? string.Empty : new string(buffer, 0, length);
    }

    /// <summary>
    /// 候補の並びの中で何番目に一致したかを返す（一致しなければ <see cref="int.MaxValue"/>）。
    /// 小さいほど優先度が高い。
    /// </summary>
    /// <param name="candidates">候補キー（正規化済み・優先順）。</param>
    /// <param name="normalizedKey">正規化済みのキー。</param>
    private static int RankOf(string[] candidates, string normalizedKey)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            if (string.Equals(candidates[i], normalizedKey, StringComparison.Ordinal)) return i;
        }

        return int.MaxValue;
    }

    /// <summary>
    /// メタデータの数値を Unix 秒へ直す。単位（秒・ミリ・マイクロ・ナノ）は桁で推定し、
    /// 現実的な日時にならない値は 0（不明）を返す。
    /// </summary>
    /// <param name="raw">メタデータの生の数値。</param>
    public static long ToPlausibleUnixSeconds(ulong raw)
    {
        if (raw == 0UL) return 0L;

        // long に収まらない巨大な値は素直に「不明」とする。
        if (raw > long.MaxValue) return 0L;

        var value = (long)raw;
        foreach (var divisor in TIME_DIVISORS)
        {
            var seconds = value / divisor;
            if (seconds is >= MIN_PLAUSIBLE_UNIX_SECONDS and <= MAX_PLAUSIBLE_UNIX_SECONDS)
            {
                return seconds;
            }
        }

        return 0L;
    }
}
