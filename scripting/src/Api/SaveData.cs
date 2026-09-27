using System;

namespace SEED;

/// <summary>
/// ゲーム進行の永続化（セーブデータ）。
///
/// 資金・強化レベル・図鑑・ハイスコア・履歴のような「シーンを切り替えても、
/// ゲームを終了して起動し直しても残したい値」をキー・バリューで保存する。
///
/// 実体は Rust ランタイムが持つ JSON 1 ファイル。スクリプト側はファイルパスも
/// ファイル IO も意識しない（保存先はパッケージ実行かエディタ Play かで
/// エンジンが自動的に切り替える）。
///
/// <para><b>読み書きのタイミング</b><br/>
/// Set/Get はメモリ上のストアに対する操作で、毎フレーム呼んでも安い。
/// ディスクへ書き出すのは <see cref="Save"/> を呼んだときと、
/// Play 終了時・アプリ終了時の自動保存だけ。区切りの良いところ
/// （魚を釣った直後・ショップで購入した直後）で <see cref="Save"/> を呼ぶこと。
/// </para>
///
/// <para><b>耐久性</b><br/>
/// 書き出しは一時ファイルへ書いてディスクへ届けてから置き換え、直前の世代を save.json.bak に残す。
/// 書き出しの途中で落ちても・電源が切れても、本体か 1 つ前の世代のどちらかから読める。
/// どこから読んだかは <see cref="RecoveredFrom"/> で分かる。
/// 複数のキーを 1 まとまりで書き換えるときは <see cref="Batch"/> で包むか、1 つの文書（JSON）を 1 つのキーに入れる。
/// </para>
///
/// <para><b>型の扱い</b><br/>
/// 整数と実数は相互に読み替えられる（実数→整数は 0 方向へ切り捨て）。
/// 文字列と数値は相互変換しない — 型を間違えた読み取りは既定値を返すので、
/// 書いたときと同じ型で読むこと。
/// </para>
///
/// <example>
/// <code>
/// int money = SaveData.GetInt("money", 0);
/// SaveData.SetInt("money", money + 500);
/// SaveData.SetFloat("best_size_bass", 41.5f);
/// SaveData.Save();
/// </code>
/// </example>
/// </summary>
public static class SaveData
{
    // ── Rust 側 host_api.rs の kind 定数と一致させること ──────────

    /// <summary>数値 API の kind: 書き込み（Rust 側 SAVE_KIND_SET）。</summary>
    private const int KindSet = 0;
    /// <summary>数値 API の kind: 読み取り（Rust 側 SAVE_KIND_GET）。</summary>
    private const int KindGet = 1;

    /// <summary>制御 API の kind: キーの存在判定（Rust 側 SAVE_CTL_HAS）。</summary>
    private const int CtlHas = 0;
    /// <summary>制御 API の kind: キーを 1 つ削除（Rust 側 SAVE_CTL_DELETE_KEY）。</summary>
    private const int CtlDeleteKey = 1;
    /// <summary>制御 API の kind: 全キー削除（Rust 側 SAVE_CTL_DELETE_ALL）。</summary>
    private const int CtlDeleteAll = 2;
    /// <summary>制御 API の kind: ディスクへ書き出し（Rust 側 SAVE_CTL_SAVE）。</summary>
    private const int CtlSave = 3;
    /// <summary>制御 API の kind: Batch を 1 段始める（Rust 側 SAVE_CTL_BATCH_BEGIN）。</summary>
    private const int CtlBatchBegin = 4;
    /// <summary>制御 API の kind: Batch を 1 段終える（Rust 側 SAVE_CTL_BATCH_END）。</summary>
    private const int CtlBatchEnd = 5;
    /// <summary>制御 API の kind: ロードでどこから読んだか（Rust 側 SAVE_CTL_RECOVERED_FROM）。</summary>
    private const int CtlRecoveredFrom = 6;

    // ── 書き込み ────────────────────────────────────────────────

    /// <summary>整数を保存する（同じキーの既存値は型ごと置き換わる）。</summary>
    /// <param name="key">キー。空文字列は無視される。</param>
    /// <param name="value">保存する値。</param>
    public static void SetInt(string key, int value) => ScriptHost.SaveInt(KindSet, key, value, out _);

    /// <summary>64bit 整数を保存する（累計スコアなど int に収まらない値用）。</summary>
    public static void SetLong(string key, long value) => ScriptHost.SaveInt(KindSet, key, value, out _);

    /// <summary>実数を保存する（同じキーの既存値は型ごと置き換わる）。</summary>
    public static void SetFloat(string key, float value) => ScriptHost.SaveFloat(KindSet, key, value, out _);

    /// <summary>
    /// 文字列を保存する（同じキーの既存値は型ごと置き換わる）。
    /// 長さの上限は無い（数 MB の JSON 文書も入れられる。長い値はスタックではなくヒープを経由して渡す）。
    /// </summary>
    public static void SetString(string key, string value) => ScriptHost.SaveSetString(key, value);

    /// <summary>真偽値を保存する（内部表現は整数 0/1）。</summary>
    public static void SetBool(string key, bool value) => SetInt(key, value ? 1 : 0);

    // ── 読み取り ────────────────────────────────────────────────

