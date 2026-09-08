// ============================================================================
//  UiColorUtil.cs
//  UI の色を「インスペクタで編集できる 16 進文字列」として扱うための共通ヘルパー。
// ============================================================================

using System;

/// <summary>
/// UI の配色を <b>16 進カラーコードの文字列</b>で持ち回すための純 C# 静的ヘルパー
/// 【UI 配色の文字列⇔色 変換の唯一の置き場】。
///
/// 【なぜ文字列で色を持つのか】
/// SEED のスクリプトインスペクタは、現状 <c>SEED.Vector3</c> / <c>SEED.Color</c> 型の
/// <c>[SerializeField]</c> を<b>編集できない</b>（値は保存されるが UI から触れない）。
/// そのため「サイズランクごとの文字色」「レベルごとの文字色」のように
/// <b>企画側が触って詰めたい配色</b>は、素直に色型で持つとデータドリブンにならない。
///
/// そこで配色は <c>"#FFD54A"</c> のような<b>文字列フィールド</b>として公開し、
/// 実行時にこのヘルパーで色へ変換する。文字列なら
/// インスペクタでそのまま編集でき、シーン JSON を人が読んでも意味が分かる。
///
/// 【受け付ける書式】
/// <list type="bullet">
///   <item><c>#RGB</c>       … 各成分 1 桁（<c>#F80</c> ＝ <c>#FF8800</c>）</item>
///   <item><c>#RRGGBB</c>    … もっとも一般的な書式（アルファは 1 になる）</item>
///   <item><c>#RRGGBBAA</c>  … アルファ込み</item>
/// </list>
/// 先頭の <c>#</c> は省略してよい。前後の空白は無視する。
/// 大文字小文字は区別しない。上記以外は<b>変換失敗</b>として扱い、
/// 呼び出し側は「色を書き換えない」＝既存の色を保つ（＝設定ミスで文字が消えない）。
///
/// 【アルファの扱い】
/// UI 文字のアルファは<b>演出（フェード・点滅・アニメーションクリップ）の持ち物</b>で、
/// 配色設定の持ち物ではない。そのため配色を適用する
/// <see cref="ApplyRgb(SEED.Text?, string)"/> は <b>RGB だけ</b>を書き換え、
/// 対象が今持っているアルファをそのまま残す。
/// </summary>
public static class UiColorUtil
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>16 進カラーコードの先頭に付く記号（省略可）。</summary>
    private const char HexPrefix = '#';

    /// <summary>短縮形（<c>#RGB</c>）の桁数。</summary>
    private const int ShortRgbLength = 3;

    /// <summary>標準形（<c>#RRGGBB</c>）の桁数。</summary>
    private const int RgbLength = 6;

    /// <summary>アルファ込み（<c>#RRGGBBAA</c>）の桁数。</summary>
    private const int RgbaLength = 8;

    /// <summary>1 成分あたりの桁数（標準形・アルファ込み共通）。</summary>
    private const int DigitsPerChannel = 2;

    /// <summary>8bit 成分の最大値（0〜1 へ正規化するための除数）。</summary>
    private const float ChannelMax = 255f;

    /// <summary>不透明を表すアルファ（アルファ指定の無い書式で使う既定値）。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>補間の下限（0）。</summary>
    private const float LerpMin = 0f;

    /// <summary>補間の中点（低→中→高の 3 色グラデーションの折り返し点）。</summary>
    private const float LerpMid = 0.5f;

    /// <summary>補間の上限（1）。</summary>
    private const float LerpMax = 1f;

    // ─── 変換 ───────────────────────────────────────────────

    /// <summary>
    /// 16 進カラーコードの文字列を色へ変換する【文字列→色 変換の唯一の実装】。
    /// </summary>
    /// <param name="hex">
    /// <c>#RGB</c> / <c>#RRGGBB</c> / <c>#RRGGBBAA</c> のいずれか（<c>#</c> は省略可）。
    /// 空・null・書式違いは変換失敗になる。
    /// </param>
    /// <param name="color">変換できた色（失敗時は既定値）。</param>
    /// <returns>変換できたら true。</returns>
    public static bool TryParse(string? hex, out SEED.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) { return false; }

        // 前後の空白と先頭の '#' を落として桁だけにする
        string body = hex!.Trim().TrimStart(HexPrefix);

        switch (body.Length)
        {
            // #RGB … 1 桁を 2 桁へ展開する（F → FF）
            case ShortRgbLength:
            {
                if (!TryReadNibble(body, 0, out float r)) { return false; }
                if (!TryReadNibble(body, 1, out float g)) { return false; }
                if (!TryReadNibble(body, 2, out float b)) { return false; }
                color = new SEED.Color(r, g, b, AlphaOpaque);
                return true;
            }

            // #RRGGBB … アルファは不透明
            case RgbLength:
            {
                if (!TryReadByte(body, 0, out float r)) { return false; }
                if (!TryReadByte(body, 1, out float g)) { return false; }
                if (!TryReadByte(body, 2, out float b)) { return false; }
                color = new SEED.Color(r, g, b, AlphaOpaque);
                return true;
            }

            // #RRGGBBAA … アルファ込み
            case RgbaLength:
            {
                if (!TryReadByte(body, 0, out float r)) { return false; }
                if (!TryReadByte(body, 1, out float g)) { return false; }
                if (!TryReadByte(body, 2, out float b)) { return false; }
                if (!TryReadByte(body, 3, out float a)) { return false; }
                color = new SEED.Color(r, g, b, a);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 16 進カラーコードを色へ変換する。失敗したら差し替え色を返す。
    /// 「設定ミスでも必ず何かの色になる」ことを保証したい場面で使う。
    /// </summary>
    /// <param name="hex">16 進カラーコード。</param>
    /// <param name="fallback">変換できなかったときに返す色。</param>
    /// <returns>変換できた色、または <paramref name="fallback"/>。</returns>
    public static SEED.Color ParseOr(string? hex, SEED.Color fallback)
        => TryParse(hex, out SEED.Color parsed) ? parsed : fallback;

    // ─── 適用 ───────────────────────────────────────────────

    /// <summary>
    /// テキストの色味（RGB）だけを 16 進カラーコードで塗り替える
    /// 【文字の配色適用の唯一の入口】。
    ///
    /// <b>アルファは触らない</b>。UI 文字のアルファはフェード・点滅・
    /// アニメーションクリップが所有しており、配色の都合で上書きすると
    /// 「消えているはずの文字が出る／出るはずの文字が消える」事故になるため。
    /// </summary>
    /// <param name="text">対象のテキスト（未設定・破棄済みなら何もしない）。</param>
    /// <param name="hex">16 進カラーコード（書式違い・空なら何もしない）。</param>
    /// <returns>実際に塗り替えたら true。</returns>
    public static bool ApplyRgb(SEED.Text? text, string? hex)
    {
        if (text is not { } t || !t.IsValid) { return false; }
        if (!TryParse(hex, out SEED.Color rgb)) { return false; }

        // 今のアルファを保ったまま色味だけ差し替える
        t.Color = new SEED.Color(rgb.r, rgb.g, rgb.b, t.Color.a);
        return true;
    }

    /// <summary>
    /// スプライトの色味（RGB）だけを 16 進カラーコードで塗り替える。
    /// 意味は <see cref="ApplyRgb(SEED.Text?, string)"/> と同じ。
    /// </summary>
    /// <param name="sprite">対象のスプライト（未設定・破棄済みなら何もしない）。</param>
    /// <param name="hex">16 進カラーコード（書式違い・空なら何もしない）。</param>
    /// <returns>実際に塗り替えたら true。</returns>
    public static bool ApplyRgb(SEED.Sprite? sprite, string? hex)
    {
        if (sprite is not { } s || !s.IsValid) { return false; }
        if (!TryParse(hex, out SEED.Color rgb)) { return false; }

        s.Color = new SEED.Color(rgb.r, rgb.g, rgb.b, s.Color.a);
        return true;
    }

    // ─── グラデーション ──────────────────────────────────────

    /// <summary>
    /// 2 色の RGB を線形補間する（アルファは <paramref name="from"/> のものを引き継ぐ）。
    /// </summary>
    /// <param name="from">t=0 のときの色。</param>
    /// <param name="to">t=1 のときの色。</param>
    /// <param name="t">補間係数（0〜1 にクランプする）。</param>
    /// <returns>補間した色。</returns>
    public static SEED.Color LerpRgb(SEED.Color from, SEED.Color to, float t)
    {
        float k = SEED.Mathf.Clamped01(t);
        return new SEED.Color(
            from.r + (to.r - from.r) * k,
            from.g + (to.g - from.g) * k,
            from.b + (to.b - from.b) * k,
            from.a);
    }

    /// <summary>
    /// 低・中・高の 3 色を並べた<b>2 段グラデーション</b>から 1 色を取り出す
    /// 【3 色グラデーションの唯一の実装】。
    ///
    /// <paramref name="t"/> が 0〜0.5 なら低→中、0.5〜1 なら中→高を補間する。
    /// 「レベルが上がるほど色が変わる」ような段階的な配色に使う。
    /// </summary>
    /// <param name="lowHex">t=0 の色（16 進）。</param>
    /// <param name="midHex">t=0.5 の色（16 進）。</param>
    /// <param name="highHex">t=1 の色（16 進）。</param>
    /// <param name="t">位置（0〜1 にクランプする）。</param>
    /// <param name="fallback">いずれかの書式が不正なときに使う色。</param>
    /// <returns>グラデーション上の色。</returns>
    public static SEED.Color Gradient3(
        string? lowHex, string? midHex, string? highHex, float t, SEED.Color fallback)
    {
        SEED.Color low  = ParseOr(lowHex,  fallback);
        SEED.Color mid  = ParseOr(midHex,  fallback);
        SEED.Color high = ParseOr(highHex, fallback);

        float k = SEED.Mathf.Clamped01(t);

        // 前半（0〜0.5）は低→中、後半（0.5〜1）は中→高。
        // どちらの区間も内部では 0〜1 の係数へ引き伸ばしてから補間する。
        return k <= LerpMid
            ? LerpRgb(low,  mid,  (k - LerpMin) / LerpMid)
            : LerpRgb(mid,  high, (k - LerpMid) / (LerpMax - LerpMid));
    }

    /// <summary>
    /// 「1〜最大値」の段階を 0〜1 の位置へ写す（グラデーションの引数を作るための補助）。
    /// 段階が 1 つしかない（最大値 ≦ 1）ときは 0 を返す（＝低の色になる）。
    /// </summary>
    /// <param name="step">現在の段階（1 始まり）。</param>
    /// <param name="maxStep">段階の最大値。</param>
    /// <returns>0〜1 の位置。</returns>
    public static float Step01(int step, int maxStep)
    {
        const int FirstStep = 1;
        if (maxStep <= FirstStep) { return LerpMin; }
        return SEED.Mathf.Clamped01((float)(step - FirstStep) / (maxStep - FirstStep));
    }

    // ─── 内部処理: 桁の読み取り ─────────────────────────────

    /// <summary>
    /// <c>#RRGGBB</c> 形式から <paramref name="channelIndex"/> 番目の成分（2 桁）を読む。
    /// </summary>
    /// <param name="body">'#' を除いた桁の並び。</param>
    /// <param name="channelIndex">成分の位置（0=R / 1=G / 2=B / 3=A）。</param>
    /// <param name="value">0〜1 に正規化した成分値。</param>
    /// <returns>読めたら true。</returns>
    private static bool TryReadByte(string body, int channelIndex, out float value)
    {
        value = LerpMin;
        int offset = channelIndex * DigitsPerChannel;
        if (!TryHexDigit(body[offset], out int hi)) { return false; }
        if (!TryHexDigit(body[offset + 1], out int lo)) { return false; }

        // 上位 4bit と下位 4bit を合成して 0〜255 にしてから正規化する
        const int NibbleShift = 16;
        value = (hi * NibbleShift + lo) / ChannelMax;
        return true;
    }

    /// <summary>
    /// <c>#RGB</c> 形式から <paramref name="channelIndex"/> 番目の成分（1 桁）を読む。
    /// 1 桁は 2 桁へ展開する（<c>F</c> → <c>FF</c>）ので、最大値がきちんと 1 になる。
    /// </summary>
    /// <param name="body">'#' を除いた桁の並び。</param>
    /// <param name="channelIndex">成分の位置（0=R / 1=G / 2=B）。</param>
    /// <param name="value">0〜1 に正規化した成分値。</param>
    /// <returns>読めたら true。</returns>
    private static bool TryReadNibble(string body, int channelIndex, out float value)
    {
        value = LerpMin;
        if (!TryHexDigit(body[channelIndex], out int digit)) { return false; }

        // F → FF のように同じ桁を 2 回並べた値にする（= digit * 17）
        const int NibbleExpand = 17;
        value = digit * NibbleExpand / ChannelMax;
        return true;
    }

    /// <summary>16 進数 1 桁（0-9 / a-f / A-F）を数値へ変換する。</summary>
    /// <param name="c">対象の文字。</param>
    /// <param name="value">0〜15 の値。</param>
    /// <returns>16 進数字なら true。</returns>
    private static bool TryHexDigit(char c, out int value)
    {
        const int DecimalBase = 10;

        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + DecimalBase; return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + DecimalBase; return true; }

        value = 0;
        return false;
    }
}

