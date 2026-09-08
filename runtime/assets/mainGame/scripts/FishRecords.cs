// ============================================================================
//  FishRecords.cs
//  釣果（魚種ごとのベストサイズ・ベストランク・釣った数）の読み書き。
// ============================================================================

/// <summary>
/// 魚種ごとの釣果記録を <see cref="SEED.SaveData"/> と読み書きする静的ヘルパー
/// 【釣果セーブキーの唯一の置き場】。
///
/// 【責務】
/// 「どのキーへ、どの型で保存するか」だけを知る。演出も UI も知らない
/// （記録するのは <c>CatchPresenter</c>、読むのは <c>Zukan</c>）。
///
/// 【キー設計】
/// 魚種の識別子には <c>Fish.DisplayName</c>（＝prefab の「表示名」）をそのまま使う。
/// <c>FishCatalog.Entries[].displayName</c> と同じ文字列なので、図鑑側は
/// カタログの表示名でそのまま引ける（ローマ字のアクタ名を経由しなくてよい）。
///
/// | キー                        | 型     | 内容                                   |
/// |-----------------------------|--------|----------------------------------------|
/// | <c>best_size:&lt;表示名&gt;</c> | float  | その魚種で釣った最大サイズ（表示単位）  |
/// | <c>best_rank:&lt;表示名&gt;</c> | string | 上のベストを釣ったときのサイズランク    |
/// | <c>catch_count:&lt;表示名&gt;</c>| int   | その魚種を釣り上げた累計回数            |
///
/// 【「釣った」の判定】
/// 釣った数が 1 以上なら図鑑に載る（<see cref="IsCaught"/>）。
/// ベストサイズ 0 を「未捕獲」とみなすとサイズ 0 の魚が出せなくなるため、
/// 判定は必ず回数側で行う。
///
/// 【互換性】
/// <c>best_size:</c> は既存キー（従来 <c>CatchPresenter</c> が直接書いていたもの）を
/// そのまま引き継ぐ。<c>best_rank:</c> と <c>catch_count:</c> は新設なので、
/// 本機能より前のセーブデータでは既定値（空文字 / 0）になる。
/// </summary>
public static class FishRecords
{
    // ─── 定数（マジックストリング禁止）───────────────────────

    /// <summary>ベストサイズの保存キーの接頭辞。</summary>
    public const string BestSizeKeyPrefix = "best_size:";

    /// <summary>ベストランクの保存キーの接頭辞。</summary>
    public const string BestRankKeyPrefix = "best_rank:";

    /// <summary>釣った数の保存キーの接頭辞。</summary>
    public const string CatchCountKeyPrefix = "catch_count:";

    /// <summary>未記録のベストサイズ（＝まだ 1 匹も釣っていないときの値）。</summary>
    private const float NoBestSize = 0f;

    /// <summary>未記録のベストランク（＝まだ 1 匹も釣っていないときの値）。</summary>
    private const string NoBestRank = "";

    /// <summary>未記録の釣った数。</summary>
    private const int NoCatchCount = 0;

    /// <summary>「釣った」とみなす最小の釣獲回数。</summary>
    private const int CaughtThreshold = 1;

    /// <summary>1 回の釣獲で増やす回数。</summary>
    private const int CatchIncrement = 1;

    // ─── 読み取り ────────────────────────────────────────────

    /// <summary>その魚種を釣り上げた累計回数。未捕獲なら 0。</summary>
    /// <param name="displayName">魚の表示名（<c>Fish.DisplayName</c>）。</param>
    public static int CatchCount(string displayName)
        => IsUsableName(displayName)
            ? SEED.SaveData.GetInt(CatchCountKeyPrefix + displayName, NoCatchCount)
            : NoCatchCount;

    /// <summary>その魚種のベストサイズ（表示単位）。未捕獲なら 0。</summary>
    /// <param name="displayName">魚の表示名。</param>
    public static float BestSize(string displayName)
        => IsUsableName(displayName)
            ? SEED.SaveData.GetFloat(BestSizeKeyPrefix + displayName, NoBestSize)
            : NoBestSize;

    /// <summary>
    /// ベストサイズを記録したときのサイズランク（"S" / "A" / "B" / "C"）。
    /// 未捕獲、または本機能より前のセーブデータでは空文字を返す。
    /// </summary>
    /// <param name="displayName">魚の表示名。</param>
    public static string BestRank(string displayName)
        => IsUsableName(displayName)
            ? SEED.SaveData.GetString(BestRankKeyPrefix + displayName, NoBestRank)
            : NoBestRank;

    /// <summary>その魚種を 1 匹以上釣っているか（図鑑に載るか）。</summary>
    /// <param name="displayName">魚の表示名。</param>
    public static bool IsCaught(string displayName)
        => CatchCount(displayName) >= CaughtThreshold;

    // ─── 書き込み ────────────────────────────────────────────

