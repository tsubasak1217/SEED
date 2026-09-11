using System.Collections.Generic;

/// <summary>
/// ビートパターン 1 行を解析した結果（＝<b>戦闘サイクル 1 周ぶんの打点の並び</b>）。
///
/// 打点は「小節を 1.0 としたときの、行の先頭からの位置」で持つ
/// （例: 4 拍子で 2 拍目の頭なら 0.25、2 小節目の頭なら 1.0）。
/// 固定グリッドの添字ではないので、1/4・1/6・1/8 が混ざっても表現できる。
/// 実時刻への変換は「フェーズ開始時刻 ＋ 位置 × 1 小節の秒数」だけで済む。
///
/// <b>不変</b>: 生成後は書き換えない。抽選で同じインスタンスが何度使われてもよい。
///
/// <b>単一責任</b>: 解析結果の入れ物。解析は <see cref="BeatPatternParser"/>、
/// 読み込みと抽選は <see cref="BeatPatternLibrary"/> の責務。
/// </summary>
public sealed class BeatPattern
{
    /// <summary>
    /// 打点の位置（小節単位・行の先頭からの昇順）。
    /// 1.0 ＝ 1 小節ぶん進んだ位置。値の範囲は 0 以上 <see cref="Bars"/> 未満。
    /// </summary>
    public IReadOnlyList<double> TriggerPositions { get; }

    /// <summary>この行の長さ（小節数。必ず 1 以上の整数）。</summary>
    public int Bars { get; }

    /// <summary>この行が対象にする魚のレベル帯。</summary>
    public LevelRange Levels { get; }

    /// <summary>元になったファイルの行番号（1 始まり。ログ用）。</summary>
    public int LineNumber { get; }

    /// <summary>元の行（コメントを除いた本文。ログ用）。</summary>
    public string SourceLine { get; }

    /// <summary>
    /// 解析結果からパターンを作る【生成の唯一の入口は <see cref="BeatPatternParser"/>】。
    /// </summary>
    /// <param name="triggerPositions">打点の位置（小節単位・昇順）。</param>
    /// <param name="bars">行の長さ（小節数）。</param>
    /// <param name="levels">対象のレベル帯。</param>
    /// <param name="lineNumber">元の行番号（1 始まり）。</param>
    /// <param name="sourceLine">元の行（ログ用）。</param>
    public BeatPattern(IReadOnlyList<double> triggerPositions, int bars, LevelRange levels,
                       int lineNumber, string sourceLine)
    {
        TriggerPositions = triggerPositions;
        Bars = bars;
        Levels = levels;
        LineNumber = lineNumber;
        SourceLine = sourceLine;
    }

    /// <summary>打点の数（＝このサイクルで叩く回数）。</summary>
    public int TriggerCount => TriggerPositions.Count;

    /// <summary>ログ用の要約（行番号・小節数・打点数）。</summary>
    public override string ToString()
        => $"L{LineNumber}: {Bars}小節 / 打点{TriggerCount}個";
}