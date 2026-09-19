using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.ScriptEditor.Navigation;

/// <summary>
/// ナビゲーション履歴に積む「1 点」の位置。
///
/// 開いているドキュメントでは AvalonEdit の TextAnchor を裏に持たせ、
/// 上の行が編集されても行がずれないようにする（＝<see cref="Line"/> が都度変わる）。
/// 閉じているファイルでは行・桁を固定値として持つ。
/// この境目を隠すためのインターフェースで、履歴本体を WPF / AvalonEdit から切り離す。
/// </summary>
public interface INavPosition
{
    /// <summary>ファイルの絶対パス。</summary>
    string FilePath { get; }

    /// <summary>行番号（1 起点）。アンカー実装では常に最新の行を返す。</summary>
    int Line { get; }

    /// <summary>桁番号（1 起点）。</summary>
    int Column { get; }
}

/// <summary>
/// 行・桁を固定値で持つ位置（閉じているファイル・タブを閉じた後の履歴点）。
/// </summary>
/// <param name="FilePath">ファイルの絶対パス。</param>
/// <param name="Line">行番号（1 起点）。</param>
/// <param name="Column">桁番号（1 起点）。</param>
public sealed record NavPosition(string FilePath, int Line, int Column) : INavPosition;

/// <summary>
/// スクリプトエディタの「戻る／進む」履歴（Visual Studio の Navigate Backward/Forward 相当）。
///
/// 【設計】
/// - 「戻る」側と「進む」側の 2 本のスタックで持つ。現在位置はスタックに入れず、
///   戻る／進むを実行した瞬間に反対側のスタックへ積む。
///   これにより「移動する直前の位置」と「移動した先」の両方を辿れる
///   （移動先＝現在位置なので、戻った瞬間に「進む」側へ乗る）。
/// - 近い点の連続は 1 点にまとめる（同じファイルで <see cref="MergeLineDistance"/> 行以内なら置き換え）。
/// - 上限 <see cref="MaxEntries"/> 件を超えたら古いものから捨てる。
/// - 「消えたファイル」の点は移動時に読み飛ばす（判定は呼び出し側から渡す述語に委ねる）。
///
/// WPF / AvalonEdit へ依存しない純粋クラス。位置の解決（アンカー ↔ 行桁）は
/// パネル側のアダプタが行う。
/// </summary>
public sealed class ScriptNavigationHistory
{
    /// <summary>
    /// 同一ファイル内でこの行数以内にある 2 点は「同じ場所」とみなす。
    /// 記録時は 1 点へまとめ、戻る／進む時は「いま居る場所」として読み飛ばす。
    /// Visual Studio の挙動に倣って 10 行。
    /// </summary>
    public const int MergeLineDistance = 10;

    /// <summary>
    /// キャレットがこの行数以上離れた位置へ飛んだら「ジャンプ」とみなして記録する。
    /// 1 行ずつのカーソル移動や普通の入力で履歴が埋まらないようにするための閾値。
    /// </summary>
    public const int JumpLineThreshold = 10;

    /// <summary>片側のスタックに積める上限件数。超えたら古いものから捨てる。</summary>
    public const int MaxEntries = 50;

    /// <summary>「戻る」で辿れる点（末尾が直前の位置）。</summary>
    private readonly List<INavPosition> _back = new();

    /// <summary>「進む」で辿れる点（末尾が直後の位置）。</summary>
    private readonly List<INavPosition> _forward = new();

    /// <summary>戻れる点があるか。</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>進める点があるか。</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>「戻る」側に積まれている件数（テスト・診断用）。</summary>
    public int BackCount => _back.Count;

    /// <summary>「進む」側に積まれている件数（テスト・診断用）。</summary>
    public int ForwardCount => _forward.Count;

    /// <summary>
    /// 履歴に 1 点を記録する。
    ///
    /// 新しい移動が起きたということなので「進む」側は捨てる。
    /// 直前の点と同じ場所なら積まずに置き換える（近い点の連続を 1 点にまとめる）。
    /// </summary>
    /// <param name="position">記録する位置。</param>
    public void Record(INavPosition position)
    {
        if (position is null) return;

        // 戻った状態から新しい移動をしたら「進む」側は無効になる
        _forward.Clear();

        // 直前の点と同じ場所なら、最新の位置で置き換えるだけにする
        if (_back.Count > 0 && IsSamePlace(_back[^1], position))
        {
            _back[^1] = position;
            return;
        }

        _back.Add(position);
        Trim(_back);
    }

