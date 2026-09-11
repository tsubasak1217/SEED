using System.Collections.Generic;

/// <summary>
/// レベルごとの仮想魚（<see cref="VirtualFish"/>）の保管庫
/// 【「海にいま何匹居るか」を持つ唯一の台帳】。
///
/// [責務]
/// レコードの<b>保持・追加・削除・列挙</b>と、仮想個体の<b>漂いの一括更新</b>だけを行う。
/// 「何匹まで維持するか」「どの魚種で作るか」「どこへ出すか」といったゲーム側の規則は
/// <see cref="FishManager"/> が決め、このクラスは受け取った結果を並べるだけである
/// （単一責任原則）。実体の生成・破棄も行わない。
///
/// [レベル添字]
/// 添字は <c>FishManager.levels</c> の添字（0 始まり）と一対一で対応する。
/// レベルが増えたときは <see cref="EnsureLevelCount"/> で伸ばす（縮めない＝添字の安定を優先）。
/// </summary>
public sealed class VirtualFishPool
{
    /// <summary>レベル添字ごとの仮想魚のリスト（添字＝レベル添字、0 始まり）。</summary>
    private readonly List<List<VirtualFish>> byLevel = new();

    /// <summary>空のレベルを返すときに使う共有の空リスト（毎回の確保を避ける）。</summary>
    private static readonly List<VirtualFish> EmptyRecords = new();

    /// <summary>いま台帳が持っているレベルの数。</summary>
    public int LevelCount => byLevel.Count;

    /// <summary>
    /// レベル数を指定数まで伸ばす（足りない分だけ空リストを足す）。
    /// 縮小はしない ―― 添字とレコードの対応が崩れると魚が迷子になるため。
    /// </summary>
    /// <param name="count">必要なレベル数。</param>
    public void EnsureLevelCount(int count)
    {
        while (byLevel.Count < count) { byLevel.Add(new List<VirtualFish>()); }
    }

    /// <summary>
    /// 指定レベルの仮想魚を列挙する（読み取り専用）。
    /// 範囲外のレベルでは空リストを返すので、呼び出し側で範囲チェックは要らない。
    /// </summary>
    /// <param name="levelIndex">レベルの添字（0 始まり）。</param>
    /// <returns>そのレベルのレコード一覧。</returns>
    public IReadOnlyList<VirtualFish> RecordsOf(int levelIndex)
        => levelIndex >= 0 && levelIndex < byLevel.Count ? byLevel[levelIndex] : EmptyRecords;

    /// <summary>
    /// 指定レベルの「維持数に数える個体」の数。
    /// 台本が出した個体（<see cref="VirtualFish.Pinned"/>）は特別枠なので数えない。
    /// </summary>
    /// <param name="levelIndex">レベルの添字（0 始まり）。</param>
    /// <returns>維持数の勘定に入る個体数。</returns>
    public int UnpinnedCount(int levelIndex)
    {
        var records = RecordsOf(levelIndex);
        int count = 0;
        for (int i = 0; i < records.Count; i++)
        {
            if (!records[i].Pinned) { count++; }
        }
        return count;
    }

    /// <summary>
    /// レコードを台帳へ加える【追加の唯一の入口】。
    /// レベル添字が範囲外なら何もしない（台帳を勝手に伸ばさない）。
    /// </summary>
    /// <param name="record">加える仮想魚。</param>
    public void Add(VirtualFish record)
    {
        if (record.LevelIndex < 0 || record.LevelIndex >= byLevel.Count) { return; }
        byLevel[record.LevelIndex].Add(record);
    }

    /// <summary>
    /// レコードを台帳から外す【削除の唯一の出口】。
    /// 実体の破棄は呼び出し側の責務（このクラスはアクタに触らない）。
    /// </summary>
    /// <param name="record">外す仮想魚。</param>
    public void Remove(VirtualFish record)
    {
        if (record.LevelIndex < 0 || record.LevelIndex >= byLevel.Count) { return; }
        byLevel[record.LevelIndex].Remove(record);
    }

    /// <summary>
    /// いま実体（アクタ）を持っている個体の総数。
    /// 「重さ」の目安としてログへ出すために使う。
    /// </summary>
    public int MaterializedCount
    {
        get
        {
            int count = 0;
            for (int level = 0; level < byLevel.Count; level++)
            {
                var records = byLevel[level];
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Materialized) { count++; }
                }
            }
            return count;
        }
    }

    /// <summary>台帳が持つレコードの総数（仮想＋実体）。</summary>
    public int TotalCount
    {
        get
        {
            int count = 0;
            for (int level = 0; level < byLevel.Count; level++) { count += byLevel[level].Count; }
            return count;
        }
    }

    /// <summary>
    /// 仮想（未実体化）の個体だけをまとめて漂わせる
    /// 【仮想個体の一括更新の唯一の入口】。
    ///
    /// 実体化済みの個体は本物の <see cref="Fish"/> スクリプトが動かすので触らない。
    /// 移動後の座標は <paramref name="clampToRing"/> を通して出現円環の中へ収める
    /// （実体側の押し戻し <c>FishManager.ClampFishToRings</c> と同じ役目）。
    /// </summary>
    /// <param name="deltaTime">前回の更新からの経過秒数。</param>
    /// <param name="random">方向転換の乱数源。</param>
    /// <param name="speed">遊泳速度（m/秒）。</param>
    /// <param name="turnIntervalSeconds">進行方向を振り直す間隔（秒）。</param>
    /// <param name="turnJitterRadians">1 回の振り直しで変える角度の最大値（ラジアン）。</param>
    /// <param name="clampToRing">(レベル添字, 座標) → 円環内へ収めた座標 を返す関数。</param>
    public void DriftVirtual(
        float deltaTime,
        System.Random random,
        float speed,
        float turnIntervalSeconds,
        float turnJitterRadians,
        System.Func<int, SEED.Vector3, SEED.Vector3> clampToRing)
    {
        for (int level = 0; level < byLevel.Count; level++)
        {
            var records = byLevel[level];
            for (int i = 0; i < records.Count; i++)
            {
                var record = records[i];
                if (record.Materialized) { continue; }

                record.Drift(deltaTime, random, speed, turnIntervalSeconds, turnJitterRadians);
                record.Position = clampToRing(level, record.Position);
            }
        }
    }
}
