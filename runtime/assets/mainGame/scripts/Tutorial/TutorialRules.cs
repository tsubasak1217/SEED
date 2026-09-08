// ============================================================================
//  TutorialRules.cs
//  チュートリアル中だけ釣りシステムの挙動を上書きする「ルール表」。
// ============================================================================

/// <summary>
/// チュートリアルのミッションが釣りシステムへ掛ける<b>ルール上書き</b>を保持する静的クラス
/// 【上書きルールの唯一の置き場】。
///
/// 【なぜ静的クラスなのか】
/// 上書きを読む側（FishManager / DriftItemManager / FishingFight / FishingController）は
/// チュートリアルの存在を知らないまま「例外規則が掛かっているか」だけを問い合わせたい。
/// 参照フィールドで結線すると、シーンにチュートリアルが居ないだけで釣りが動かなくなる
/// （＝本編がチュートリアルに依存する）。<see cref="InputGate"/> と同じ流儀で、
/// 「読む側は静的な問い合わせを 1 行挟むだけ」に留める。
///
/// 【既定値＝上書き無し】
/// すべてのフィールドは「上書きしない」を意味する値で始まる。<see cref="Active"/> が false の間は
/// 読む側の分岐が必ず素通りするため、<b>チュートリアルが居なければ挙動は従来どおり</b>。
///
/// 【掛けた側が必ず戻す】
/// 掛けるのは <c>TutorialDirector</c> だけ。ミッションの切り替え時・チュートリアル完了時・
/// スクリプト破棄時に必ず <see cref="Clear"/> を呼んで戻す責任を持つ。
/// 静的フィールドはホットリロードで作り直されるが<b>シーン遷移では作り直されない</b>ため、
/// 解除漏れは焼き付く。
///
/// 【使い方（読む側）】
/// <code>
/// // 漂流物マネージャ: チュートリアルが「漂流物なし」と言っていれば出さない
/// if (TutorialRules.Active &amp;&amp; TutorialRules.DriftDisabled) { return false; }
/// </code>
/// </summary>
public static class TutorialRules
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>レベルフィルタ「制限なし」を表す値（1 始まりのレベル指定なので 0 は無効値）。</summary>
    public const int NoLevelFilter = 0;

    /// <summary>魚種フィルタ「制限なし」を表す値。</summary>
    public const string NoPrefabFilter = "";

    /// <summary>糸ゲージを満タンへ戻すときの既定の所要秒数（「ぬっ」と戻る手触り）。</summary>
    public const float DefaultGaugeRestoreSeconds = 0.5f;

    /// <summary>方向指定で漂流物を流すときの既定の距離（メートル。ウキからの水平距離）。</summary>
    public const float DefaultDriftDirectionDistance = 6.0f;

    /// <summary>
    /// 漂流物の巻き込み半径「上書き無し」を表す値。
    /// 半径 0 の当たり判定は「絶対に拾えない」＝設定として意味を持たないので、
    /// 0 以下を「上書きしない（prefab の値をそのまま使う）」の合図に使う。
    /// </summary>
    public const float NoDriftPickupRadiusOverride = 0f;

    // ─── 全体の有効・無効 ────────────────────────────────────

    /// <summary>
    /// 上書きが有効か【全分岐の門番】。
    ///
    /// 読む側は必ずこのフラグを最初に見る。false の間は個別のフィールドを一切参照しないため、
    /// チュートリアルが居ないシーン・完了後のプレイでは従来の挙動とビット単位で同一になる。
    /// </summary>
    public static bool Active;

    // ─── 出現する魚の制限 ────────────────────────────────────

    /// <summary>
    /// 出現させる魚のレベル（<b>1 始まり</b>）。<see cref="NoLevelFilter"/>（0）で制限なし。
    /// FishManager の個体補充がこのレベル以外を作らなくなる。
    /// </summary>
    public static int FishLevelFilter = NoLevelFilter;

    /// <summary>
    /// 出現させる魚の .actor パスに含まれていてほしい文字列（例 <c>"kumanomi"</c>）。
    /// <see cref="NoPrefabFilter"/>（空文字）で制限なし。
    /// 「必ずこの魚を出す」台本用。一致する候補が無ければ通常の抽選へフォールバックする。
    /// </summary>
    public static string FishPrefabFilter = NoPrefabFilter;

    /// <summary>
    /// 魚種フィルタを<b>許可リスト</b>として扱うか。
    ///
    /// false（既定）なら <see cref="FishPrefabFilter"/> は「できればこの魚」の希望で、
    /// 一致する候補が無ければ通常の抽選へフォールバックする（従来の挙動）。
    /// true なら一致しない魚は<b>一切出さず、既に泳いでいる個体も取り除く</b>。
    /// 「連鎖が起きない・目当ての魚だけが居る」状態を作る説明ミッション用。
    /// </summary>
    public static bool FishPrefabExclusive;

    /// <summary>
    /// <b>最低 1 匹は維持したい</b>魚の .actor パスに含まれる文字列（例 <c>"kumanomi"</c>）。
    /// <see cref="NoPrefabFilter"/>（空文字）で指定なし。
    ///
    /// <see cref="FishPrefabFilter"/> が「出す魚をその 1 種へ固定する」のに対し、
    /// こちらは<b>他の魚種の抽選はそのままに</b>、対象の魚が 1 匹も居ないときだけ
    /// 優先的に 1 匹補充させる（<see cref="FishManager"/> の個体補充が読む）。
    /// 釣り上げて居なくなれば、次の補充でまた 1 匹だけ供給される。
    /// </summary>
    public static string FishPrefabRequired = NoPrefabFilter;

    /// <summary>維持数の上書き「上書き無し」を表す値。</summary>
    public const int NoPopulationOverride = 0;

    /// <summary>
    /// 対象レベル（<see cref="FishLevelFilter"/> の対象、または <see cref="FishManager"/> の
    /// 台本が固定しているレベル）の自然出現の維持数をこの値へ強制的に置き換える。
    /// <see cref="NoPopulationOverride"/>（0）で上書きなし（<see cref="FishLevelEntry.maintainCount"/> のまま）。
    /// 「魚種は固定したいが、個体数はもっと絞りたい」説明ミッション用
    /// （<see cref="TutorialMission.fishPopulationOverride"/> から渡される）。
    /// </summary>
    public static int FishPopulationOverride = NoPopulationOverride;

    // ─── わらしべ連鎖の制限 ─────────────────────────────────

    /// <summary>
    /// true の間、掛かっている魚を別の魚が食う<b>わらしべ連鎖を成立させない</b>。
    /// 巻き上げの練習中に横取りされて手順が飛ぶのを防ぐ
    /// （<c>FishingController.TryEatHookedFish</c> が早期 return する）。
    /// </summary>
    public static bool ChainDisabled;

    // ─── 漂流物の制限 ────────────────────────────────────────

    /// <summary>true の間、漂流物を自然出現させない（台本による明示生成だけを許す）。</summary>
    public static bool DriftDisabled;

    /// <summary>
    /// true の間、漂流物を<b>漂わせず・寿命でも消さない</b>（置いた場所に留める）。
    /// 台本が「巻く方向の一直線上」に並べた漂流物がずれて拾えなくなるのを防ぐ。
    ///
    /// <b>片付けの抑止も兼ねる</b>: <see cref="DriftItemManager"/> は
    /// 「出してよい条件」が崩れたフレームで生存中の漂流物を一掃するが、
    /// このフラグが立っている間は台本が置いた個体を巻き上げ待ちで残す
    /// （自然出現の停止 <see cref="DriftDisabled"/> と一掃は別の判断である）。
    /// </summary>
    public static bool DriftStationary;

    /// <summary>
    /// 漂流物の巻き込み判定の半径（メートル）をこの値へ強制的に置き換える。
    /// <see cref="NoDriftPickupRadiusOverride"/>（0 以下）で上書きなし（prefab の値のまま）。
    ///
    /// 台本が一直線上に並べた漂流物でも、巻きの 1 フレームの移動量や
    /// ウキの微妙な横ぶれで prefab の当たり半径（既定 0.8m）を擦り抜けることがある。
    /// 説明ミッションの間だけ半径を広げて「巻けば必ず拾える」を保証するために使う。
    /// </summary>
    public static float DriftPickupRadiusOverride = NoDriftPickupRadiusOverride;

    // ─── やり取り（ビートバトル）の制限 ─────────────────────

    /// <summary>
    /// true の間、出題・回答のビートを行わず<b>ずっと隙（ひるみ）状態</b>にする。
    /// 「巻いて釣り上げるだけ」を体験させるミッション用。
    /// </summary>
    public static bool BeatDisabled;

    /// <summary>true の間、糸の残りが 0 になっても糸切れにしない（減りはする）。</summary>
    public static bool LineBreakDisabled;

    /// <summary>
    /// true の間、回答でミスしたら隙（Rest）を挟まずに即もう一周（出題へ戻る）。
    /// 「1 周ミスなしで刻む」ミッション用。
    /// </summary>
    public static bool RestartCycleOnMiss;

    /// <summary>
    /// 周回をやり直すときに糸ゲージを満タンへ戻すのに掛ける秒数。
    /// 0 以下なら即座に満タンへ戻る。
    /// </summary>
    public static float GaugeRestoreSeconds = DefaultGaugeRestoreSeconds;

    // ─── 合わせ（フッキング）の制限 ─────────────────────────

    /// <summary>
    /// true の間、合わせをミスしても魚を逃がさず<b>必ずウキへ戻して前アタリからやり直す</b>。
    /// 「合わせを覚える」ミッションで詰まらせないための救済。
    /// </summary>
    public static bool RebiteAfterHookMiss;

    // ─── カメラの制限 ────────────────────────────────────────

    /// <summary>
    /// true の間、通常のカメラ追従（<see cref="CameraMove"/>）を止めて
    /// <b>チュートリアル側がカメラを直接動かす</b>。
    ///
    /// 締めの演出で怪獣を追わせるために使う。掛けたミッションが
    /// 終了時に必ず false へ戻す（<see cref="Clear"/> でも落ちる）。
    /// </summary>
    public static bool CameraSuspended;

    /// <summary>
    /// true の間、糸が切れても<b>掛かった状態のままやり取りを仕切り直す</b>。
    /// 「釣り上げよう」のミッションで、失敗しても投げ直しからではなく
    /// 掛かった続きから再開させるために使う。
    /// </summary>
    public static bool RestartFightOnLineBreak;

    // ─── アタリの抑止（説明台詞を読ませている間の足止め） ───

    /// <summary>
    /// true の間、魚の食いつきに関わる進行を<b>一時停止</b>する。
    ///
    /// 止めるのは 2 箇所（<see cref="Fish"/> と <see cref="FishingController"/> の
    /// 双方が個別に見る）。
    /// <list type="bullet">
    ///   <item>まだ食いつく前の魚の待ちカウントダウン（台本の秒数・通常抽選の乱数の両方）</item>
    ///   <item>既に前アタリ（コツコツ）に入っている魚が本アタリ（合わせ受付）へ進む処理</item>
    /// </list>
    ///
    /// 【なぜ必要か】
    /// チュートリアルは「ミッションの台本（魚の仕込みなど）を説明より先に済ませる」設計
    /// （<see cref="TutorialDirector.AdvanceToNextMission"/> 参照）になっているため、
    /// 何も対策しないと<b>説明を読んでいる最中にアタリが来て魚が掛かってしまう</b>。
    /// このフラグで「読んでいる間はアタリの時計を進めない」ようにし、読み終えた瞬間
    /// （<see cref="TutorialDirector.OnIntroFinished"/> など）に false へ戻すことで
    /// 続きから自然にアタリが再開する（＝待ち直しにはならない。タイマー自体はリセットしない）。
    ///
    /// 掛けるのは <see cref="TutorialDirector"/> だけ。説明・クリアバナー・ Outro の
    /// 表示中は必ず true。ミッション本編（Playing）に入るときは、そのミッションの
    /// <see cref="TutorialMission.suppressBite"/> の値へ差し替える（既定 false なので
    /// 通常は解除されるが、true が指定されたミッションでは本編中も抑止が続く。
    /// 「まず海を空にしてから次のミッションで狙った魚だけを仕込みたい」場面用）。
    /// </summary>
    public static bool BiteSuppressed;

    /// <summary>
    /// true の間、やり取り（ビートバトル）の<b>フェーズ進行そのものを凍結</b>する
    /// （<see cref="FishingFight.Tick"/> が <c>Paused</c> と同じ扱いで即座に抜ける）。
    ///
    /// 【なぜ時間停止と別に必要か】
    /// 説明の台詞を読ませている間は <c>SEED.Time.Scale = 0</c> で止めるのが基本だが、
    /// 「時間を止めない」指定の台詞（波や鳥を動かしたまま読ませたい場面）が混ざると、
    /// <b>魚が掛かった状態から始まるミッションでは余白(LeadIn)が進んで
    /// 読んでいる最中に出題(Call)が始まってしまう</b>。
    /// 時間停止が効かない経路が 1 つでもあれば同じ事故が起きるので、
    /// 「台詞を出している間はやり取りを進めない」を時間軸とは独立に保証する。
    ///
    /// <see cref="BiteSuppressed"/>（まだ掛かっていない魚のアタリを止める）の
    /// 掛かった<b>あと</b>版に当たる。掛けるのは <see cref="TutorialDirector"/> だけ。
    /// </summary>
    public static bool FightSuppressed;

    // ─── 解除 ────────────────────────────────────────────────

    /// <summary>
    /// すべての上書きを解除して従来の挙動へ戻す【解除の唯一の出口】。
    /// ミッションの切り替え・チュートリアル完了・スクリプト破棄で必ず呼ぶこと。
    /// </summary>
    public static void Clear()
    {
        Active               = false;
        FishLevelFilter      = NoLevelFilter;
        FishPrefabFilter     = NoPrefabFilter;
        FishPrefabExclusive  = false;
        FishPrefabRequired   = NoPrefabFilter;
        FishPopulationOverride = NoPopulationOverride;
        ChainDisabled        = false;
        DriftDisabled        = false;
        DriftStationary      = false;
        DriftPickupRadiusOverride = NoDriftPickupRadiusOverride;
        BeatDisabled         = false;
        LineBreakDisabled    = false;
        RestartCycleOnMiss   = false;
        RebiteAfterHookMiss  = false;
        RestartFightOnLineBreak = false;
        BiteSuppressed          = false;
        FightSuppressed         = false;
        CameraSuspended         = false;
        GaugeRestoreSeconds  = DefaultGaugeRestoreSeconds;
    }
}
