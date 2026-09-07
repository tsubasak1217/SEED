// ============================================================================
//  TypewriterText.cs
//  「1 文字ずつ表示する」文字送りロジックだけを持つ純 C# ヘルパー。
// ============================================================================

using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// 文字送り（タイプライタ表示）の進行を担う純 C# クラス。
///
/// 【なぜ SEEDScript ではなく純クラスなのか】
/// 文字送りは「表示先の Text コンポーネントが何であるか」に依存しない純粋なロジック
/// （表示済み文字列を組み立てるだけ）である。会話窓（DialogueWindow）と
/// チュートリアル窓（TutorialWindow）の両方から使い回せるよう、
/// エンジンのライフサイクルから切り離した部品にしてある（単一責任）。
/// 経過時間は呼び出し側が渡すため、ゲーム時間（DeltaTime）で動かすか
/// 実時間（UnscaledDeltaTime）で動かすかも呼び出し側が決められる。
///
/// 【1 文字の単位】
///  1. サロゲートペア・結合文字を途中で切らないよう「書記素クラスタ」で分割する。
///  2. さらに Text のインライン画像記法 [icon:名前] / [img:パス] は
///     「記法まるごとで 1 文字」として扱う。1 文字ずつ出すと
///     「[ic」のような壊れた記法が一瞬表示されてしまうため。
///  3. エスケープ（U+005C ＋ 開き括弧）も 2 文字まとめて 1 単位にする
///     （分断するとエスケープ記号だけが表示される瞬間ができるため）。
///
/// 【使い方】
/// <code>
/// var writer = new TypewriterText();
/// writer.Begin("こんにちは [icon:key_w] を押してね");
/// // 毎フレーム: 変化したときだけ Text へ反映する
/// if (writer.Advance(SEED.Time.UnscaledDeltaTime, 0.04f)) label.Content = writer.Shown;
/// // 送り入力で全文表示
/// if (confirmPressed) { writer.Complete(); label.Content = writer.Shown; }
/// </code>
/// </summary>
public sealed class TypewriterText
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>この値以下の表示間隔は「即時全文表示」とみなす（0 除算・無限ループ回避）。</summary>
    public const float MinCharInterval = 0.0001f;

    /// <summary>1 回の <see cref="Advance"/> で進められる最大単位数（極端な経過時間での暴走を防ぐ）。</summary>
    private const int MaxUnitsPerAdvance = 64;

    /// <summary>インライン記法の開始文字。</summary>
    private const char MarkupOpen = '[';

    /// <summary>インライン記法の終了文字。</summary>
    private const char MarkupClose = ']';

    /// <summary>
    /// エスケープ記号の文字コード（U+005C）。開き括弧の前に置くと「[」そのものを表す。
    /// 文字リテラルで書くとエスケープが重なって読みづらいのでコード値で定義する。
    /// </summary>
    private const char EscapeChar = (char)0x5C;

    /// <summary>エスケープ 1 個ぶんの文字数（エスケープ記号＋開き括弧）。</summary>
    private const int EscapeUnitLength = 2;

    /// <summary>インライン画像記法（アイコンセット参照）の接頭辞。</summary>
    private const string IconMarkupPrefix = "icon:";

    /// <summary>インライン画像記法（パス直接指定）の接頭辞。</summary>
    private const string ImageMarkupPrefix = "img:";

    // ─── 内部状態 ────────────────────────────────────────────

    /// <summary>表示対象を「見た目 1 文字」単位へ分解したもの。</summary>
    private readonly List<string> _units = new();

    /// <summary>現在までに表示した単位数。</summary>
    private int _shownCount;

    /// <summary>文字送りの端数時間（秒）。</summary>
    private float _timer;

    /// <summary>表示済み文字列の組み立てバッファ（毎フレームの文字列連結を避ける）。</summary>
    private readonly StringBuilder _builder = new();

    /// <summary>直近に組み立てた表示済み文字列（<see cref="Shown"/> の実体）。</summary>
    private string _shown = "";

    // ─── 公開プロパティ ──────────────────────────────────────

    /// <summary>現在までに表示すべき文字列（Text.Content へそのまま入れる）。</summary>
    public string Shown => _shown;

    /// <summary>全文を表示し切っているか（＝送り待ちの状態か）。</summary>
    public bool IsComplete => _shownCount >= _units.Count;

    /// <summary>表示対象の総単位数（デバッグ・進捗表示用）。</summary>
    public int TotalUnits => _units.Count;

    // ─── 公開メソッド ────────────────────────────────────────

    /// <summary>
    /// 新しい本文の文字送りを開始する（表示済みは 0 文字に戻る）。
    /// </summary>
    /// <param name="text">表示する本文（null・空文字なら「表示し切った状態」になる）。</param>
    public void Begin(string? text)
    {
        _units.Clear();
        SplitIntoUnits(text, _units);

        _shownCount = 0;
        _timer      = 0f;
        _builder.Clear();
        _shown = "";
    }

    /// <summary>
    /// 経過時間ぶんだけ文字送りを進める。
    /// </summary>
    /// <param name="deltaTime">前フレームからの経過秒（実時間で動かすなら UnscaledDeltaTime）。</param>
    /// <param name="charInterval">1 単位あたりの表示間隔（秒）。MinCharInterval 以下なら即時全文表示。</param>
    /// <returns>このフレームで <see cref="Shown"/> が変化したら true。</returns>
    public bool Advance(float deltaTime, float charInterval)
    {
        if (IsComplete) { return false; }

        // 間隔が実質 0 の設定は「文字送りをしない」意味なので、その場で全文表示する
        if (charInterval <= MinCharInterval) { return Complete(); }

        // 逆行・NaN のような不正な経過時間は無視する（タイマーを壊さない）
        if (!(deltaTime > 0f)) { return false; }

        _timer += deltaTime;

        // 端数時間から「今回出す単位数」を求める
        int advance = 0;
        while (_timer >= charInterval
               && advance < MaxUnitsPerAdvance
               && _shownCount + advance < _units.Count)
        {
            _timer -= charInterval;
            advance++;
        }
        if (advance <= 0) { return false; }

        for (int i = 0; i < advance; i++) { _builder.Append(_units[_shownCount + i]); }
        _shownCount += advance;
        _shown = _builder.ToString();
        return true;
    }

    /// <summary>
    /// 残りをすべて表示する（送り入力による全文表示に使う）。
    /// </summary>
    /// <returns><see cref="Shown"/> が変化したら true。</returns>
    public bool Complete()
    {
        if (IsComplete) { return false; }

        for (int i = _shownCount; i < _units.Count; i++) { _builder.Append(_units[i]); }
        _shownCount = _units.Count;
        _timer      = 0f;
        _shown      = _builder.ToString();
        return true;
    }

    // ─── 内部処理: 分割 ──────────────────────────────────────

    /// <summary>
    /// 本文を「見た目 1 文字」単位へ分解する【分割規則の唯一の実装】。
    ///
    /// インライン画像記法とエスケープはまとめて 1 単位にし、
    /// それ以外は書記素クラスタ単位で刻む。
    /// </summary>
    /// <param name="text">分解する本文。</param>
    /// <param name="into">分解結果の追加先（呼び出し前に空にしておくこと）。</param>
    private static void SplitIntoUnits(string? text, List<string> into)
    {
        if (string.IsNullOrEmpty(text)) { return; }

        int index = 0;
        while (index < text.Length)
        {
            // 1) エスケープは 2 文字で 1 単位（分断すると記号だけ見える瞬間ができる）
            if (text[index] == EscapeChar
                && index + 1 < text.Length
                && text[index + 1] == MarkupOpen)
            {
                into.Add(text.Substring(index, EscapeUnitLength));
                index += EscapeUnitLength;
                continue;
            }

            // 2) インライン画像記法は記法まるごとで 1 単位
            if (text[index] == MarkupOpen
                && TryMeasureImageMarkup(text, index, out int markupLength))
            {
                into.Add(text.Substring(index, markupLength));
                index += markupLength;
                continue;
            }

            // 3) それ以外は書記素クラスタ 1 個ぶんを 1 単位にする。
            //    先頭 1 クラスタの長さだけが欲しいので、残り部分について列挙器を作って
            //    最初の要素を取る（文字列全体を再列挙しないため、記法をまたいでも破綻しない）。
            string rest = text.Substring(index);
            var enumerator = StringInfo.GetTextElementEnumerator(rest);
            if (enumerator.MoveNext() && enumerator.Current is string element && element.Length > 0)
            {
                into.Add(element);
                index += element.Length;
            }
            else
            {
                // 列挙器が進まないことは通常あり得ないが、無限ループを避けるため 1 文字だけ進める
                into.Add(text.Substring(index, 1));
                index += 1;
            }
        }
    }

    /// <summary>
    /// 指定位置から始まるインライン画像記法の長さを測る。
    ///
    /// 判定は Text の実装に合わせる: 「[icon:」か「[img:」で始まり、
    /// 後ろで「]」が閉じているものだけを記法とみなす。
    /// それ以外（[0] や閉じない [icon:x）は通常の文字として扱われるため false を返す。
    /// </summary>
    /// <param name="text">本文全体。</param>
    /// <param name="start">開き括弧の位置。</param>
    /// <param name="length">記法全体の長さ（開き括弧から閉じ括弧まで）。</param>
    /// <returns>記法として成立していれば true。</returns>
    private static bool TryMeasureImageMarkup(string text, int start, out int length)
    {
        length = 0;

        int bodyStart = start + 1;
        if (!StartsWithAt(text, bodyStart, IconMarkupPrefix)
            && !StartsWithAt(text, bodyStart, ImageMarkupPrefix))
        {
            return false;
        }

        int close = text.IndexOf(MarkupClose, bodyStart);
        if (close < 0) { return false; }

        length = close - start + 1;
        return true;
    }

    /// <summary>
    /// 文字列の指定位置が、与えた接頭辞で始まっているか（範囲外は false）。
    /// </summary>
    /// <param name="text">検査する文字列。</param>
    /// <param name="index">検査開始位置。</param>
    /// <param name="prefix">接頭辞。</param>
    /// <returns>接頭辞で始まっていれば true。</returns>
    private static bool StartsWithAt(string text, int index, string prefix)
    {
        if (index < 0 || index + prefix.Length > text.Length) { return false; }
        return string.CompareOrdinal(text, index, prefix, 0, prefix.Length) == 0;
    }
}
