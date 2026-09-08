namespace SEED;

/// <summary>
/// アセット（`assets://…`）のテキスト読み込み。
///
/// レベルデザイン用のデータを「ソースコードではなくテキストファイル」に置き、
/// ゲーム側から読み込むための最小 API。譜面表・会話台本・敵の湧きテーブルのように
/// <b>差し替えるだけで挙動が変わる</b> データを扱うことを想定している。
///
/// 実体は Rust ランタイムの `asset_fs`（PAK 同梱・実ファイルのどちらでも読める）。
/// スクリプト側はファイルシステムを直接触らないので、パッケージ実行でも
/// エディタの Play でも同じパスで動く。
///
/// <para><b>読み込みのコスト</b><br/>
/// 呼ぶたびにディスク（または PAK）から読み直す。毎フレーム呼ばず、
/// 起動時や場面の切り替えで 1 度だけ読み、結果はスクリプト側で保持すること。
/// ホットリロードしたい場合は <see cref="GetModifiedTime"/> を数秒に 1 度だけ調べ、
/// 値が変わったときにだけ読み直すのが安い。
/// </para>
///
/// <example>
/// <code>
/// // レベルデザイン用のテキストを読む
/// if (SEED.Assets.TryReadText("assets://mainGame/rhythm/beat_patterns.txt", out string text))
/// {
///     foreach (string line in text.Split('\n')) { /* … */ }
/// }
///
/// // 更新されたかを調べる（ホットリロード）
/// long stamp = SEED.Assets.GetModifiedTime("assets://mainGame/rhythm/beat_patterns.txt");
/// </code>
/// </example>
/// </summary>
public static class Assets
{
    /// <summary>更新時刻を取得できなかったことを表す値。</summary>
    public const long UnknownModifiedTime = 0;

    /// <summary>
    /// アセットを UTF-8 テキストとして読む【テキスト読み込みの唯一の入口】。
    /// BOM は取り除かれる。
    /// </summary>
    /// <param name="path">アセットパス（`assets://…` の仮想パス、または絶対パス）。</param>
    /// <param name="text">読み取った本文（失敗時は空文字列）。</param>
    /// <returns>読めたら true。ファイルが無い・UTF-8 でない場合は false。</returns>
    public static bool TryReadText(string path, out string text)
        => ScriptHost.AssetText(ScriptHost.AssetTextKindRead, path, out text);

    /// <summary>
    /// アセットを UTF-8 テキストとして読む（失敗時は <paramref name="fallback"/>）。
    /// 「読めなくても既定のデータで動かす」呼び出し側のための簡便版。
    /// </summary>
    /// <param name="path">アセットパス。</param>
    /// <param name="fallback">読めなかったときに返す文字列。</param>
    public static string ReadText(string path, string fallback = "")
        => TryReadText(path, out string text) ? text : fallback;

    /// <summary>
    /// アセットの最終更新時刻（UNIX 秒）を返す
    /// 【ホットリロードの変更検知の唯一の手段】。
    ///
    /// 取得できない場合（ファイルが無い・PAK 同梱）は
    /// <see cref="UnknownModifiedTime"/>（0）を返す。
    /// 値そのものに意味は無く、<b>前回と違うかどうか</b>だけを見て使うこと。
    /// </summary>
    /// <param name="path">アセットパス。</param>
    public static long GetModifiedTime(string path)
    {
        if (!ScriptHost.AssetText(ScriptHost.AssetTextKindModifiedTime, path, out string raw))
        {
            return UnknownModifiedTime;
        }
        return long.TryParse(raw, out long stamp) ? stamp : UnknownModifiedTime;
    }
}
