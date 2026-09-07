// ============================================================================
//  MissionContext.cs
//  ミッションから触れてよい「周辺」への窓口をまとめた受け渡し用オブジェクト。
// ============================================================================

/// <summary>
/// ミッションの判定クラスへ渡す文脈オブジェクト
/// 【ミッションが外の世界へ触れる唯一の窓口】。
///
/// 【なぜ用意するか】
/// 判定クラス（<see cref="IMission"/> の実装）は SEEDScript ではないので、
/// gameObject も transform も持たない。かといって各クラスが
/// <c>FishingController.Current</c> や <c>GameObject.Find</c> を直接呼び始めると、
/// 「ミッションが何に依存しているか」がクラスごとにバラバラになり追えなくなる。
/// 依存をこの 1 型に集約すれば、依存の増減がここのプロパティ一覧に必ず現れる。
///
/// 【寿命】
/// ミッション 1 件につき 1 個。<see cref="TutorialDirector"/> が作り、
/// <see cref="IMission.Begin"/> / <see cref="IMission.Update"/> / <see cref="IMission.End"/>
/// のすべてへ同じインスタンスを渡す。
///
/// 【マネージャ参照をキャッシュしない理由】
/// 各マネージャの静的アクセサはホットリロード・シーン遷移で差し替わる。
/// プロパティとして毎回引き直すことで、古いインスタンスを掴み続ける事故を防ぐ。
/// </summary>
public sealed class MissionContext
{
    // ─── 参照 ────────────────────────────────────────────────

    /// <summary>このミッションを進めているディレクタ。</summary>
    public TutorialDirector Director { get; }

    /// <summary>このミッションの設定データ（名前・目的・ルール上書き・パラメータ）。</summary>
    public TutorialMission Data { get; }

    // ─── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// ディレクタとミッションデータを束ねて文脈を作る。
    /// </summary>
    /// <param name="director">進行を司るディレクタ。</param>
    /// <param name="data">このミッションの設定データ。</param>
    public MissionContext(TutorialDirector director, TutorialMission data)
    {
        Director = director;
        Data     = data;
    }

    // ─── 周辺システムへの窓口（毎回引き直す）───────────────

    /// <summary>釣りの状態機械（シーンに居なければ null）。</summary>
    public FishingController? Controller => FishingController.Current;

    /// <summary>やり取り（リズム勝負）の本体（釣り本体が居なければ null）。</summary>
    public FishingFight? Fight => FishingController.Current?.Fight;

    /// <summary>魚の出現マネージャ（シーンに居なければ null）。</summary>
    public FishManager? Fish => FishManager.Current;

    /// <summary>漂流物マネージャ（シーンに居なければ null）。</summary>
    public DriftItemManager? Drift => DriftItemManager.Current;

    /// <summary>プレイヤーの移動スクリプト（ディレクタへ結線されていなければ null）。</summary>
    public PlayerMove? Player => Director.Player;

    // ─── ディレクタへの依頼 ─────────────────────────────────

    /// <summary>
    /// ミッションの最中に説明の吹き出しを 1 回割り込ませる
    /// （漂流物を拾ったときの解説など）。
    ///
    /// 割り込み中はミッションの <see cref="IMission.Update"/> が止まり、
    /// 台詞を読み終えて決定を押すと元の状態（ルール上書き・入力許可）へ戻る。
    /// 該当する台詞が 1 枚も無ければ何も起きない。
    /// </summary>
    /// <param name="slot">出したい台詞の場面。</param>
    public void Interject(TutorialDialogueSlot slot) => Director.Interject(slot);
}
