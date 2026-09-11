// ============================================================================
//  CutsceneSettings.cs
//  締め演出（怪獣の跳ね上がり＋それを追うカメラ）の調整値を束ねた値オブジェクト。
// ============================================================================

/// <summary>
/// 締め演出（<see cref="CutsceneMission"/>）の調整値
/// 【演出パラメータの既定値と正規化の唯一の置き場】。
///
/// 【責務】
/// ミッションデータ（<see cref="TutorialMission"/>）に並んだ「演出:〜」の生の設定値を
/// 読み取り、未設定（0）を既定値へ補正した<b>そのまま使える値</b>として持つ。
/// 演出そのもの（位置の計算・カメラの操作）は持たない（それは
/// <see cref="CutsceneMission"/> の責務）。
///
/// 【なぜ <c>[System.Serializable]</c> の入れ子構造体にしていないか】
/// ミッションデータは <c>List&lt;TutorialMission&gt;</c>（TutorialDirector の
/// 「ミッション」リスト）の要素としてインスペクタに並ぶ。構造体配列の要素は
/// エンジン側の <c>SEED.ScriptStructArray</c> が「メンバはスカラ／参照／1 段の配列だけ」
/// という制限付きで扱っており、<b>入れ子の構造体メンバが 1 つでもあると
/// <c>TryGetLayout</c> が false を返して配列フィールド全体が非対応になる</b>
/// （インスペクタからミッション一覧が消え、実行時にもシーンの保存値が
///  流し込まれなくなる）。そのため調整値は <see cref="TutorialMission"/> の
/// 平坦なメンバとして持ち、このクラスは「読み出して補正した結果を束ねる」役に徹する。
///
/// 【既定値の与え方（2 段構え）】
///  1. <see cref="TutorialMission"/> 側のフィールド初期化子（＋パラメータ無し
///     コンストラクタ）で本来の既定値を持たせる。インスペクタにも実行時にも
///     この値が出るため、シーンに保存値が無くても従来どおりの動きになる。
///  2. それでも 0 が入ってきた場合（古い保存値・誤入力）に備え、
///     「大きさとして 0 以下があり得ない項目」だけ <see cref="PositiveOr"/> で
///     既定値へ寄せる。0 が意味を持つ項目（角度・画角・高さの上乗せ・横ずれ）は
///     補正せずそのまま使う。列挙型（カメラ制御・出現位置の基準）は保存値が無ければ
///     宣言順の先頭＝既定になるので、補正しない。
/// </summary>
public sealed class CutsceneSettings
{
    // ─── 既定値（旧 CutsceneMission の定数をここへ集約。重複定義しない）───

    /// <summary>
    /// カメラ制御の既定値。
    /// 通常の追従のまま画面の奥で跳ねさせる（カメラには触らない）のが既定の絵。
    /// </summary>
    public const CutsceneCameraMode DefaultCameraMode = CutsceneCameraMode.FollowNormal;

    /// <summary>出現位置の基準の既定値（通常カメラの視界の奥へ出す）。</summary>
    public const CutsceneSpawnAnchor DefaultSpawnAnchor = CutsceneSpawnAnchor.CameraView;

    /// <summary>視界基準のとき、カメラの前方向へ何メートル先へ出すかの既定値。</summary>
    public const float DefaultCameraViewDistance = 60f;

    /// <summary>視界基準のとき、カメラの真横へ何メートルずらすかの既定値（正で画面右）。</summary>
    public const float DefaultCameraViewLateral = 15f;

    /// <summary>跳ぶ方向（画面右方向を 0 度とした水平角）の既定値。</summary>
    public const float DefaultJumpDirectionDegrees = 0f;

    /// <summary>跳ね上がる高さ（メートル。水面からの頂点の高さ）の既定値。</summary>
    public const float DefaultJumpApexHeight = 14f;

    /// <summary>助走で進む水平距離（メートル）の既定値。</summary>
    public const float DefaultJumpTravelDistance = 26f;

    /// <summary>待機中に怪獣を沈めておく深さ（メートル・正値）の既定値。</summary>
    public const float DefaultSubmergedDepth = 8f;

    /// <summary>
    /// 着水までに掛けるピッチ回転量（度）の既定値。
    /// 従来の実装は「頂点で 0 度・前後で ±45 度」だったので、振れ幅の合計は 90 度になる。
    /// </summary>
    public const float DefaultPitchSweepDegrees = 90f;

    /// <summary>カメラが怪獣から離れて構える距離（メートル）の既定値。</summary>
    public const float DefaultCameraDistance = 34f;

    /// <summary>カメラの高さ（怪獣の中心からの相対。メートル）の既定値。</summary>
    public const float DefaultCameraHeight = 8f;

