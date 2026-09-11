/// <summary>
/// 「どの仮想魚を実体化するか」を決める規則
/// 【実体化条件の唯一の判断点】。
///
/// [なぜ切り出すか]
/// 実体化の条件は<b>レベル帯</b>と<b>距離</b>の 2 本立てで、どちらもチューニング対象である。
/// 生成の段取り（<see cref="FishManager"/>）と混ぜると調整のたびに生成処理を触ることになるため、
/// 純粋な判定だけをこのクラスへ集めている（副作用なし・アクタに触らない）。
///
/// [レベル帯]
/// 「現在の基準レベル L」（掛かっている魚が居ればそのレベル、居なければウキが落ちた水域のレベル）から
/// <see cref="ActiveLevelSpan"/> 段ぶん上・<see cref="ActiveLevelSpanBelow"/> 段ぶん下まで
/// （L-下 〜 L+上）が実体化の対象。
/// 連鎖（わらしべ）で乗り換えられるのは L+1 以上の魚なので、span は
/// <c>FishingController.RadarSkipLevelGap</c>（格上カリングのレベル差）と揃えるのが自然
/// ―― span=2 なら L+3 以上はもともと更新も描画も止まっている個体である。
///
/// [距離とヒステリシス]
/// 実体化は <see cref="MaterializeRadius"/> より近い個体だけ、仮想へ戻すのは
/// <see cref="DematerializeRadius"/> より遠くなってからにする。しきい値を分けることで、
/// 境界付近の個体が毎回の判定で生成と破棄を繰り返す「ばたつき」を防ぐ。
/// </summary>
public sealed class FishMaterializationPolicy
{
    /// <summary>レベル帯の指定が無いことを表す値（負のレベル添字は存在しない）。</summary>
    public const int NoForcedLevel = -1;

    /// <summary>
    /// 基準レベルから何段上までを実体化するか（0 なら基準レベルより上は見ない）。
    /// </summary>
    public int ActiveLevelSpan { get; set; }

    /// <summary>
    /// 基準レベルから何段下までを実体化するか（0 なら基準レベルより下は見ない）。
    /// ウキが落ちたリングの内側にも魚は居て画面に映るので、通常は 1 段ぶん残す。
    /// </summary>
    public int ActiveLevelSpanBelow { get; set; }

    /// <summary>この距離（メートル）より近い仮想魚を実体化する。</summary>
    public float MaterializeRadius { get; set; }

    /// <summary>この距離（メートル）より遠ざかった実体を仮想へ戻す（必ず実体化半径以上にする）。</summary>
    public float DematerializeRadius { get; set; }

    /// <summary>
    /// レベル帯（基準レベルからの段数）に入っているか。
    /// 台本・チュートリアルによる強制レベルの扱いは呼び出し側が上乗せする。
    /// </summary>
    /// <param name="levelIndex">判定するレベルの添字（0 始まり）。</param>
    /// <param name="baseLevelIndex">基準レベルの添字（0 始まり）。</param>
    /// <returns>レベル帯の中なら true。</returns>
    public bool IsWithinSpan(int levelIndex, int baseLevelIndex)
        => levelIndex >= baseLevelIndex - (ActiveLevelSpanBelow < 0 ? 0 : ActiveLevelSpanBelow)
        && levelIndex <= baseLevelIndex + (ActiveLevelSpan < 0 ? 0 : ActiveLevelSpan);

    /// <summary>
    /// 距離の条件で実体化してよいか（距離の 2 乗で比べて平方根を省く）。
    /// </summary>
    /// <param name="sqrDistance">判定原点（ウキ）からの水平距離の 2 乗。</param>
    /// <returns>実体化してよいなら true。</returns>
    public bool IsWithinMaterializeRadius(float sqrDistance)
        => sqrDistance <= MaterializeRadius * MaterializeRadius;

    /// <summary>
    /// 距離の条件で仮想へ戻すべきか（ヒステリシスの外側しきい値）。
    /// </summary>
    /// <param name="sqrDistance">判定原点（ウキ）からの水平距離の 2 乗。</param>
    /// <returns>仮想へ戻すべきなら true。</returns>
    public bool IsBeyondDematerializeRadius(float sqrDistance)
        => sqrDistance > DematerializeRadius * DematerializeRadius;
}
