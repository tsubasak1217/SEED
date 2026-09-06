using System.Collections.Generic;
using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// レーダーに 1 点を表示するための入力データ（点 1 個ぶん）。
///
/// レーダーは「誰を出すか」「どんな色で出すか」を<b>一切判断しない</b>。
/// 判断は呼び出し側（<see cref="FishingController"/>）が行い、その結果
/// （位置・色・不透明度）だけをこの構造体で渡す（表示と判断の責務分離）。
///
/// 色の<b>値そのもの</b>は <see cref="FishRadar"/> のインスペクタが持ち
/// （<see cref="FishRadar.CatchableColor"/> / <see cref="FishRadar.DriftColorOf"/> など）、
/// 呼び出し側はそれを読んで詰めるだけ ―― パレットの唯一の置き場をレーダー側に保つ。
/// </summary>
public readonly struct RadarEntry
{
    /// <summary>この点のワールド座標（XZ だけ使う。Y は無視される）。</summary>
    public readonly SEED.Vector3 position;

    /// <summary>点の色（RGB）。魚も漂流物もこの値がそのまま描かれる。</summary>
    public readonly SEED.Vector3 color;

    /// <summary>点の不透明度（0〜1）。魚の「釣れない個体」だけ薄くする使い方をしている。</summary>
    public readonly float alpha;

    /// <summary>各値を指定して 1 点ぶんの入力を作る。</summary>
    /// <param name="position">ワールド座標。</param>
    /// <param name="color">点の色（RGB）。</param>
    /// <param name="alpha">点の不透明度（0〜1）。</param>
    public RadarEntry(SEED.Vector3 position, SEED.Vector3 color, float alpha)
    {
        this.position = position;
        this.color = color;
        this.alpha = alpha;
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
/// [描画方式]（2026-09-07 改定：スプライト全廃）
/// 背景の円・外周リング・中心点・魚の点は<b>すべてアクタを持たない</b>。
/// 毎フレーム <see cref="SEED.Draw"/> のプリミティブとして直接描く（イミディエイトモード）。
/// 以前は背景テクスチャ（<c>radar_bg.png</c>）と中心点スプライト、さらに固定 24 枚の
/// 点スプライトを使っていたため<b>同時表示 24 個の上限</b>とアクタ管理の手間があったが、
/// プリミティブ描画に移したことで上限が無くなり、レーダーに要るアクタは
/// <b>座標空間としての本体 1 つだけ</b>になった（Sprite すら持たない）。
///
/// [座標空間]
/// 点は <see cref="radarSpace"/>（＝レーダー本体アクタ自身の CanvasTransform）の
/// <b>ローカル空間</b>へ描く。この空間は「そのアクタの子としてスプライトを置いたとき」と
/// まったく同じ変換連鎖（アンカー・親スケール・自動解像度）を通るので、
/// 本体アクタを動かせば背景の円も点もそのまま追従する。
/// 中心（＝ウキ）はローカル原点であり、点の位置は中心からのオフセット px そのものになる。
///
/// [ピボット補正]
/// エンジンのローカル空間行列（<c>CanvasTransform::to_mesh_mat4</c>）では
/// pivot が「正規化値 × 追加スケール」＝ 実質<b>ピクセル値</b>として平行移動に効く。
/// そのため pivot(0.5,0.5) のアクタではローカル原点が中心から 0.5px ずれる。
/// 描画時に pivot 分を足し戻して打ち消している（<see cref="SpaceOriginPx"/>）。
///
/// [描画タイミング]
/// 点を積むのは <see cref="LateUpdate"/> の 1 か所だけ。
/// <see cref="UpdateRadar"/> は「何を出すか」を控えるだけで描かない
/// （イミディエイトモードでは 1 フレームに 2 回積むと同じ図形が二重に描かれるため、
///   全スクリプトの Update が終わった後の LateUpdate を唯一の描画点にする）。
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
    /// レーダーを描く座標空間（＝レーダー本体アクタ自身の CanvasTransform）
    /// 【レーダーの基準の唯一の置き場】。
    /// この空間のローカル原点がレーダーの中心になり、背景の円も点も
    /// そこからの px オフセットで置かれる。
    /// 未設定のときは何も描かない（スクリーン座標へ落とすと自動解像度・
    /// アンカーが効かず、まったく別の場所に出てしまうため）。
    /// </summary>
    [Header("参照"), SerializeField(Label = "レーダーの座標空間(CanvasTransform)")]
    private SEED.CanvasTransform? radarSpace = null;

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

    /// <summary>
    /// 点の半径（ピクセル）。旧 <c>RadarDot**</c> スプライトは 10×10 px だったので
    /// 既定はその半分の 5（＝見た目の大きさを従来どおりに保つ）。
    /// </summary>
    [SerializeField(Label = "点の半径(px)")]
    private float dotRadiusPx = 5f;

    /// <summary>
    /// 点の描画レイヤー（大きいほど手前）。背景スプライトのレイヤー（シーン設定は 18）より
    /// 大きい値にすること。旧 <c>RadarDot**</c> スプライトと同じ 19 が既定。
    /// </summary>
    [SerializeField(Label = "点のレイヤー")]
    private int dotLayer = 19;

    /// <summary>
    /// 背景の円の半径（ピクセル）。旧背景スプライト（radar_bg.png）は 180×180 px
    /// だったので、既定はその半分の 90（＝見た目の大きさを従来どおりに保つ）。
    /// 点が動く範囲（<see cref="radarRadiusPx"/>）より少し大きいのが自然。
    /// </summary>
    [SerializeField(Label = "背景の円の半径(px)")]
    private float bgRadiusPx = 90f;

    /// <summary>背景の円の色（RGB）。既定は暗い青緑（旧背景テクスチャの地色に相当）。</summary>
    [SerializeField(Label = "背景の色(RGB)")]
    private SEED.Vector3 bgColor = new(0.04f, 0.09f, 0.07f);

    /// <summary>背景の円の描画レイヤー（点・リングより奥＝小さい値にすること）。</summary>
    [SerializeField(Label = "背景のレイヤー")]
    private int bgLayer = 17;

    /// <summary>中心点（＝ウキ＝自分）の半径（ピクセル）。旧中心点スプライトは 8×8 px。</summary>
    [SerializeField(Label = "中心点の半径(px)")]
    private float centerDotRadiusPx = 4f;

    /// <summary>
    /// レーダーの外周リングを描くか。背景テクスチャを廃したので、
    /// 円枠はこのリングが担う（既定は true）。
    /// </summary>
    [SerializeField(Label = "外周リングを描く")]
    private bool drawRimRing = true;

    /// <summary>外周リングの太さ（ピクセル・半径方向。背景の円の外周に沿って描く）。</summary>
    [SerializeField(Label = "外周リングの太さ(px)")]
    private float rimRingThicknessPx = 2f;

    /// <summary>外周リングの色（RGB）。</summary>
    [SerializeField(Label = "外周リングの色(RGB)")]
    private SEED.Vector3 rimRingColor = new(0.72f, 1f, 0.24f);

    /// <summary>外周リングの不透明度（フェード値が掛かる）。</summary>
    [SerializeField(Label = "外周リングの不透明度")]
    private float rimRingOpacity = 0.8f;

    /// <summary>外周リングの描画レイヤー（点より奥＝小さい値にすること）。</summary>
    [SerializeField(Label = "外周リングのレイヤー")]
    private int rimRingLayer = 18;

    /// <summary>釣れる（乗り換えできる）個体の点の色（RGB）。</summary>
    [SerializeField(Label = "釣れる個体の色(RGB)")]
    private SEED.Vector3 catchableColor = new(0.72f, 1f, 0.24f);

    /// <summary>釣れない個体の点の不透明度（色は釣れる個体と同じで、薄さだけで区別する）。</summary>
    [SerializeField(Label = "釣れない個体の不透明度")]
    private float uncatchableAlpha = 0.35f;

    /// <summary>漂流物「糸の回復」（<see cref="DriftItem.KindLineRecover"/>）の点の色（RGB）。</summary>
    [SerializeField(Label = "漂流物の色(糸回復)")]
    private SEED.Vector3 driftLineRecoverColor = new(1f, 0.45f, 0.8f);

    /// <summary>漂流物「ひるませ（スタン）」（<see cref="DriftItem.KindStun"/>）の点の色（RGB）。</summary>
    [SerializeField(Label = "漂流物の色(ひるませ)")]
    private SEED.Vector3 driftStunColor = new(1f, 0.95f, 0.3f);

    /// <summary>漂流物「魚HPの回復」（<see cref="DriftItem.KindFishRecover"/>）の点の色（RGB）。</summary>
    [SerializeField(Label = "漂流物の色(魚回復)")]
    private SEED.Vector3 driftFishRecoverColor = new(1f, 0.6f, 0.2f);

    /// <summary>背景の円の不透明度（表示中）。</summary>
    [SerializeField(Label = "背景の不透明度")]
    private float bgOpacity = 0.9f;

    /// <summary>
    /// 表示／非表示を切り替えるときのフェード秒数（0 なら即座に切り替わる）。
    /// <see cref="Show"/> / <see cref="Hide"/> は目標値を変えるだけで、
    /// 実際の不透明度はこの秒数をかけて <see cref="Update"/> が近づける。
    /// </summary>
    [SerializeField(Label = "フェード秒数")]
    private float radarFadeSeconds = 0.5f;

    // ─── 内部状態 ─────────────────────────────────────────────

    /// <summary>いま表示中か（<see cref="Show"/> / <see cref="Hide"/> が唯一の切替点）。</summary>
    private bool visible = false;

    /// <summary>
    /// フェードの現在値（0＝完全に消えている／1＝設定どおりの濃さ）。
    /// 描画するすべての不透明度にこの値を掛ける。
    /// </summary>
    private float fade01 = 0f;

    /// <summary>フェードの目標値（<see cref="Show"/> で 1・<see cref="Hide"/> で 0）。</summary>
    private float fadeTarget = 0f;

    /// <summary>
    /// 直近に <see cref="UpdateRadar"/> へ渡された点の控え。
    /// フェードアウト中は呼び出し側が点を渡してこないので、
    /// この控えを使って「同じ絵を薄くしていく」ために保持する。
    /// </summary>
    private readonly List<RadarEntry> cachedEntries = new();

    /// <summary>直近に渡されたレーダー中心（ワールド座標）。</summary>
    private SEED.Vector3 cachedCenter = SEED.Vector3.Zero;

    /// <summary>直近に渡された「上向きに合わせる方位角」（度）。</summary>
    private float cachedForwardYawDeg = 0f;

    /// <summary>
    /// レーダーが映す実距離（メートル）。呼び出し側が「載せる魚」を絞るのに使う。
    /// </summary>
    public float RangeMeters => radarRangeMeters;

    /// <summary>釣れない個体の点の不透明度（外部から確認できるように公開する）。</summary>
    public float UncatchableAlpha => uncatchableAlpha;

    /// <summary>釣れる（乗り換えできる）個体の点の色（RGB）。呼び出し側が <see cref="RadarEntry"/> へ詰める。</summary>
    public SEED.Vector3 CatchableColor => catchableColor;

    /// <summary>完全に不透明な値（点を濃く出すときに呼び出し側が使う）。</summary>
    public float OpaqueDotAlpha => OpaqueAlpha;

    /// <summary>
    /// 漂流物の種類（<see cref="DriftItem.KindStun"/> など）に対応する点の色（RGB）
    /// 【漂流物のレーダー色の唯一の対応表】。未知の種類は釣れる個体の色で描く。
    /// </summary>
    /// <param name="kind">漂流物の種類文字列。</param>
    public SEED.Vector3 DriftColorOf(string kind) => kind switch
    {
        DriftItem.KindLineRecover => driftLineRecoverColor,
        DriftItem.KindStun => driftStunColor,
        DriftItem.KindFishRecover => driftFishRecoverColor,
        _ => catchableColor,
    };

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
    /// 毎フレームの更新【フェードを進める唯一の場所】。
    ///
    /// 「何を出すか」は呼び出し側（<see cref="FishingController"/>）が
    /// <see cref="UpdateRadar"/> で決めるが、<b>フェードの進行だけは自前で持つ</b>
    /// （フェードアウト中は呼び出し側が点を渡してこないため、
    ///   控えた内容（<see cref="cachedEntries"/>）を薄くしながら描き続ける必要がある）。
    ///
    /// ここでは<b>描画しない</b>。プリミティブを積むのは <see cref="LateUpdate"/> だけ
    /// （呼び出し側の Update より先に描くと 1 フレーム古い内容になり、
    ///   Update と <see cref="UpdateRadar"/> の両方で描くと二重に積まれるため）。
    /// </summary>
    /// <param name="ctx">フレーム情報（経過秒数を読む）。</param>
    public override void Update(ref NativeFrameContext ctx)
    {
        AdvanceFade(ctx.DeltaTime);

        // 完全に消えたら描画をやめる（プリミティブなので描かなければ消える）
        if (fade01 <= HiddenAlpha)
        {
            visible = false;
            return;
        }

        visible = true;
    }

    /// <summary>
    /// フェードの現在値を目標値へ近づける。
    /// フェード秒数が 0 以下なら即座に目標値へ飛ぶ（フェード無しの設定）。
    /// </summary>
    /// <param name="deltaTime">このフレームの経過秒数。</param>
    private void AdvanceFade(float deltaTime)
    {
        if (radarFadeSeconds <= MinPositiveValue || deltaTime <= 0f)
        {
            fade01 = fadeTarget;
            return;
        }

        float step = deltaTime / radarFadeSeconds;
        fade01 = fade01 < fadeTarget
            ? SEED.Mathf.Min(fade01 + step, fadeTarget)
            : SEED.Mathf.Max(fade01 - step, fadeTarget);
    }

    /// <summary>固定タイムステップの更新。</summary>
    public override void ConstantUpdate(ref NativeFrameContext ctx)
    {
    }

    /// <summary>
    /// Update 後の更新【レーダーを描く唯一の場所】。
    ///
    /// すべてのスクリプトの <c>Update</c>（＝ <see cref="FishingController"/> による
    /// <see cref="UpdateRadar"/> の呼び出し）が終わってから走るので、
    /// ここで描けば「最新の内容を 1 フレームに 1 回だけ」積める。
    /// </summary>
    /// <param name="ctx">フレーム情報（未使用）。</param>
    public override void LateUpdate(ref NativeFrameContext ctx)
    {
        Draw();
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
        fadeTarget = OpaqueAlpha;
        visible = true;
    }

    /// <summary>
    /// レーダーを隠す（フェードし切ったところで描くのをやめる）。
    /// 位置は動かさないので、次に <see cref="Show"/> しても基準はずれない。
    /// 何度呼んでも副作用は無い（冪等）。
    /// </summary>
    public void Hide()
    {
        fadeTarget = HiddenAlpha;

        // フェードが残っているあいだは描き続ける（実際に隠れるのは Update 側）
        if (fade01 > HiddenAlpha) { return; }

        visible = false;
    }

    /// <summary>
    /// レーダーをフェードなしで即座に隠す【キャンセル・釣り上げ時の即時消去】。
    /// 次に <see cref="Show"/> したときは 0 からのフェードインになる。
    /// </summary>
    public void HideImmediate()
    {
        fadeTarget = HiddenAlpha;
        fade01 = HiddenAlpha;
        visible = false;
    }

    /// <summary>
    /// レーダーの表示内容を更新する【表示内容の唯一の受け口】。
    ///
    /// ここでは<b>控えるだけ</b>で描かない（実際の描画は <see cref="LateUpdate"/>）。
    /// 点の数に上限は無い（プリミティブ描画なので枠の枚数に縛られない）。
    /// </summary>
    /// <param name="center">レーダーの中心にするワールド座標（＝ウキの位置）。</param>
    /// <param name="forwardYawDeg">上向きに合わせる方位角（度）。プレイヤーの正面。</param>
    /// <param name="entries">表示する点（呼び出し側が絞り込んだ結果）。</param>
    public void UpdateRadar(SEED.Vector3 center, float forwardYawDeg, IReadOnlyList<RadarEntry> entries)
    {
        // 渡された内容は必ず控える（フェードアウト中に描き直すため）
        cachedCenter = center;
        cachedForwardYawDeg = forwardYawDeg;
        cachedEntries.Clear();
        for (int i = 0; i < entries.Count; i++) { cachedEntries.Add(entries[i]); }
    }

    /// <summary>
    /// 控えた内容（<see cref="cachedEntries"/>）を現在のフェード値で描く
    /// 【点の配置と色の唯一の書き込み点】。
    /// 完全に消えている（<see cref="fade01"/> が 0）ときは何も描かない。
    ///
    /// 点は <see cref="SEED.Draw.Circle"/> のイミディエイト描画なので<b>個数の上限は無い</b>。
    /// 座標は <see cref="radarSpace"/>（背景アクタ）のローカル空間で、
    /// 原点＝レーダー中心・単位＝キャンバス px・Y は下向き。
    /// </summary>
    private void Draw()
    {
        if (!visible || fade01 <= HiddenAlpha) { return; }

        var center = cachedCenter;
        float forwardYawDeg = cachedForwardYawDeg;

        // 描く座標空間。未設定ならここで打ち切る（誤った場所へ描かないため）。
        if (radarSpace is not { IsValid: true } space) { return; }
        var origin = SpaceOriginPx(space);

        // 背景の円（一番奥）。テクスチャではなく塗り円で描く。
        SEED.Draw.Circle(
            origin,
            SEED.Mathf.Max(bgRadiusPx, MinPositiveValue),
            ToColor(bgColor, SEED.Mathf.Clamped01(bgOpacity) * fade01),
            layer: bgLayer,
            space: space);

        // メートル → ピクセルの換算係数。射程 0 の設定ミスでも 0 除算しない。
        float range = SEED.Mathf.Max(radarRangeMeters, MinPositiveValue);
        float radius = SEED.Mathf.Max(radarRadiusPx, MinPositiveValue);
        float metersToPixels = radius / range;

        // 外周リング（円枠）。背景の円の外周に沿って描く。
        if (drawRimRing)
        {
            float rimOuter = SEED.Mathf.Max(bgRadiusPx, MinPositiveValue);
            float thickness = SEED.Mathf.Max(rimRingThicknessPx, MinPositiveValue);
            SEED.Draw.Ring(
                origin,
                SEED.Mathf.Max(rimOuter - thickness, 0f),
                rimOuter,
                ToColor(rimRingColor, SEED.Mathf.Clamped01(rimRingOpacity) * fade01),
                layer: rimRingLayer,
                space: space);
        }

        // 中心点（＝ウキ＝自分）。常にレーダーの中心に置く。
        SEED.Draw.Circle(
            origin,
            SEED.Mathf.Max(centerDotRadiusPx, MinPositiveValue),
            ToColor(catchableColor, OpaqueAlpha * fade01),
            layer: dotLayer,
            space: space);

        // エンジン規約: yaw = atan2(x, z)、前方 +Z。
        // forward = (sin, cos)、right（forward を右へ 90° 回した方向）= (cos, -sin)。
        float yawRad = forwardYawDeg * DegToRad;
        float forwardX = SEED.Mathf.Sin(yawRad);
        float forwardZ = SEED.Mathf.Cos(yawRad);
        float rightX = forwardZ;
        float rightZ = -forwardX;

        for (int i = 0; i < cachedEntries.Count; i++)
        {
            var e = cachedEntries[i];

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

            // 色と不透明度は呼び出し側が決めた値をそのまま描く（レーダーは判断しない）
            SEED.Draw.Circle(
                new SEED.Vector2(origin.x + px, origin.y + py),
                SEED.Mathf.Max(dotRadiusPx, MinPositiveValue),
                ToColor(e.color, SEED.Mathf.Clamped01(e.alpha) * fade01),
                layer: dotLayer,
                space: space);
        }
    }

    // ─── 内部処理 ─────────────────────────────────────────────

    /// <summary>
    /// 指定した座標空間における「レーダー中心」のローカル座標。
    ///
    /// エンジンのローカル空間行列（<c>CanvasTransform::to_mesh_mat4</c>）は
    /// pivot をピクセル値として平行移動へ効かせるため、ローカル原点は
    /// アクタ位置から pivot 分だけずれる。pivot を足し戻すと
    /// 「アクタ位置＝レーダー中心」に一致する（pivot(0.5,0.5) なら 0.5px の補正）。
    /// </summary>
    /// <param name="space">点を描く座標空間。</param>
    private static SEED.Vector2 SpaceOriginPx(SEED.CanvasTransform space) => space.Pivot;

    /// <summary>RGB（Vector3）と不透明度から描画用の色を作る。</summary>
    /// <param name="rgb">色（RGB・0〜1）。</param>
    /// <param name="alpha">不透明度（0〜1）。</param>
    private static SEED.Color ToColor(SEED.Vector3 rgb, float alpha)
        => new SEED.Color(rgb.x, rgb.y, rgb.z, alpha);

}