    /// <summary>
    /// カメラの方位角（度）の既定値。
    /// 0 は「怪獣の進行方向側から見る＝正面を見る」構図で、従来の実装と同じ。
    /// </summary>
    public const float DefaultCameraAzimuthDegrees = 0f;

    /// <summary>注視点を怪獣の中心から何メートル上へずらすかの既定値（従来は上乗せなし）。</summary>
    public const float DefaultCameraLookAtHeight = 0f;

    /// <summary>カメラ補間の速さ（1 秒あたりの収束率）の既定値。</summary>
    public const float DefaultCameraLerpRate = 3.0f;

    /// <summary>怪獣モデルの向きの補正角（度）の既定値（補正なし）。</summary>
    public const float DefaultYawOffsetDegrees = 0f;

    // ─── 番兵値 ──────────────────────────────────────────────

    /// <summary>
    /// 「画角の指定なし」を表す番兵値。
    /// 視野角は必ず正の度数なので 0 は実在しない値であり、
    /// 「演出中も画角を変えない」をこの 1 値で表せる
    /// （<c>DialogueCameraDirector</c> と同じ約束）。
    /// </summary>
    public const float FieldOfViewUnspecified = 0f;

    // ─── 演出の組み立て（何を基準にするか）──────────────────

    /// <summary>
    /// カメラ制御。<see cref="CutsceneCameraMode.FollowNormal"/> なら演出はカメラに触らない。
    /// </summary>
    public CutsceneCameraMode CameraMode { get; init; } = DefaultCameraMode;

    /// <summary>出現位置の基準（通常カメラの視界の奥か、目標アクタ／ウキ基準か）。</summary>
    public CutsceneSpawnAnchor SpawnAnchor { get; init; } = DefaultSpawnAnchor;

    // ─── 怪獣の動き ──────────────────────────────────────────

    /// <summary>
    /// 視界基準のとき、カメラの水平前方向へ何メートル先に出すか
    /// （<see cref="CutsceneSpawnAnchor.CameraView"/> のときだけ使う）。
    /// </summary>
    public float CameraViewDistance { get; init; } = DefaultCameraViewDistance;

    /// <summary>
    /// 視界基準のとき、カメラの真横へ何メートルずらすか（正で画面右・負で画面左）。
    /// </summary>
    public float CameraViewLateral { get; init; } = DefaultCameraViewLateral;

    /// <summary>
    /// 跳ぶ方向（度）。視界基準のとき、<b>画面右方向</b>を 0 度として水平に回した向きへ跳ぶ。
    /// 0 で画面を左から右へ横切り、90 でカメラへ向かってきて、−90 で遠ざかる。
    /// </summary>
    public float JumpDirectionDegrees { get; init; } = DefaultJumpDirectionDegrees;

    /// <summary>跳ね上がる高さ（メートル。水面から頂点までの高さ）。</summary>
    public float JumpApexHeight { get; init; } = DefaultJumpApexHeight;

    /// <summary>助走で進む水平距離（メートル。開始地点から着水地点まで）。</summary>
    public float JumpTravelDistance { get; init; } = DefaultJumpTravelDistance;

    /// <summary>待機中に沈めておく深さ（メートル・正値。内部では下向きに使う）。</summary>
    public float SubmergedDepth { get; init; } = DefaultSubmergedDepth;

    /// <summary>怪獣モデルの向きの補正角（度）。進行方向を向けた上からさらに回す。</summary>
    public float YawOffsetDegrees { get; init; } = DefaultYawOffsetDegrees;

    /// <summary>
    /// 着水までに掛けるピッチ回転量（度）。
    /// 開始で −半分（頭を上げる）、頂点で 0、着水で +半分（頭を下げる）になる。
    /// 0 を指定すると回転しない（負値にすると回る向きが逆になる）。
    /// </summary>
    public float PitchSweepDegrees { get; init; } = DefaultPitchSweepDegrees;

    // ─── カメラ ──────────────────────────────────────────────

    /// <summary>カメラが怪獣から離れて構える距離（メートル）。</summary>
    public float CameraDistance { get; init; } = DefaultCameraDistance;

    /// <summary>カメラの高さ（怪獣の中心からの相対。メートル。負値で見上げる構図になる）。</summary>
    public float CameraHeight { get; init; } = DefaultCameraHeight;

    /// <summary>
    /// カメラの方位角（度）。怪獣の<b>進行方向</b>を基準に、カメラを水平に回り込ませる。
    /// 0 で進行方向側（正面から迫ってくる絵）、90 で真横、180 で背後、
    /// 負値で 90 とは反対側の真横になる。
    /// </summary>
    public float CameraAzimuthDegrees { get; init; } = DefaultCameraAzimuthDegrees;

    /// <summary>注視点を怪獣の中心から何メートル上へずらすか（0 で中心をそのまま見る）。</summary>
    public float CameraLookAtHeight { get; init; } = DefaultCameraLookAtHeight;

