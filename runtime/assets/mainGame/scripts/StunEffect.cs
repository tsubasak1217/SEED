using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 「スタン（気絶）」の星がぐるぐる回る演出を司るスクリプト【この演出の唯一の実装】。
///
/// <b>専用の空アクタ「StunEffect」に付ける</b>。星そのものは子アクタ
/// （<c>StunStar0</c> / <c>StunStar1</c> / <c>StunStar2</c> …）で、
/// それぞれの <see cref="SEED.Transform"/> を <see cref="stars"/> に、
/// <see cref="SEED.Model"/> を <see cref="starModels"/> に同じ並び順で割り当てる。
///
/// 呼び出し側（<c>FishingController</c>）は次の 3 本だけを使う:
/// <list type="bullet">
///   <item><see cref="Show"/> … 演出を開始し、以後その位置を中心に星が回る</item>
///   <item><see cref="SetAnchor"/> … 中心（追従先）を毎フレーム置き直す</item>
///   <item><see cref="Stop"/> … 演出を畳んで星を隠し、格納位置へ落とす</item>
/// </list>
///
/// 星の配置は<b>ワールド座標</b>で直接書き込む（親子のローカル変換に依存しない）ため、
/// 中心（アンカー）がどこにあっても素直に追従する。
/// </summary>
public class StunEffect : SEEDScript
{
    // ─── 定数（マジックナンバーを置かないための名前付き定数） ───────────

    /// <summary>1 周の角度（度）。星を等間隔に配るときの分子。</summary>
    private const float FullCircleDegrees = 360f;

    /// <summary>2π。上下の揺れ（bob）の周波数[Hz]を角速度[rad/s]へ直すのに使う。</summary>
    private const float TwoPi = SEED.Mathf.PI * 2f;

    /// <summary>星が 1 つも割り当てられていないときの安全値（0 除算・0 割りの回避）。</summary>
    private const int MinStarCount = 1;

    // ─── 参照（シーン上のアクタ割り当て） ──────────────────────

    /// <summary>
    /// 星の <see cref="SEED.Transform"/>（<c>StunStar0</c>… を順に割り当てる）。
    /// 位置はこのスクリプトがワールド座標で毎フレーム上書きする。
    /// </summary>
    [Header("参照"), SerializeField(Label = "星のトランスフォーム")]
    private List<SEED.Transform> stars = new();

    /// <summary>
    /// 星の <see cref="SEED.Model"/>（<see cref="stars"/> と同じ並び順）。
    /// 表示・非表示（<see cref="SEED.Model.Visible"/>）の切り替えにだけ使う。
    /// </summary>
    [SerializeField(Label = "星の Model")]
    private List<SEED.Model> starModels = new();

    // ─── 見た目のパラメータ（すべて Inspector から調整可能） ─────────

    /// <summary>星が回る円の半径（m）。中心（アンカー）からの水平距離。</summary>
    [Header("軌道"), SerializeField(Label = "回転半径(m)")]
    private float orbitRadius = 0.6f;

    /// <summary>星の高さ（m）。アンカーからの相対で、上向きが正。</summary>
    [SerializeField(Label = "アンカーからの高さ(m)")]
    private float orbitHeight = 0.15f;

    /// <summary>回る速さ（度/秒）。正で反時計回り（上から見て）。</summary>
    [SerializeField(Label = "回転速度(度/秒)")]
    private float orbitSpeedDegPerSec = 240f;

    /// <summary>上下の揺れ幅（m）。0 で揺れなし。</summary>
    [SerializeField(Label = "上下の揺れ幅(m)")]
    private float bobAmplitude = 0.05f;

    /// <summary>上下の揺れの周期（Hz）。1 秒あたりの往復回数。</summary>
    [SerializeField(Label = "上下の揺れ周期(Hz)")]
    private float bobFrequency = 2f;

    /// <summary>星アクタの大きさ（<see cref="SEED.Transform.Scale"/> に等倍で入れる）。</summary>
    [SerializeField(Label = "星の大きさ")]
    private float starScale = 0.25f;

    /// <summary>非表示のときに星を落としておく Y 座標（視界外へ逃がす）。</summary>
    [SerializeField(Label = "格納位置Y")]
    private float parkPositionY = -100f;

    // ─── 効果音（すべて Inspector から調整可能） ─────────────────

    /// <summary>
    /// 演出を開始した瞬間に鳴らす効果音のアセットパス。空文字なら鳴らさない。
    /// </summary>
    [Header("効果音"), SerializeField(Label = "スタンの効果音")]
    private string stunSePath = "assets://mainGame/audios/stan.mp3";

    /// <summary>スタン効果音の音量（0〜1）。</summary>
    [SerializeField(Label = "スタンの音量")]
    private float stunSeVolume = 1f;

    // ─── 内部状態 ─────────────────────────────────────

    /// <summary>演出中か。<see cref="Show"/> で true、<see cref="Stop"/> で false。</summary>
    private bool showing = false;

    /// <summary>回転の中心（ワールド座標）。<see cref="SetAnchor"/> で置き直される。</summary>
    private SEED.Vector3 anchorPosition = SEED.Vector3.Zero;

    /// <summary>現在の回転角（度）。毎フレーム <see cref="orbitSpeedDegPerSec"/> ぶん進む。</summary>
    private float orbitAngleDegrees = 0f;

    /// <summary>演出開始からの経過秒。上下の揺れ（bob）の位相に使う。</summary>
    private float elapsedSeconds = 0f;

