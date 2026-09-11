using System.Collections.Generic;

/// <summary>
/// ビートパターンのテキストファイルを読み込み、魚のレベルに合う行を抽選して返す
/// 【譜面データの入口】。
///
/// <b>役割</b>
/// 1. <see cref="SEED.Assets"/> でテキストを読み、<see cref="BeatPatternParser"/> に解析させる
/// 2. エラー行は捨て、行番号つきのメッセージをログへ出す（起動時に 1 度）
/// 3. 魚のレベルに該当する行から 1 つ抽選する（<see cref="Pick"/>）
/// 4. ファイルの更新時刻を一定間隔で確認し、変わっていれば読み直す（ホットリロード）
///
/// <b>単一責任</b>: 「どの行を使うか」だけを決める。記法の解釈は
/// <see cref="BeatPatternParser"/>、実時刻への割り付けは釣りバトル側の責務。
///
/// <b>寿命</b>: 釣りバトル（FishingFight）が 1 つ持ち、戦闘をまたいで使い回す。
/// 読み込みは <see cref="Configure"/> と <see cref="Reload"/> のときだけ走るので、
/// 毎フレーム <see cref="PollHotReload"/> を呼んでも安い（間隔内は即 return）。
/// </summary>
public sealed class BeatPatternLibrary
{
    /// <summary>ファイル更新の確認間隔（秒）。ホットリロードの反応の速さ。</summary>
    private const float CheckIntervalSeconds = 1f;

    /// <summary>ログに並べるエラー行数の上限（誤記だらけのときにログを埋め尽くさないため）。</summary>
    private const int MaxLoggedErrors = 10;

    /// <summary>まだ 1 度も更新確認をしていないことを表す時刻。</summary>
    private const float NoCheckTime = float.MinValue;

    /// <summary>読み込み元のアセットパス（未設定なら読み込みを行わない）。</summary>
    private string assetPath = "";

    /// <summary>1 行に期待する小節数（＝戦闘サイクルの長さ）。</summary>
    private int expectedBars = 1;

    /// <summary>読み込めた全パターン（ファイル内の順序を保つ）。</summary>
    private readonly List<BeatPattern> patterns = new();

    /// <summary>直近の読み込みで出たエラー（行番号つきメッセージ）。</summary>
    private readonly List<string> errors = new();

    /// <summary>抽選の候補を毎回組み直すための作業用リスト（確保を使い回す）。</summary>
    private readonly List<BeatPattern> candidates = new();

    /// <summary>読み込んだ時点のファイル更新時刻（ホットリロードの比較対象）。</summary>
    private long loadedStamp = SEED.Assets.UnknownModifiedTime;

    /// <summary>次に更新確認を行う時刻（<see cref="PollHotReload"/> に渡される時間軸）。</summary>
    private float nextCheckTime = NoCheckTime;

    /// <summary>1 度でも読み込みを行ったか（<see cref="Configure"/> の空振り判定に使う）。</summary>
    private bool loaded = false;

    /// <summary>読み込めたパターンの数。</summary>
    public int Count => patterns.Count;

    /// <summary>直近の読み込みで出たエラーの数（0 なら全行が有効）。</summary>
    public int ErrorCount => errors.Count;

    /// <summary>いま読み込み元にしているアセットパス。</summary>
    public string AssetPath => assetPath;

    /// <summary>
    /// 読み込み元と想定小節数を設定する【設定変更の唯一の入口】。
    /// 値が変わったときだけ読み直すので、毎フレーム呼んでも問題ない。
    /// </summary>
    /// <param name="path">ビートパターンのアセットパス（assets://…）。</param>
    /// <param name="bars">1 行に期待する小節数（戦闘サイクルの長さ）。</param>
    public void Configure(string path, int bars)
    {
        string next = path ?? "";

        // 設定が同じで、既に 1 度読み込んであるなら何もしない（毎フレーム呼んでも安い）
        if (loaded && next == assetPath && bars == expectedBars) { return; }

        assetPath = next;
        expectedBars = bars;
        Reload();
    }

    /// <summary>
    /// ファイルを読み直す【読み込みの唯一の出口】。
    /// 解析エラーはここでまとめてログに出す（1 回の読み込みにつき 1 度だけ）。
    /// </summary>
    public void Reload()
    {
        patterns.Clear();
        errors.Clear();
        loaded = true;
        loadedStamp = SEED.Assets.GetModifiedTime(assetPath);

        if (string.IsNullOrEmpty(assetPath))
        {
            SEED.Debug.LogWarning("[BeatPattern] ビートパターンのファイルパスが未設定です");
            return;
        }

        if (!SEED.Assets.TryReadText(assetPath, out string text))
        {
            SEED.Debug.LogWarning($"[BeatPattern] 読み込めませんでした: {assetPath}");
            return;
        }

        BeatPatternParser.ParseText(text, expectedBars, patterns, errors);

        for (int i = 0; i < errors.Count && i < MaxLoggedErrors; i++)
        {
            SEED.Debug.LogWarning($"[BeatPattern] {assetPath} {errors[i]}");
        }
        if (errors.Count > MaxLoggedErrors)
        {
            SEED.Debug.LogWarning($"[BeatPattern] ほか {errors.Count - MaxLoggedErrors} 件のエラーがあります");
        }

        SEED.Debug.Log($"[BeatPattern] 読み込み完了: {patterns.Count} 行 有効 / {errors.Count} 行 無効"
                     + $"（想定 {expectedBars} 小節・{assetPath}）");
    }

    /// <summary>
    /// ファイルが更新されていれば読み直す【ホットリロードの唯一の入口】。
    ///
    /// 確認は <see cref="CheckIntervalSeconds"/> 秒に 1 度だけ行う。
    /// 読み直した結果は<b>次に <see cref="Pick"/> を呼んだとき</b>から効くので、
    /// 進行中の戦闘の譜面が途中で入れ替わることはない。
    /// </summary>
    /// <param name="now">現在時刻（秒。単調増加ならどの時間軸でもよい）。</param>
    public void PollHotReload(float now)
    {
        if (string.IsNullOrEmpty(assetPath)) { return; }
        if (nextCheckTime > NoCheckTime && now < nextCheckTime) { return; }

        nextCheckTime = now + CheckIntervalSeconds;

        long stamp = SEED.Assets.GetModifiedTime(assetPath);
        if (stamp == loadedStamp) { return; }

        SEED.Debug.Log($"[BeatPattern] ファイルの更新を検知したので読み直します: {assetPath}");
        Reload();
    }

    /// <summary>
    /// 指定レベルの魚に使うパターンを 1 つ抽選する【抽選の唯一の出口】。
    ///
    /// 1. そのレベルに該当する行から抽選する
    /// 2. 1 行も無ければ「全レベル対象の行」から抽選する
    /// 3. それも無ければ null（呼び出し側がフォールバックを作る）
    /// </summary>
    /// <param name="level">魚のレベル（1 始まり。0 は未確定）。</param>
    /// <returns>抽選したパターン（候補が無ければ null）。</returns>
    public BeatPattern? Pick(int level)
    {
        // 1. レベルに該当する行
        candidates.Clear();
        foreach (var pattern in patterns)
        {
            if (pattern.Levels.Contains(level)) { candidates.Add(pattern); }
        }

        // 2. 該当が無ければ全レベル対象の行へ広げる
        if (candidates.Count == 0)
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Levels.IsAny) { candidates.Add(pattern); }
            }
        }

        if (candidates.Count == 0) { return null; }

        return candidates[SEED.Random.Range(0, candidates.Count)];
    }
}