/// <summary>
/// サイズランク（<c>"S"</c> / <c>"A"</c> / <c>"B"</c> / <c>"C"</c>）から
/// 表示色を選ぶための共通ヘルパー【ランク配色の対応表の唯一の置き場】。
///
/// リザルトパネルと図鑑カードは<b>同じ魚の同じランク</b>を別画面で見せるので、
/// 「S は金、A は赤」といった対応がずれると強い違和感になる。
/// 対応表をここに 1 つだけ置き、両画面がこれを通すことでずれを防ぐ。
///
/// <b>色そのものは持たない</b>。実際の色はインスペクタで各スクリプトが持つ
/// 4 つの 16 進文字列であり、このクラスは「ランク文字 → 4 つのうちどれか」の
/// 選択だけを担う（配色の差し替えはコード変更なしで行える）。
/// </summary>
public static class RankColorTable
{
    /// <summary>最上位ランクの文字。</summary>
    private const string RankS = "S";

    /// <summary>上位ランクの文字。</summary>
    private const string RankA = "A";

    /// <summary>中位ランクの文字。</summary>
    private const string RankB = "B";

    /// <summary>下位ランクの文字。</summary>
    private const string RankC = "C";

    /// <summary>
    /// ランク文字に対応する 16 進カラーコードを選ぶ。
    ///
    /// ランク文字の前後の空白は無視し、大文字小文字は区別しない。
    /// 未記録（空文字）や表に無い文字は <paramref name="unknownHex"/> になる。
    /// </summary>
    /// <param name="rank">ランク文字（<c>"S"</c>〜<c>"C"</c>）。</param>
    /// <param name="sHex">S の色。</param>
    /// <param name="aHex">A の色。</param>
    /// <param name="bHex">B の色。</param>
    /// <param name="cHex">C の色。</param>
    /// <param name="unknownHex">上記以外（未記録を含む）の色。</param>
    /// <returns>選ばれた 16 進カラーコード。</returns>
    public static string Select(
        string? rank, string sHex, string aHex, string bHex, string cHex, string unknownHex)
    {
        if (string.IsNullOrWhiteSpace(rank)) { return unknownHex; }

        string key = rank!.Trim();
        if (string.Equals(key, RankS, StringComparison.OrdinalIgnoreCase)) { return sHex; }
        if (string.Equals(key, RankA, StringComparison.OrdinalIgnoreCase)) { return aHex; }
        if (string.Equals(key, RankB, StringComparison.OrdinalIgnoreCase)) { return bHex; }
        if (string.Equals(key, RankC, StringComparison.OrdinalIgnoreCase)) { return cHex; }

        return unknownHex;
    }
}