    /// <summary>演出中かどうかの読み取り（呼び出し側の二重再生防止などに使える）。</summary>
    public bool IsShowing => showing;

    // ─── ライフサイクル ────────────────────────────────

    /// <summary>開始時は必ず畳んだ状態にする（シーン上の置き忘れを実行と同時に隠す）。</summary>
    public override void OnStart()
    {
        Stop();
    }

    /// <summary>
    /// 毎フレームの更新。演出中のときだけ角度・位相を進めて星を配置し直す。
    /// 停止中は何もしない（<see cref="Stop"/> で既に隠して格納済み）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!showing) { return; }

        // 角度は 1 周でラップさせて桁落ちを防ぐ
        orbitAngleDegrees += orbitSpeedDegPerSec * ctx.DeltaTime;
        if (orbitAngleDegrees >= FullCircleDegrees || orbitAngleDegrees <= -FullCircleDegrees)
        {
            orbitAngleDegrees %= FullCircleDegrees;
        }

        elapsedSeconds += ctx.DeltaTime;

        ApplyStarLayout();
    }

    // ─── 公開 API ─────────────────────────────────────

    /// <summary>
    /// 演出を開始する【この演出の唯一の入口】。
    /// 角度・位相を初期化してから、その場で 1 度配置まで済ませる
    /// （呼ばれた瞬間から正しい位置に星が出るようにするため）。
    /// すでに演出中（<see cref="showing"/> が true）のときは、
    /// 中心を置き直すだけで効果音は鳴らさない（毎フレーム呼ばれても暴発しないように）。
    /// </summary>
    /// <param name="anchor">回転の中心となるワールド座標（例: 魚の頭上）。</param>
    public void Show(SEED.Vector3 anchor)
    {
        bool wasShowing = showing;

        anchorPosition = anchor;
        orbitAngleDegrees = 0f;
        elapsedSeconds = 0f;
        showing = true;
        ApplyStarLayout();

        // 「畳んでいた → 出す」の立ち上がりでだけ鳴らす（空文字なら無音を許容）
        if (!wasShowing && !string.IsNullOrEmpty(stunSePath))
        {
            SEED.Audio.Play(stunSePath, stunSeVolume);
        }
    }

    /// <summary>
    /// 回転の中心を置き直す（演出中に追従先が動く場合、毎フレーム呼ぶ）。
    /// 停止中に呼んでも位置だけ控えるので副作用はない。
    /// </summary>
    /// <param name="anchor">新しい中心のワールド座標。</param>
    public void SetAnchor(SEED.Vector3 anchor)
    {
        anchorPosition = anchor;
    }

    /// <summary>
    /// 演出を畳む【この演出の唯一の出口】。星を非表示にし、格納位置へ落とす。
    /// 何度呼んでも安全（停止中に呼んでも同じ状態になる）。
    /// </summary>
    public void Stop()
    {
        showing = false;

        for (int i = 0; i < stars.Count; i++)
        {
            if (stars[i] is { IsValid: true } starTf)
            {
                starTf.Position = new SEED.Vector3(starTf.Position.x, parkPositionY, starTf.Position.z);
            }
        }

        for (int i = 0; i < starModels.Count; i++)
        {
            if (starModels[i] is { IsValid: true } model) { model.Visible = false; }
        }
    }

    // ─── 配置の実装 ────────────────────────────────────

    /// <summary>
    /// 星をアンカー周りの円周上へ等間隔に並べる【星の座標を決める唯一の場所】。
    ///
    /// <code>
    /// 位相 a_i   = orbitAngleDegrees + i × (360 / 星の数)
    /// 位置       = anchor + (cos a_i × 半径, 高さ + 揺れ, sin a_i × 半径)
    /// 揺れ       = sin(2π × 周期Hz × 経過秒 + a_i) × 揺れ幅
    /// </code>
    ///
    /// 位置は<b>ワールド座標</b>で書き込む（子アクタでも親の変換に引きずられない）。
    /// </summary>
    private void ApplyStarLayout()
    {
        // 星が 0 個でも 0 除算しないよう下限を噛ませる
        int count = SEED.Mathf.Max(stars.Count, MinStarCount);
        float stepDegrees = FullCircleDegrees / count;

        for (int i = 0; i < stars.Count; i++)
        {
            if (stars[i] is not { IsValid: true } starTf) { continue; }

            // この星の位相（度 → ラジアン）
            float phaseDegrees = orbitAngleDegrees + stepDegrees * i;
            float phaseRadians = phaseDegrees * SEED.Mathf.Deg2Rad;

            // 上下の揺れ（星ごとに位相をずらして、そろって上下しないようにする）
            float bob = SEED.Mathf.Sin(TwoPi * bobFrequency * elapsedSeconds + phaseRadians) * bobAmplitude;

            starTf.Position = new SEED.Vector3(
                anchorPosition.x + SEED.Mathf.Cos(phaseRadians) * orbitRadius,
                anchorPosition.y + orbitHeight + bob,
                anchorPosition.z + SEED.Mathf.Sin(phaseRadians) * orbitRadius);

            // 大きさは毎フレーム引き直す（Inspector での調整が即座に効くように）
            starTf.Scale = new SEED.Vector3(starScale, starScale, starScale);
        }

        // 表示は配置と同じタイミングで ON にする（1 フレーム古い位置で出るのを防ぐ）
        for (int i = 0; i < starModels.Count; i++)
        {
            if (starModels[i] is { IsValid: true } model) { model.Visible = true; }
        }
    }
}
