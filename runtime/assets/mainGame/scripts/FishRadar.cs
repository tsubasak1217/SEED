using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// レーダーに 1 点を表示するための入力データ（1 匹ぶん）。
///
/// レーダーは「誰を出すか」「釣れるか」を<b>一切判断しない</b>。
/// 判断は呼び出し側（<see cref="FishingController"/>）が行い、その結果だけを
/// この構造体で渡す（表示と判断の責務分離）。
/// </summary>
public readonly struct RadarEntry
{
    /// <summary>この点のワールド座標（XZ だけ使う。Y は無視される）。</summary>
    public readonly SEED.Vector3 position;

    /// <summary>
    /// 「いま掛かっている魚を食べられる＝乗り換えで釣れる」個体か。
    /// true なら不透明、false なら半透明（<see cref="FishRadar.UncatchableAlpha"/>）で描く。
    /// </summary>
    public readonly bool catchable;

    /// <summary>各値を指定して 1 点ぶんの入力を作る。</summary>
    /// <param name="position">ワールド座標。</param>
    /// <param name="catchable">乗り換えで釣れる個体か。</param>
    public RadarEntry(SEED.Vector3 position, bool catchable)
    {
        this.position = position;
        this.catchable = catchable;
    }
}

/// <summary>
/// ヒット中に画面左上へ出す<b>魚レーダー</b>（表示専用スクリプト）。
///
/// [責務]
/// このスクリプトは<b>与えられた点を描くだけ</b>である。
/// 「どの魚を載せるか」「釣れるか（不透明／半透明）」の判断は
/// <see cref="FishingController"/> が行い、<see cref="RadarEntry"/> の列として渡す。
/// レーダー側は魚にもレベルにも一切依存しない（単一責任原則）。
///
/// [座標変換]（<see cref="UpdateRadar"/> が唯一の変換点）
/// <code>
/// rel = 点のワールド位置 − 中心（ウキ）位置        … XZ 平面
/// u   = dot(rel, right)                            … 画面 +X（右）
/// v   = dot(rel, forward)                          … 画面の上（キャンバス Y は下向きなので符号反転）
/// px  = ( u, -v ) × (レーダー半径px ÷ レーダー射程m) … 円外は円周へクランプ
/// </code>
/// forward はプレイヤーの向き（度）から作るので、<b>常にプレイヤーの正面が上</b>になる。
///
/// [配置]
/// 背景・点はすべて釣り UI キャンバス（FishingUI）の<b>直下</b>に平置きし、
/// 点の位置は「背景（<see cref="bgTransform"/>）の位置 ＋ オフセット」で決める。
/// 入れ子（背景の子として点を置く）でも描画自体は成立するが、
/// 子アクタのアンカー基準サイズは「CanvasComponent を持つ最も近い祖先」から取られるため、
/// CanvasComponent を持たない背景アクタの子は基準サイズを失って挙動が読みにくくなる。
/// 既存のテンションゲージ（GaugeSeg**）と同じ<b>平置き</b>に揃えることで、
/// 「アンカーは常に FishingUI 基準」という単純な規則を保っている。
/// レーダーを動かしたいときは背景アクタの位置だけを動かせば点も追従する。
/// </summary>
public class FishRadar : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）───────────────────────────

    /// <summary>度→ラジアン変換係数。</summary>
    private const float DegToRad = 3.14159265f / 180f;

    /// <summary>射程・半径が 0 以下のときに 0 除算を避けるための下限。</summary>
    private const float MinPositiveValue = 1e-4f;

    /// <summary>完全に見えない不透明度（点・背景を隠すときに使う）。</summary>
    private const float HiddenAlpha = 0f;

    /// <summary>完全に不透明な値（釣れる個体の点に使う）。</summary>
    private const float OpaqueAlpha = 1f;

    // ─── 参照（インスペクタで割り当てる）───────────────────────

    /// <summary>
    /// レーダー背景（円）の Sprite。表示／非表示は色のアルファで行う
    /// （アクタを動かして隠す方式は、点の基準位置がずれるので使わない）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "背景のSprite")]
    private SEED.Sprite? bgSprite = null;

    /// <summary>
    /// レーダー背景の CanvasTransform。<b>この位置がレーダーの中心</b>であり、
    /// 各点の位置はここからのオフセットで決まる（読むだけで書き換えない）。
    /// </summary>
    [SerializeField(Label = "背景のCanvasTransform")]
    private SEED.CanvasTransform? bgTransform = null;

    /// <summary>魚を表す点の Sprite（色とアルファを毎フレーム書き換える）。</summary>
    [SerializeField(Label = "点のSprite")]
    private List<SEED.Sprite> dotSprites = new();

    /// <summary>
    /// 魚を表す点の CanvasTransform（位置を毎フレーム書き換える）。
    /// <see cref="dotSprites"/> と<b>同じ順序</b>で並べること（同じ添字が同じ点を指す）。
    /// </summary>
    [SerializeField(Label = "点のCanvasTransform")]
    private List<SEED.CanvasTransform> dotTransforms = new();

    /// <summary>中心（＝ウキ＝自分）を示す点の Sprite。位置は背景の中心に固定。</summary>
    [SerializeField(Label = "中心点のSprite")]
    private SEED.Sprite? centerSprite = null;

    /// <summary>中心点の CanvasTransform（背景の中心へ置き直すために使う）。</summary>
    [SerializeField(Label = "中心点のCanvasTransform")]
    private SEED.CanvasTransform? centerTransform = null;

    // ─── 表示パラメータ ───────────────────────────────────────

    /// <summary>
    /// レーダーが映す実距離（メートル）＝円周までの距離。
    /// これより遠い点は<b>呼び出し側で除外</b>される想定だが、
    /// 万一渡されても円周上へクランプして描く。
    /// </summary>
    [Header("表示"), SerializeField(Label = "レーダー射程(m)")]
    private float radarRangeMeters = 25f;

    /// <summary>レーダー円の半径（ピクセル）。<see cref="radarRangeMeters"/> がこの長さに対応する。</summary>
    [SerializeField(Label = "レーダー半径(px)")]
    private float radarRadiusPx = 80f;

    /// <summary>釣れる（乗り換えできる）個体の点の色（RGB）。</summary>
    [SerializeField(Label = "釣れる個体の色(RGB)")]
    private SEED.Vector3 catchableColor = new(0.72f, 1f, 0.24f);

    /// <summary>釣れない個体の点の不透明度（色は釣れる個体と同じで、薄さだけで区別する）。</summary>
    [SerializeField(Label = "釣れない個体の不透明度")]
    private float uncatchableAlpha = 0.35f;

    /// <summary>レーダー背景の不透明度（表示中）。</summary>
    [SerializeField(Label = "背景の不透明度")]
    private float bgOpacity = 0.9f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>いま表示中か（<see cref="Show"/> / <see cref="Hide"/> が唯一の切替点）。</summary>
    private bool visible = false;

    /// <summary>
    /// 非表示の書き込み（全 Sprite のアルファ 0）が済んでいるか。
    /// <see cref="Hide"/> は毎フレーム呼ばれる想定なので、既に隠し終えているフレームでは
    /// 何も書き込まずに抜けるためのラッチ。
    /// </summary>
    private bool hideApplied = false;

    /// <summary>
    /// レーダーが映す実距離（メートル）。呼び出し側が「載せる魚」を絞るのに使う。
    /// </summary>
    public float RangeMeters => radarRangeMeters;

    /// <summary>釣れない個体の点の不透明度（外部から確認できるように公開する）。</summary>
    public float UncatchableAlpha => uncatchableAlpha;

    // ─── ライフサイクル ───────────────────────────────────────

    /// <summary>生成直後の初期化。シーンの保存状態に依らず必ず非表示から始める。</summary>
    public override void OnStart()
    {
        Hide();
    }

    /// <summary>フレーム開始時に呼ばれる。</summary>
    public override void BeginFrame(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update より前の更新。</summary>
    public override void EarlyUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// 毎フレームの更新。
    /// レーダーは<b>呼び出し側（<see cref="FishingController"/>）の駆動</b>だけで動くので、
    /// ここでは何もしない（表示・非表示も含めて外部 API 経由に一本化する）。
    /// </summary>
    public override void Update(ref NativeFrameContext ctx)
    {
    }

    /// <summary>固定タイムステップの更新。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>Update 後の更新。</summary>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>描画フェーズ。</summary>
    public override void Render(ref NativeFrameContext ctx)
    {
    }

    /// <summary>フレーム終了時に呼ばれる。</summary>
    public override void EndFrame(ref NativeFrameContext ctx)
    {
    }

    // ─── 公開 API ─────────────────────────────────────────────

    /// <summary>
    /// レーダーを表示状態にする（背景と中心点を出す）。
    /// 点そのものは <see cref="UpdateRadar"/> が呼ばれるまで出ない。
    /// 何度呼んでも副作用は無い（冪等）。
    /// </summary>
    public void Show()
    {
        visible = true;
        hideApplied = false;
        SetSpriteAlpha(bgSprite, SEED.Mathf.Clamped01(bgOpacity), SEED.Vector3.One);
        PlaceCenterDot();
    }

    /// <summary>
    /// レーダーを隠す（背景・中心点・すべての魚の点をアルファ 0 にする）。
    /// 位置は動かさないので、次に <see cref="Show"/> しても基準はずれない。
    /// 何度呼んでも副作用は無い（冪等）。
    /// </summary>
    public void Hide()
    {
        // 既に隠し終えているなら書き込まない（非表示のあいだ毎フレーム呼ばれるため）
        if (hideApplied) { visible = false; return; }
        visible = false;
        hideApplied = true;
        SetSpriteAlpha(bgSprite, HiddenAlpha, SEED.Vector3.One);
        SetSpriteAlpha(centerSprite, HiddenAlpha, catchableColor);
        HideDotsFrom(0);
    }

    /// <summary>
    /// レーダーの表示内容を更新する【点の配置と色の唯一の決定点】。
    ///
    /// <see cref="Show"/> していないときは何もしない（表示中だけ動かす）。
    /// 点の数が足りない場合は先頭から埋め、余った点はアルファ 0 で隠す。
    /// </summary>
    /// <param name="center">レーダーの中心にするワールド座標（＝ウキの位置）。</param>
    /// <param name="forwardYawDeg">上向きに合わせる方位角（度）。プレイヤーの正面。</param>
    /// <param name="entries">表示する点（呼び出し側が絞り込んだ結果）。</param>
    public void UpdateRadar(SEED.Vector3 center, float forwardYawDeg, IReadOnlyList<RadarEntry> entries)
    {
        if (!visible) { return; }

        // 背景と中心点は毎フレーム引き直す（背景の位置をエディタで動かしても追従させる）
        SetSpriteAlpha(bgSprite, SEED.Mathf.Clamped01(bgOpacity), SEED.Vector3.One);
        PlaceCenterDot();

        // メートル → ピクセルの換算係数。射程 0 の設定ミスでも 0 除算しない。
        float range = SEED.Mathf.Max(radarRangeMeters, MinPositiveValue);
        float radius = SEED.Mathf.Max(radarRadiusPx, MinPositiveValue);
        float metersToPixels = radius / range;

        // エンジン規約: yaw = atan2(x, z)、前方 +Z。
        // forward = (sin, cos)、right（forward を右へ 90° 回した方向）= (cos, -sin)。
        float yawRad = forwardYawDeg * DegToRad;
        float forwardX = SEED.Mathf.Sin(yawRad);
        float forwardZ = SEED.Mathf.Cos(yawRad);
        float rightX = forwardZ;
        float rightZ = -forwardX;

        var origin = RadarCenterPx();
        int slots = SlotCount();
        int used = 0;

        for (int i = 0; i < entries.Count && used < slots; i++)
        {
            var e = entries[i];

            // 中心からの相対位置（XZ）をプレイヤー基準（正面が上）の座標へ回す
            float relX = e.position.x - center.x;
            float relZ = e.position.z - center.z;
            float u = relX * rightX + relZ * rightZ;       // 右方向の成分
            float v = relX * forwardX + relZ * forwardZ;   // 正面方向の成分

            // ピクセルへ換算（キャンバス Y は下向きなので、正面成分は符号を反転する）
            float px = u * metersToPixels;
            float py = -v * metersToPixels;

            // 円からはみ出す点は円周上へ寄せる（レーダーの外に点が出ないようにする）
            float lengthPx = SEED.Mathf.Sqrt(px * px + py * py);
            if (lengthPx > radius && lengthPx > MinPositiveValue)
            {
                float shrink = radius / lengthPx;
                px *= shrink;
                py *= shrink;
            }

            if (used < dotTransforms.Count && dotTransforms[used] is { IsValid: true } tf)
            {
                tf.Position = new SEED.Vector2(origin.x + px, origin.y + py);
            }
            SetSpriteAlpha(
                used < dotSprites.Count ? dotSprites[used] : null,
                e.catchable ? OpaqueAlpha : SEED.Mathf.Clamped01(uncatchableAlpha),
                catchableColor);
            used++;
        }

        // 使わなかった点は必ず隠す（前フレームの残像が残らないようにする）
        HideDotsFrom(used);
    }

    // ─── 内部処理 ─────────────────────────────────────────────

    /// <summary>
    /// 使える点の数（Sprite と CanvasTransform の<b>少ない方</b>）。
    /// 片方だけ足りていても「位置は動くが見えない」点になるので、両方揃った数だけ使う。
    /// </summary>
    private int SlotCount()
        => dotSprites.Count < dotTransforms.Count ? dotSprites.Count : dotTransforms.Count;

    /// <summary>
    /// レーダー中心のキャンバス座標。背景アクタの位置をそのまま使う
    /// （背景の Sprite は pivot 中央で置く前提なので、位置＝円の中心になる）。
    /// 背景未設定なら原点を返す（点はキャンバス左上付近に固まるが、落ちはしない）。
    /// </summary>
    private SEED.Vector2 RadarCenterPx()
        => bgTransform is { IsValid: true } tf ? tf.Position : SEED.Vector2.Zero;

    /// <summary>中心点（自分＝ウキ）を背景の中心へ置き、表示状態に合わせて出し入れする。</summary>
    private void PlaceCenterDot()
    {
        if (centerTransform is { IsValid: true } tf) { tf.Position = RadarCenterPx(); }
        SetSpriteAlpha(centerSprite, visible ? OpaqueAlpha : HiddenAlpha, catchableColor);
    }

    /// <summary>指定の添字から末尾までの点をすべて隠す。</summary>
    /// <param name="startIndex">隠し始める添字（0 なら全部）。</param>
    private void HideDotsFrom(int startIndex)
    {
        for (int i = startIndex; i < dotSprites.Count; i++)
        {
            SetSpriteAlpha(dotSprites[i], HiddenAlpha, catchableColor);
        }
    }

    /// <summary>
    /// Sprite の色（RGB）と不透明度をまとめて書き込む【色書き込みの唯一の経路】。
    /// null・破棄済みハンドルは黙って無視する（シーン未設定でも落ちない）。
    /// </summary>
    /// <param name="sprite">対象の Sprite。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    /// <param name="rgb">色（RGB）。</param>
    private static void SetSpriteAlpha(SEED.Sprite? sprite, float alpha, SEED.Vector3 rgb)
    {
        if (sprite is not { IsValid: true } s) { return; }
        s.Color = new SEED.Color(rgb.x, rgb.y, rgb.z, alpha);
    }
}