    /// <summary>
    /// 1 つ前の点へ戻る。現在位置は「進む」側へ積む。
    ///
    /// 消えたファイルの点と、いま居る場所と同じ点は読み飛ばして次を探す
    /// （飛ばした点は履歴から捨てる）。戻り先が 1 つも無ければ何も変更せず null を返す。
    /// </summary>
    /// <param name="current">
    /// 現在位置（戻れた場合だけ「進む」側へ積まれる）。
    /// タブを全て閉じている場合は null を渡す。その場合は積むものが無いので
    /// 「進む」側は増えず、「いま居る場所」の読み飛ばしも行わない。
    /// </param>
    /// <param name="isAvailable">その点へ移動できるか（ファイルが存在するか）を判定する述語。</param>
    /// <returns>移動先の点。戻れない場合は null。</returns>
    public INavPosition? GoBack(INavPosition? current, Func<INavPosition, bool> isAvailable)
        => Step(_back, _forward, current, isAvailable);

    /// <summary>
    /// 1 つ先の点へ進む。現在位置は「戻る」側へ積む。
    /// 読み飛ばしの規則は <see cref="GoBack"/> と同じ。
    /// </summary>
    /// <param name="current">現在位置（進めた場合だけ「戻る」側へ積まれる）。タブが無ければ null。</param>
    /// <param name="isAvailable">その点へ移動できるかを判定する述語。</param>
    /// <returns>移動先の点。進めない場合は null。</returns>
    public INavPosition? GoForward(INavPosition? current, Func<INavPosition, bool> isAvailable)
        => Step(_forward, _back, current, isAvailable);

    /// <summary>
    /// 保持している全ての点を変換関数で置き換える。
    ///
    /// タブを閉じるとき・本文を丸ごと読み直すときに、そのファイルのアンカーを
    /// 行・桁の固定値へ落とし込むために使う（アンカーは本文の総入れ替えで壊れるため）。
    /// </summary>
    /// <param name="convert">各点を新しい点へ変換する関数（そのままでよければ引数を返す）。</param>
    public void Replace(Func<INavPosition, INavPosition> convert)
    {
        for (int i = 0; i < _back.Count; i++)    _back[i]    = convert(_back[i]);
        for (int i = 0; i < _forward.Count; i++) _forward[i] = convert(_forward[i]);
    }

    /// <summary>履歴を空にする（プロジェクトの切り替えなど、前の文脈が無意味になったとき）。</summary>
    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    /// <summary>
    /// 2 点が「同じ場所」か（同じファイルで、行の差が <see cref="MergeLineDistance"/> 以内）。
    /// </summary>
    /// <param name="a">比較する点。</param>
    /// <param name="b">比較する点。</param>
    /// <returns>同じ場所とみなせるなら true。</returns>
    public static bool IsSamePlace(INavPosition a, INavPosition b)
        => IsSameFile(a, b) && Math.Abs(a.Line - b.Line) <= MergeLineDistance;

    /// <summary>同じファイルを指す点か（Windows のファイルシステムに合わせて大文字小文字を無視）。</summary>
    private static bool IsSameFile(INavPosition a, INavPosition b)
        => string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 片側のスタックから移動先を 1 点取り出し、現在位置を反対側へ積む共通処理。
    /// 「戻る」と「進む」は取り出す側と積む側が入れ替わるだけなので 1 本にまとめてある。
    /// </summary>
    /// <param name="from">移動先を取り出すスタック。</param>
    /// <param name="to">現在位置を積むスタック。</param>
    /// <param name="current">現在位置（タブが 1 つも開いていなければ null）。</param>
    /// <param name="isAvailable">その点へ移動できるかを判定する述語。</param>
    /// <returns>移動先の点。見つからなければ null。</returns>
    private static INavPosition? Step(
        List<INavPosition>        from,
        List<INavPosition>        to,
        INavPosition?             current,
        Func<INavPosition, bool>  isAvailable)
    {
        while (from.Count > 0)
        {
            var candidate = from[^1];
            from.RemoveAt(from.Count - 1);

            // ファイルが消えている点は捨てて次を探す
            // （「ファイルが見つかりません」タブを履歴移動で量産しないため）
            if (!isAvailable(candidate)) continue;

            // いま居る場所と同じなら、押しても動かないので次を探す
            if (current is not null && IsSamePlace(candidate, current)) continue;

            // 現在位置が無い（全タブを閉じた直後）ときは積むものが無い
            if (current is not null)
            {
                to.Add(current);
                Trim(to);
            }
            return candidate;
        }
        return null;
    }

    /// <summary>上限件数を超えた分を古い方（先頭）から捨てる。</summary>
    private static void Trim(List<INavPosition> stack)
    {
        while (stack.Count > MaxEntries) stack.RemoveAt(0);
    }
}