    /// <summary>整数を読む。キーが無い / 文字列が入っている場合は <paramref name="defaultValue"/>。</summary>
    public static int GetInt(string key, int defaultValue = 0)
        => ScriptHost.SaveInt(KindGet, key, 0, out long v) ? unchecked((int)v) : defaultValue;

    /// <summary>64bit 整数を読む。キーが無い / 型が合わない場合は <paramref name="defaultValue"/>。</summary>
    public static long GetLong(string key, long defaultValue = 0)
        => ScriptHost.SaveInt(KindGet, key, 0, out long v) ? v : defaultValue;

    /// <summary>実数を読む。キーが無い / 文字列が入っている場合は <paramref name="defaultValue"/>。</summary>
    public static float GetFloat(string key, float defaultValue = 0f)
        => ScriptHost.SaveFloat(KindGet, key, 0f, out float v) ? v : defaultValue;

    /// <summary>文字列を読む。キーが無い / 数値が入っている場合は <paramref name="defaultValue"/>。</summary>
    public static string GetString(string key, string defaultValue = "")
        => ScriptHost.SaveGetString(key, out string v) ? v : defaultValue;

    /// <summary>真偽値を読む（0 以外を true とみなす）。</summary>
    public static bool GetBool(string key, bool defaultValue = false)
        => ScriptHost.SaveInt(KindGet, key, 0, out long v) ? v != 0 : defaultValue;

    // ── 問い合わせ・削除 ────────────────────────────────────────

    /// <summary>キーが保存されているか（型は問わない）。</summary>
    public static bool Has(string key) => ScriptHost.SaveControl(CtlHas, key);

    /// <summary>キーを 1 つ削除する。削除した場合 true、元から無ければ false。</summary>
    public static bool DeleteKey(string key) => ScriptHost.SaveControl(CtlDeleteKey, key);

    /// <summary>
    /// すべてのキーを削除する（ニューゲーム用）。
    /// ディスク上のファイルは <see cref="Save"/> するまで残る。
    /// </summary>
    public static void DeleteAll() => ScriptHost.SaveControl(CtlDeleteAll, null);

    /// <summary>
    /// 起動して最初にセーブを読んだとき、どこから読んだか。
    /// <see cref="SaveRecovery.Backup"/> なら本体が無い・壊れていて 1 つ前の世代から読んだ（直前の保存が失われた可能性）、
    /// <see cref="SaveRecovery.Lost"/> なら読めるものが無く空で始めた。利用者へ知らせるときに使う。
    /// ホスト API が使えないときは <see cref="SaveRecovery.None"/>。
    /// </summary>
    public static SaveRecovery RecoveredFrom
    {
        get
        {
            int code = ScriptHost.SaveControlCode(CtlRecoveredFrom);
            return code switch
            {
                (int)SaveRecovery.Backup => SaveRecovery.Backup,
                (int)SaveRecovery.Lost => SaveRecovery.Lost,
                // 普段どおり・初回・ホスト API 未登録（-1）・知らない番号（版の食い違い）
                _ => SaveRecovery.None,
            };
        }
    }

    // ── 永続化 ──────────────────────────────────────────────────

    /// <summary>
    /// 現在の内容をディスクへ書き出す。成功時 true。
    /// Play 終了時・アプリ終了時にも自動で書き出されるが、
    /// 進行の区切りでは明示的に呼ぶこと（強制終了への保険になる）。
    /// <see cref="Batch"/> の中で呼ぶと、書き出しは最も外側の Batch の終わりまで待たされ、ここでは true を返す
    /// （書き出しの成否は <see cref="Batch"/> の戻り値）。
    /// </summary>
    public static bool Save() => ScriptHost.SaveControl(CtlSave, null);

    /// <summary>
    /// 複数のキーの書き換えを 1 まとまりにする。<paramref name="action"/> を実行している間は、
    /// 自動保存（Android の背面・アプリの破棄を含む。別スレッドからでも）と <see cref="Save"/> をディスクへ書かせず、
    /// 最も外側の Batch の終わりに 1 回だけ書く（Batch の間に書き出しの要求が無ければ書かない）。
    /// これで「お金は減ったが履歴は無い」といった半端な組み合わせがディスクに残らない。入れ子にしてよい（数える）。
    /// <para>
    /// 取り消しはしない: <paramref name="action"/> が例外を投げても Batch は終わる（深さは戻る）が、
    /// それまでに書き換えたキーは元に戻らない。中は短く保ち、待ち（非同期・長い計算）を入れないこと。
    /// Batch の途中でプロセスが終わると、Batch より前の未書き出しの変更も書かれない（ディスクは前回の書き出しのまま）。
    /// </para>
    /// </summary>
    /// <param name="action">まとめて行う書き換え。null なら何もしない。</param>
    /// <returns>終わりの書き出しが要らなかった・成功したら true。失敗した・ホスト API が使えないなら false。</returns>
    public static bool Batch(Action action)
    {
        if (action is null) return true;

        ScriptHost.SaveControl(CtlBatchBegin, null);
        bool written = false;
        try
        {
            action();
        }
        finally
        {
            // 例外でも必ず深さを戻す（戻さないと、以後の書き出しがすべて待たされたままになる）
            written = ScriptHost.SaveControl(CtlBatchEnd, null);
        }
        return written;
    }
}
