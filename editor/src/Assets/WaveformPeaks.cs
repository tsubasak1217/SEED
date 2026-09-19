// ============================================================
//  WaveformPeaks.cs — 音声波形サムネイルの「列ごとのピーク」計算（純ロジック）
//
//  【役割】
//  復号済みのサンプル列を受け取り、サムネイルの横 1 ピクセル（＝1 列）ごとに
//  最小値と最大値（ピーク）を求める。描画も復号もここでは行わない。
//
//  【なぜ平均ではなくピークなのか】
//  波形サムネイルは「どこで鳴っていて、どこが無音か」を一目で分かるための絵。
//  区間平均を取ると、振動する波は正負が打ち消し合ってほぼ 0 になり、
//  大音量の箇所まで平らに見えてしまう。区間の最小・最大を縦線で結べば、
//  音量の増減がそのまま帯の太さになる（Unity のインスペクタと同じ描き方）。
//
//  【なぜ逐次（ストリーミング）なのか】
//  数分の BGM は数千万サンプルになる。全部を配列へ載せてから間引くと
//  数百 MB を一時的に確保することになるため、読みながら列へ畳み込む。
//  使用メモリは列数ぶん（= サムネイル幅）だけで、ファイル長に依存しない。
//
//  【長さが分からなくても壊れないこと】
//  復号器が返す全長は形式によっては推定値で、実際の再生長とずれる
//  （可変ビットレートの MP3 など）。そのため
//    ・推定より長い → 列をつぶして解像度を半分にし、読み続ける
//    ・推定より短い → 埋まった列を横へ引き伸ばして幅いっぱいに描く
//  という両側の逃げ道を持たせてある。
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//  WPF 型・NAudio へ依存しない（復号は呼び出し側が行い、float を流し込む）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Assets;

/// <summary>
/// 波形サムネイルの 1 列ぶんのピーク（その区間の最小値と最大値）。
/// 値の範囲は復号器が返すサンプルと同じ（通常 -1.0〜+1.0）。
/// </summary>
/// <param name="Min">区間内の最小サンプル値。</param>
/// <param name="Max">区間内の最大サンプル値。</param>
public readonly record struct WaveformColumn(float Min, float Max)
{
    /// <summary>無音（振幅ゼロ）の列。</summary>
    public static WaveformColumn Silent => new(0f, 0f);

    /// <summary>
    /// この列の帯の高さ（最大と最小の差）。描画で縦線の長さに使う。
    ///
    /// <para>
    /// <b>これは「音の大きさ」ではない。</b> 1 列に 1 サンプルしか入らない
    /// （非常に短い音や、列数より短いファイル）ときは最小＝最大となり、
    /// 大きな音でもこの値は 0 になる。音の有無を判定したいときは
    /// <see cref="Peak"/> を使うこと。
    /// </para>
    /// </summary>
    public float Amplitude => Max - Min;

    /// <summary>
    /// この列の振れ幅の絶対値（中心線からの最大の隔たり）。
    /// 「鳴っているか」の判定はこちらで行う。
    /// </summary>
    public float Peak => Math.Max(Math.Abs(Min), Math.Abs(Max));
}