    /// <summary>カメラ補間の速さ（1 秒あたりの収束率。大きいほど速く寄る）。</summary>
    public float CameraLerpRate { get; init; } = DefaultCameraLerpRate;

    /// <summary>
    /// 演出中だけ使うカメラの画角（度）。
    /// <see cref="FieldOfViewUnspecified"/>（0）なら画角には触らない。
    /// カメラ目標アクタに Camera が付いていれば、そちらの画角が優先される。
    /// </summary>
    public float FieldOfViewDegrees { get; init; } = FieldOfViewUnspecified;

    /// <summary>
    /// カメラ目標アクタ（任意）。有効なら方位角・距離・高さの計算を行わず、
    /// このアクタの位置・回転（＋Camera があればその画角）へ補間する
    /// （会話カメラ <c>DialogueCameraDirector</c> と同じ流儀）。
    /// </summary>
    public SEED.Transform CameraTarget { get; init; }

    // ─── 派生値（計算で毎回書かないための読み取り専用プロパティ）───

    /// <summary>待機位置の垂直オフセット（水面からの相対。常に負）。</summary>
    public float SubmergedOffsetY => -SubmergedDepth;

    /// <summary>
    /// 放物線の振れ幅（待機位置から頂点までの高さ）。
    /// 待機位置は水面より <see cref="SubmergedDepth"/> だけ下なので、
    /// 水面から <see cref="JumpApexHeight"/> の高さへ届かせるにはこの合計が要る。
    /// </summary>
    public float JumpHeightAmplitude => JumpApexHeight + SubmergedDepth;

    /// <summary>
    /// この演出がカメラの主導権を握るか
    /// （<see cref="CutsceneCameraMode.FollowNormal"/> 以外なら握る）。
    /// 握らないときは通常の追従（CameraMove）に任せ、位置・回転・画角のどれにも触らない。
    /// </summary>
    public bool UsesCameraControl => CameraMode != CutsceneCameraMode.FollowNormal;

    /// <summary>カメラ目標アクタが設定されているか。</summary>
    public bool HasCameraTarget => CameraTarget.IsValid;

    /// <summary>画角の指定があるか（<see cref="FieldOfViewUnspecified"/> 以外か）。</summary>
    public bool HasFieldOfViewOverride => FieldOfViewDegrees > FieldOfViewUnspecified;

    // ─── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// ミッションデータから調整値を読み出す【データ → 演出パラメータの唯一の変換点】。
    ///
    /// 大きさとして 0 以下があり得ない項目（高さ・距離・深さ・収束率）は
    /// 0 以下なら既定値へ寄せる。角度・画角・高さの上乗せは 0 も意味を持つ値なので
    /// 補正せずそのまま使う。
    /// </summary>
    /// <param name="data">このミッションの設定データ。</param>
    /// <returns>そのまま演出に使える調整値。</returns>
    public static CutsceneSettings FromMission(TutorialMission data) => new()
    {
        CameraMode  = data.cutsceneCameraMode,
        SpawnAnchor = data.cutsceneSpawnAnchor,

        CameraViewDistance   = PositiveOr(data.cutsceneCameraViewDistance, DefaultCameraViewDistance),
        CameraViewLateral    = data.cutsceneCameraViewLateral,
        JumpDirectionDegrees = data.cutsceneJumpDirectionDegrees,

        JumpApexHeight     = PositiveOr(data.cutsceneJumpApexHeight,     DefaultJumpApexHeight),
        JumpTravelDistance = PositiveOr(data.cutsceneJumpTravelDistance, DefaultJumpTravelDistance),
        SubmergedDepth     = PositiveOr(data.cutsceneSubmergedDepth,     DefaultSubmergedDepth),
        YawOffsetDegrees   = data.cutsceneYawOffsetDegrees,
        PitchSweepDegrees  = data.cutscenePitchSweepDegrees,

        CameraDistance       = PositiveOr(data.cutsceneCameraDistance,  DefaultCameraDistance),
        CameraLerpRate       = PositiveOr(data.cutsceneCameraLerpRate,  DefaultCameraLerpRate),
        CameraHeight         = data.cutsceneCameraHeight,
        CameraAzimuthDegrees = data.cutsceneCameraAzimuthDegrees,
        CameraLookAtHeight   = data.cutsceneCameraLookAtHeight,
        FieldOfViewDegrees   = data.cutsceneCameraFieldOfView,
        CameraTarget         = data.cutsceneCameraTarget,
    };

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 正の値ならそのまま、0 以下なら既定値を返す
    /// 【「未設定は既定へ」の唯一の判定】。
    /// </summary>
    /// <param name="value">データに入っていた値。</param>
    /// <param name="fallback">0 以下だったときに使う既定値。</param>
    /// <returns>演出に使う値。</returns>
    private static float PositiveOr(float value, float fallback) => value > 0f ? value : fallback;
}