    /// <summary>
    /// 1 匹ぶんの釣果を記録した結果【釣果 UI が「何を出すか」を決めるための唯一の返り値】。
    ///
    /// <see cref="FishRecords.RecordCatch"/> は「釣った数を増やす」「ベストを更新する」の
    /// 2 つを同時に行うため、呼び出し側（釣果パネル）が欲しい情報は 1 つの bool では
    /// 足りない（初捕獲＝図鑑登録の演出を出すか／ベスト更新＝New Record を出すか／
    /// 更新前のベストは幾つだったか、の 3 つ）。値を足すたびに関数を増やさずに済むよう、
    /// 結果はこの構造体 1 つにまとめて返す。
    /// </summary>
    public readonly struct CatchRecordResult
    {
        /// <summary>
        /// この 1 匹が<b>その魚種の初捕獲</b>か（＝記録前の釣った数が 0 だったか）。
        /// true なら図鑑へ新規登録されたことになるので、釣果パネルは
        /// 「図鑑に登録されました」の演出を追加で出す。
        /// </summary>
        public readonly bool FirstCatch;

        /// <summary>ベストサイズを更新したか（＝新記録か）。</summary>
        public readonly bool NewBest;

        /// <summary>
        /// <b>この 1 匹を記録する前の</b>ベストサイズ（表示単位）。未捕獲なら 0。
        /// 釣果パネルの「自己ベスト」行は<b>更新後</b>の値を出したいことが多いが、
        /// 「前回までのベスト」を並べて見せる演出もできるよう、更新前の値を返す
        /// （更新後の値は <see cref="FishRecords.BestSize"/> で読める）。
        /// </summary>
        public readonly float PreviousBest;

        /// <summary>全項目を指定して作る（生成はこのクラスの中だけ）。</summary>
        /// <param name="firstCatch">初捕獲か。</param>
        /// <param name="newBest">ベスト更新か。</param>
        /// <param name="previousBest">記録前のベストサイズ。</param>
        public CatchRecordResult(bool firstCatch, bool newBest, float previousBest)
        {
            FirstCatch = firstCatch;
            NewBest = newBest;
            PreviousBest = previousBest;
        }
    }

    /// <summary>
    /// 1 匹ぶんの釣果を記録する【釣果の書き込みの唯一の入口】。
    ///
    /// 釣った数は必ず 1 増える。ベストサイズを更新したときだけ、
    /// その個体のサイズランクをベストランクとして併せて書き換える
    /// （同一魚種なら基準サイズが同じでサイズとランクは単調に対応するため、
    /// 　ベストサイズの個体のランク＝その魚種の最高ランクになる）。
    ///
    /// 書き込み後は必ず <see cref="SEED.SaveData.Save"/> を呼ぶ
    /// （釣り上げは進行の区切りで、ここで落ちても記録を失わせない）。
    /// </summary>
    /// <param name="displayName">魚の表示名。空白のみなら何もしない。</param>
    /// <param name="displaySize">釣った個体の表示サイズ。</param>
    /// <param name="sizeRank">釣った個体のサイズランク（<c>Fish.SizeRank</c>）。</param>
    /// <returns>
    /// 初捕獲か・ベスト更新か・更新前のベストサイズ（<see cref="CatchRecordResult"/>）。
    /// 表示名が使えないときは全項目が既定値（false / false / 0）の結果を返す。
    /// </returns>
    public static CatchRecordResult RecordCatch(string displayName, float displaySize, string sizeRank)
    {
        if (!IsUsableName(displayName))
        {
            return new CatchRecordResult(firstCatch: false, newBest: false, previousBest: NoBestSize);
        }

        // 記録前の状態を控える（初捕獲判定と「更新前のベスト」の両方に使う）。
        // 釣った数を増やしたあとでは初捕獲を判定できないので、必ず先に読む。
        int count = SEED.SaveData.GetInt(CatchCountKeyPrefix + displayName, NoCatchCount);
        float previousBest = BestSize(displayName);
        bool firstCatch = count < CaughtThreshold;

        // 釣った数は成否に関わらず 1 増やす
        SEED.SaveData.SetInt(CatchCountKeyPrefix + displayName, count + CatchIncrement);

        // ベストサイズは超えたときだけ更新し、ランクも同時に差し替える
        bool isNewRecord = displaySize > previousBest;
        if (isNewRecord)
        {
            SEED.SaveData.SetFloat(BestSizeKeyPrefix + displayName, displaySize);
            SEED.SaveData.SetString(BestRankKeyPrefix + displayName, sizeRank ?? NoBestRank);
        }

        SEED.SaveData.Save();
        return new CatchRecordResult(firstCatch, isNewRecord, previousBest);
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>キーの一部として使える表示名か（空・空白のみを弾く）。</summary>
    /// <param name="displayName">検査する表示名。</param>
    private static bool IsUsableName(string displayName)
        => !string.IsNullOrWhiteSpace(displayName);
}
