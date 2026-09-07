// ============================================================================
//  TutorialEnums.cs
//  チュートリアル 1 手順の「振る舞いの種類」を表す列挙型をまとめる。
// ============================================================================

/// <summary>
/// 説明窓をどこに出すか（配置モード）。
/// </summary>
public enum TutorialAnchorMode
{
    /// <summary>キャンバス座標の固定位置に出す（画面中央原点・Y 下向き・1 単位 1px）。</summary>
    ScreenFixed,

    /// <summary>対象アクタのワールド位置を画面へ射影し、その少し上に追従させる。</summary>
    AboveTarget,
}

/// <summary>
/// 手順を開始する条件。
/// </summary>
public enum TutorialStartCondition
{
    /// <summary>前の手順が終わったら即座に開始する。</summary>
    Immediately,

    /// <summary>指定した名前のイベント（FishingEvents の定数）が飛ぶまで待ってから開始する。</summary>
    OnEvent,
}

/// <summary>
/// 手順を終了する条件。
/// </summary>
public enum TutorialFinishCondition
{
    /// <summary>決定入力（Enter / Space / 左クリック）で送る。</summary>
    Confirm,

    /// <summary>指定した名前のイベントが飛んだら終了する（＝実際に操作できたら進む）。</summary>
    OnEvent,

    /// <summary>指定した秒数（実時間）が経過したら終了する。</summary>
    Seconds,
}

/// <summary>
/// 手順の開始時に釣りシステムへ強制する「台本」の種類。
///
/// チュートリアルは乱数任せの本編と違い「必ずこの出来事が起きる」必要があるため、
/// 手順ごとに釣りマネージャへ強制設定を掛ける。
/// </summary>
public enum TutorialScriptedAction
{
    /// <summary>何も強制しない（通常どおりの乱数挙動）。</summary>
    None,

    /// <summary>指定レベルの魚を必ず・すぐに食いつかせる（FishManager の台本設定）。</summary>
    ForceBite,

    /// <summary>漂流物を 1 個その場に出す（DriftItemManager の台本生成）。</summary>
    SpawnDrift,
}