/// <summary>
/// サンプルを流し込みながら、列ごとのピークへ畳み込む器。
///
/// <para>
/// 使い方:
/// <code>
/// var acc = new WaveformPeakAccumulator(columnCount: 128, estimatedFrameCount: totalFrames);
/// while (読める) acc.AddInterleaved(buffer, readCount, channelCount);
/// WaveformColumn[] columns = acc.Build();
/// </code>
/// </para>
///
/// <para>
/// インスタンスは使い捨て（<see cref="Build"/> 後に追加しても結果は変わらない）。
/// スレッド安全ではない（1 本の復号ループが 1 個を占有する前提）。
/// </para>
/// </summary>
public sealed class WaveformPeakAccumulator
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>1 列に詰める最小のサンプル数（0 除算と無限ループの防止）。</summary>
    private const long MinSamplesPerColumn = 1;

    /// <summary>列をつぶすときに何列を 1 列へまとめるか（＝解像度を半分にする）。</summary>
    private const int MergeGroupSize = 2;

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>要求された列数（＝サムネイルの横ピクセル数）。</summary>
    private readonly int _columnCount;

    /// <summary>列ごとの最小値（<see cref="_filledColumns"/> 件目までが有効）。</summary>
    private readonly float[] _min;

    /// <summary>列ごとの最大値（<see cref="_filledColumns"/> 件目までが有効）。</summary>
    private readonly float[] _max;

    /// <summary>1 列へ詰めるサンプル数。推定より長い音が来るたびに倍へ広がる。</summary>
    private long _samplesPerColumn;

    /// <summary>今書いている列の番号（0 始まり）。</summary>
    private int _currentColumn;

    /// <summary>今書いている列へ既に詰めたサンプル数。</summary>
    private long _currentCount;

    /// <summary>今書いている列の暫定の最小値。</summary>
    private float _currentMin;

    /// <summary>今書いている列の暫定の最大値。</summary>
    private float _currentMax;

    /// <summary>
    /// 今書いている列に 1 つでも値が入っているか。
    /// サンプル数（<see cref="_currentCount"/>）では判定できない
    /// ＝解像度を落としたときに「確定済みの列を書きかけへ戻す」経路があり、
    /// そこではサンプル数が 0 のまま値だけを引き継ぐため。
    /// </summary>
    private bool _currentHasData;

    /// <summary>確定済みの列数（<see cref="_currentColumn"/> と同じだが意図を明示するため別名）。</summary>
    private int _filledColumns;

    // ── 生成 ─────────────────────────────────────────────────────

    /// <summary>
    /// 器を作る。
    /// </summary>
    /// <param name="columnCount">サムネイルの横ピクセル数（1 以上）。</param>
    /// <param name="estimatedFrameCount">
    /// 復号器が申告する総フレーム数（チャンネルを混ぜた後の長さ）。
    /// 0 以下・不明なら 1 サンプル 1 列から始めて、必要に応じて自動で粗くする。
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="columnCount"/> が 1 未満。</exception>
    public WaveformPeakAccumulator(int columnCount, long estimatedFrameCount)
    {
        if (columnCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(columnCount), columnCount, "波形の列数は 1 以上でなければならない。");
        }

        _columnCount = columnCount;
        _min = new float[columnCount];
        _max = new float[columnCount];

        // 推定長から「1 列あたり何サンプルか」を決める。
        // 切り上げるのは、切り捨てると必ず列があふれて初回から潰し直しになるため。
        _samplesPerColumn = estimatedFrameCount > 0
            ? Math.Max(MinSamplesPerColumn, CeilDiv(estimatedFrameCount, columnCount))
            : MinSamplesPerColumn;

        ResetCurrent();
    }

    // ── 追加 ─────────────────────────────────────────────────────

    /// <summary>
    /// モノラル 1 サンプルを追加する。
    /// </summary>
    /// <param name="sample">サンプル値（通常 -1.0〜+1.0）。</param>
    public void Add(float sample)
    {
        // NaN・無限大は復号の事故で紛れ込むことがある。
        // そのまま min/max に混ぜると以降の比較がすべて壊れるので無音として捨てる。
        if (!float.IsFinite(sample)) sample = 0f;

        if (sample < _currentMin) _currentMin = sample;
        if (sample > _currentMax) _currentMax = sample;
        _currentCount++;
        _currentHasData = true;

        if (_currentCount >= _samplesPerColumn) CommitCurrent();
    }

    /// <summary>
    /// インターリーブされたバッファを追加する（チャンネルは平均して 1 本に混ぜる）。
    /// </summary>
    /// <param name="buffer">復号済みサンプル（チャンネルが交互に並ぶ）。</param>
    /// <param name="count">この呼び出しで使う要素数（<paramref name="buffer"/> の先頭から）。</param>
    /// <param name="channelCount">チャンネル数（1 以上。0 以下は 1 として扱う）。</param>
    public void AddInterleaved(float[] buffer, int count, int channelCount)
    {
        if (buffer is null) return;
        if (count <= 0) return;
        if (count > buffer.Length) count = buffer.Length;
        if (channelCount < 1) channelCount = 1;

        if (channelCount == 1)
        {
            for (int i = 0; i < count; i++) Add(buffer[i]);
            return;
        }

        // 1 フレーム（全チャンネル）ぶんが揃っている範囲だけを処理する。
        // 端数（バッファ境界で切れたフレーム）は捨てる。1 フレームは数十マイクロ秒で、
        // サムネイルの見た目には現れない一方、持ち越しの状態を増やすと不具合の種になる。
        int frames = count / channelCount;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channelCount;
            float sum = 0f;
            for (int c = 0; c < channelCount; c++)
            {
                float v = buffer[baseIndex + c];
                if (float.IsFinite(v)) sum += v;
            }
            Add(sum / channelCount);
        }
    }

    // ── 確定 ─────────────────────────────────────────────────────

    /// <summary>
    /// 列の配列を作って返す（常に要求した列数ちょうど）。
    ///
    /// <para>
    /// サンプルが列数より少なかった場合は、埋まった列を横へ引き伸ばす
    /// （最近傍で拡大）。サムネイルは「ファイル全体を幅いっぱいに描く」絵なので、
    /// 短い音でも右側が空白になるより引き伸ばしたほうが実態に合う。
    /// </para>
    /// </summary>
    /// <returns>列ごとのピーク（長さは生成時の columnCount）。</returns>
    public WaveformColumn[] Build()
    {
        // 書きかけの列も 1 列として確定させる（末尾のサンプルを捨てないため）。
        int filled = _filledColumns;
        var tailMin = _currentMin;
        var tailMax = _currentMax;
        bool hasTail = _currentHasData && filled < _columnCount;
        if (hasTail) filled++;

        var result = new WaveformColumn[_columnCount];

        // 1 サンプルも来なかった＝無音（または復号失敗）。全列を 0 にする。
        if (filled == 0)
        {
            for (int i = 0; i < _columnCount; i++) result[i] = WaveformColumn.Silent;
            return result;
        }

        for (int i = 0; i < _columnCount; i++)
        {
            // 最近傍で filled 列 → _columnCount 列へ拡大する。
            // filled == _columnCount のときは src == i となり、引き伸ばしは起きない。
            int src = (int)((long)i * filled / _columnCount);
            if (src >= filled) src = filled - 1;

            result[i] = (hasTail && src == filled - 1)
                ? new WaveformColumn(tailMin, tailMax)
                : new WaveformColumn(_min[src], _max[src]);
        }
        return result;
    }

    // ── 内部 ─────────────────────────────────────────────────────

    /// <summary>書きかけの列を確定し、次の列へ進む。列があふれたら解像度を半分にする。</summary>
    private void CommitCurrent()
    {
        _min[_currentColumn] = _currentMin;
        _max[_currentColumn] = _currentMax;
        _currentColumn++;
        _filledColumns = _currentColumn;
        ResetCurrent();

        if (_currentColumn >= _columnCount) HalveResolution();
    }

    /// <summary>
    /// 列を 2 本ずつ 1 本へまとめ、1 列あたりのサンプル数を倍にする。
    ///
    /// <para>
    /// 推定長より音が長かったときの逃げ道。以降は粗い解像度で読み続けるので、
    /// メモリも走査回数も増えない（つぶす操作は列数に比例する軽い処理で、
    /// 長さが 2 倍になるごとに 1 回しか起きない）。
    /// </para>
    /// </summary>
    private void HalveResolution()
    {
        int written = 0;
        for (int src = 0; src < _columnCount; src += MergeGroupSize)
        {
            float mn = _min[src];
            float mx = _max[src];

            // 列数が奇数のときは最後のグループが 1 列だけになる。
            for (int k = 1; k < MergeGroupSize && src + k < _columnCount; k++)
            {
                if (_min[src + k] < mn) mn = _min[src + k];
                if (_max[src + k] > mx) mx = _max[src + k];
            }

            _min[written] = mn;
            _max[written] = mx;
            written++;
        }

        _currentColumn    = written;
        _filledColumns    = written;
        _samplesPerColumn *= MergeGroupSize;
        ResetCurrent();

        // 列数が 1 のときは、まとめても列が 1 本のままで空きが作れない。
        // 唯一の列を「書きかけ」へ戻し、以降はそこへ吸収し続ける
        // （閾値が倍になるだけで、最小・最大は引き継ぐので情報は失われない）。
        if (_currentColumn >= _columnCount)
        {
            _currentColumn  = _columnCount - 1;
            _filledColumns  = _currentColumn;
            _currentMin     = _min[_currentColumn];
            _currentMax     = _max[_currentColumn];
            _currentHasData = true;
        }
    }

    /// <summary>書きかけの列の集計を初期化する。</summary>
    private void ResetCurrent()
    {
        _currentCount   = 0;
        _currentMin     = float.MaxValue;
        _currentMax     = float.MinValue;
        _currentHasData = false;
    }

    /// <summary>切り上げ除算（両方とも正であることが前提）。</summary>
    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;
}

