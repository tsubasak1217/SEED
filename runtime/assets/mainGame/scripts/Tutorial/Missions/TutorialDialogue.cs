// ============================================================================
//  TutorialDialogue.cs
//  チュートリアルの説明台詞 1 枚ぶんのデータ定義。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 説明台詞 1 枚（吹き出し 1 回ぶん）のデータ。
///
/// 【責務】
/// 「どのミッションの」「どの場面で」「何を言い」「画面のどこに出すか」だけを持つ。
///
/// 【なぜミッションと別リストなのか】
/// インスペクタの構造体リストは<b>入れ子が 1 段まで</b>で、構造体の中に
/// 「複数行テキストのリスト」を置けない（TextArea 属性はリストの要素に効かない）。
/// 台詞を独立したリストにして <see cref="missionId"/> と <see cref="slot"/> で引くことで、
/// 1 ミッションに何枚でも複数行の台詞を持たせられる。
///
/// 【文体と改行の規則】
/// テバ口調（一人称「俺」・命令形の江戸弁調・プレイヤーは「ササミ」）。
/// 吹き出しの本文枠は 326.7 x 181.7 px・フォント 30px・行送り 1.35 なので、
/// <b>1 行は全角 10 文字前後・最大 4 行</b>に収める。文節の切れ目で改行し、
/// 行の途中で語を割らない。アイコン記法の前後には半角空白を 1 つ置く。
/// </summary>
[System.Serializable]
public struct TutorialDialogue
{
    /// <summary>
    /// この台詞が属するミッションの ID（<see cref="TutorialMission.id"/> と一致させる）。
    /// 一致するミッションが無い台詞は表示されない（設定漏れは警告で知らせる）。
    /// </summary>
    [SerializeField(Label = "ミッションID", Tooltip = "どのミッションの台詞か（TutorialMission の ID と一致させる）")]
    public string missionId;

    /// <summary>この台詞を出す場面。</summary>
    [SerializeField(Label = "場面", Tooltip = "Intro=開始前 / Clear=達成後 / Drift*=漂流物を拾ったとき / Cutscene=締めの演出")]
    public TutorialDialogueSlot slot;

    /// <summary>
    /// 台詞の本文。インライン画像記法（アイコン）と改行が使える。
    /// 1 行は全角 10 文字前後・最大 4 行に収めること（吹き出しの枠に入らなくなる）。
    /// </summary>
    [SerializeField(Label = "台詞", Tooltip = "吹き出しに出す本文。1 行 全角10文字前後・最大4行")]
    [TextArea(4)]
    public string text;

    /// <summary>
    /// 吹き出しを重ねる位置アンカー（FishingUI/TutorialAnchors 配下の空アクタ）。
    /// 未設定なら窓は現在の位置のまま出る（詰まらないためのフォールバック）。
    /// </summary>
    [SerializeField(Label = "位置アンカー", Tooltip = "吹き出しを重ねる 2D アクタ（TutorialAnchors 配下）")]
    public SEED.CanvasTransform anchorTarget;

    /// <summary>
    /// この台詞を出しているあいだゲーム時間を止めるか。
    /// 説明だけを読ませたいときは true、ミッションの最中に差し込むときも
    /// 手を止めて読ませたいなら true にする。
    /// </summary>
    [SerializeField(Label = "時間を止める", Tooltip = "true でゲーム時間を停止して読ませる（UI は実時間で動く）")]
    public bool pauseTime;
}