/// <summary>
/// 波形ピーク計算の入口（テストと呼び出し側の便宜のための薄い包み）。
/// </summary>
public static class WaveformPeaks
{
    /// <summary>
    /// モノラルのサンプル列から、列ごとのピークを一気に求める。
    /// </summary>
    /// <param name="samples">サンプル列（列挙は 1 度だけ行う）。</param>
    /// <param name="columnCount">列数（1 以上）。</param>
    /// <param name="estimatedFrameCount">
    /// 総フレーム数の推定値。0 以下なら不明として扱う
    /// （<see cref="WaveformPeakAccumulator"/> が自動で解像度を合わせる）。
    /// </param>
    /// <returns>列ごとのピーク（長さは <paramref name="columnCount"/>）。</returns>
    public static WaveformColumn[] Compute(
        IEnumerable<float> samples, int columnCount, long estimatedFrameCount = 0)
    {
        var acc = new WaveformPeakAccumulator(columnCount, estimatedFrameCount);
        if (samples is not null)
        {
            foreach (var s in samples) acc.Add(s);
        }
        return acc.Build();
    }

    /// <summary>
    /// 列の集合が実質的に無音か（すべての列の振れ幅が閾値以下か）を返す。
    /// 「復号はできたが中身が無音」を「復号に失敗した」と取り違えないための判定に使う。
    ///
    /// <para>
    /// 判定に使うのは <see cref="WaveformColumn.Peak"/>（中心線からの隔たり）であって
    /// <see cref="WaveformColumn.Amplitude"/>（帯の高さ）ではない。
    /// 列に 1 サンプルしか入らない短い音では最小＝最大となり、
    /// 大きな音でも帯の高さが 0 になってしまうため。
    /// </para>
    /// </summary>
    /// <param name="columns">列ごとのピーク。</param>
    /// <param name="threshold">無音とみなす振れ幅の上限。</param>
    public static bool IsSilent(IReadOnlyList<WaveformColumn>? columns, float threshold)
    {
        if (columns is null || columns.Count == 0) return true;
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Peak > threshold) return false;
        }
        return true;
    }
}
