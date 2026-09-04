using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

/// <summary>
/// 環境音（波音などの BGM/アンビエンス）を<b>継ぎ目なくループ再生</b>するコントローラ。
///
/// <b>付ける場所</b>: <c>AudioSource</c>（AudioComponent）スロットを<b>2 つ</b>持つアクタ。
/// 既定ではスロット名 "A" / "B" を自動で拾うが、インスペクタの
/// <see cref="sourceA"/> / <see cref="sourceB"/> に明示指定してもよい。
///
/// <b>なぜ 2 音源なのか</b>
/// AudioComponent の <c>Loop = true</c> は「末尾に達したら先頭へ戻す」だけなので、
/// 素材の頭と尻が繋がっていないと必ず「プツッ」という継ぎ目が出る。
/// そこで<b>同じ素材を 2 本の音源で交互に鳴らし</b>、末尾の
/// <see cref="crossfadeSeconds"/> 秒だけ両者を重ねてクロスフェードし、継ぎ目を隠す。
///
/// <b>クロスフェード曲線（等パワー / equal-power）</b>
/// 進捗 t ∈ [0,1] に対して
/// <code>
///   出ていく音量 = volume * cos(t * PI/2)
///   入ってくる音量 = volume * sin(t * PI/2)
/// </code>
/// を用いる。cos² + sin² = 1 なので、無相関な 2 音が重なったときの
/// <b>合成パワーが一定</b>に保たれ、線形フェード（cos/sin の代わりに 1-t / t）で
/// 生じる「中間で音量が凹む」現象が起きない。
/// 波音のように位相の揃っていない素材では等パワーが正解になる。
///
/// <b>再生位置 API が無いことへの対処</b>
/// スクリプト API には再生位置・長さの取得手段が無いため、素材の長さは
/// <see cref="clipSeconds"/>（実測値）を人が入れる<b>データ駆動</b>とし、
/// 経過時間はこのスクリプトが <c>ctx.DeltaTime</c> の積算で自前に管理する。
/// 素材を差し替えたら <see cref="clipSeconds"/> も更新すること。
///
/// <b>ホットリロードについて</b>
/// スクリプトを再読み込みするとインスタンスが作り直されるため、
/// 経過時間・フェード状態はリセットされ、<b>音源 A の頭から鳴り直す</b>。
/// 実行中に編集した場合に音が飛ぶのは仕様（状態は保存されない）。
/// </summary>
public class AmbientLoop : SEEDScript
{
    // ─── 定数（マジックナンバー回避）─────────────────────────

    /// <summary>音源 A の既定スロット名（インスペクタ未設定時に自動解決する名前）。</summary>
    private const string DefaultSlotNameA = "A";

    /// <summary>音源 B の既定スロット名（インスペクタ未設定時に自動解決する名前）。</summary>
    private const string DefaultSlotNameB = "B";

    /// <summary>
    /// クロスフェード秒の下限。素材長がクロスフェード長以下という異常設定のとき、
    /// 「ほぼ即時の切り替え」に縮退させるために使う（0 にすると継ぎ目が丸出しになるため僅かに残す）。
    /// </summary>
    private const float MinimumCrossfadeSeconds = 0.05f;

    /// <summary>素材長の下限（0 や負値を入れられても無限ループしないための保険）。</summary>
    private const float MinimumClipSeconds = 0.1f;

    /// <summary>等パワーフェードの位相係数（t=1 で cos が 0・sin が 1 になる）。</summary>
    private const float EqualPowerPhase = SEED.Mathf.PI * 0.5f;

    /// <summary>無音を表す音量。</summary>
    private const float SilentVolume = 0.0f;

    // ─── 参照（インスペクタ）─────────────────────────────────

    /// <summary>
    /// 交互再生に使う音源その 1。未設定ならスロット名 <see cref="DefaultSlotNameA"/> を自動で探す。
    /// </summary>
    [Header("参照"), SerializeField(Label = "音源A")]
    private SEED.AudioSource? sourceA = null;

    /// <summary>
    /// 交互再生に使う音源その 2。未設定ならスロット名 <see cref="DefaultSlotNameB"/> を自動で探す。
    /// </summary>
    [SerializeField(Label = "音源B")]
    private SEED.AudioSource? sourceB = null;

    // ─── パラメータ（インスペクタ）───────────────────────────

    /// <summary>
    /// 素材 1 周の長さ（秒）。<b>実測値を入れること</b>（再生位置 API が無いため自動取得できない）。
    /// この値からクロスフェード開始時刻を決めるので、ずれると継ぎ目が露出する。
    /// </summary>
    [Header("ループ設定"), SerializeField(Label = "クリップ長(秒)")]
    private float clipSeconds = 75.48f;

    /// <summary>
    /// クロスフェードに掛ける秒数。長いほど継ぎ目は隠れるが、重なり区間で
    /// 素材の同じ部分が二重に鳴るため、波音なら 2〜4 秒程度が扱いやすい。
    /// </summary>
    [SerializeField(Label = "クロスフェード秒")]
    private float crossfadeSeconds = 3.0f;

    /// <summary>定常状態での音量（1.0 = 等倍）。フェード中はこの値を上限に増減する。</summary>
    [SerializeField(Label = "音量")]
    private float volume = 0.6f;

    /// <summary>OnStart で自動的に再生を開始するか。false ならスクリプトから <see cref="Play"/> を呼ぶ。</summary>
    [SerializeField(Label = "開始時に再生")]
    private bool playOnStart = true;

    /// <summary>
    /// 音源パス（assets:// 仮想パス）。空文字なら各 AudioComponent の設定値をそのまま使う。
    /// 2 つの音源へ同じ素材を確実に割り当てたいときにここで一括指定する。
    /// </summary>
    [SerializeField(Label = "音源パス(空なら各Audioの設定を使う)")]
    private string audioPath = "";

    // ─── 実行時状態 ──────────────────────────────────────────

    /// <summary>いま主として鳴っている音源（null = 未解決）。</summary>
    private SEED.AudioSource? current = null;

    /// <summary>次に鳴らす（＝フェード中は重ねて鳴っている）音源。</summary>
    private SEED.AudioSource? next = null;

    /// <summary><see cref="current"/> が鳴り始めてからの経過秒。</summary>
    private float currentElapsed = 0.0f;

    /// <summary><see cref="next"/> が鳴り始めてからの経過秒（フェード中のみ意味を持つ）。</summary>
    private float nextElapsed = 0.0f;

    /// <summary>クロスフェード中か。</summary>
    private bool crossfading = false;

    /// <summary>再生中か（<see cref="Play"/> と <see cref="Stop"/> で切り替わる論理状態）。</summary>
    private bool playing = false;

    /// <summary>音源が解決できなかった旨の警告を出したか（毎フレーム出さないためのラッチ）。</summary>
    private bool warnedMissingSources = false;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>音源を解決・初期化し、必要なら再生を開始する。</summary>
    public override void OnStart()
    {
        ResolveSources();
        InitializeSources();

        if (playOnStart)
        {
            Play();
        }
    }

    /// <summary>経過時間を進め、クロスフェードの開始・進行・完了を処理する。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (!playing)
        {
            return;
        }

        // 音源が揃っていなければ何もしない（警告は解決時に 1 度だけ出している）
        if (current is not { } cur || next is not { } nxt)
        {
            return;
        }

        float dt = ctx.DeltaTime;
        currentElapsed += dt;

        if (crossfading)
        {
            nextElapsed += dt;
            AdvanceCrossfade();
        }
        else if (currentElapsed >= CrossfadeStartSeconds())
        {
            BeginCrossfade(nxt);
        }
    }

    /// <summary>アクタ破棄時に鳴りっぱなしにならないよう両音源を止める。</summary>
    public override void OnDestroy()
    {
        Stop();
    }

    // ─── 公開操作（シーンイベントからの制御用）───────────────

    /// <summary>
    /// 環境音の再生を開始する（既に再生中なら先頭から鳴らし直す）。
    /// 音源が解決できていない場合は何もしない。
    /// </summary>
    public void Play()
    {
        if (current is not { } cur)
        {
            return;
        }

        // 状態をリセットしてから A（= current）だけを規定音量で鳴らす
        StopAll();
        currentElapsed = 0.0f;
        nextElapsed = 0.0f;
        crossfading = false;
        playing = true;

        cur.Volume = volume;
        cur.Play();
    }

    /// <summary>環境音を停止する（両音源を止め、フェード状態も破棄する）。</summary>
    public void Stop()
    {
        StopAll();
        crossfading = false;
        playing = false;
        currentElapsed = 0.0f;
        nextElapsed = 0.0f;
    }

    /// <summary>現在再生中か（<see cref="Play"/> 済みで <see cref="Stop"/> されていないか）。</summary>
    public bool IsPlaying => playing;

    // ─── 内部処理: 初期化 ────────────────────────────────────

    /// <summary>
    /// インスペクタ参照が空の場合に、既定スロット名から音源を補完する。
    /// どちらかでも解決できなければ警告を 1 度だけ出す。
    /// </summary>
    private void ResolveSources()
    {
        if (sourceA is null || sourceA is { } a0 && !a0.IsValid)
        {
            sourceA = gameObject.GetComponent<SEED.AudioSource>(DefaultSlotNameA);
        }
        if (sourceB is null || sourceB is { } b0 && !b0.IsValid)
        {
            sourceB = gameObject.GetComponent<SEED.AudioSource>(DefaultSlotNameB);
        }

        current = sourceA;
        next = sourceB;

        if ((current is null || next is null) && !warnedMissingSources)
        {
            warnedMissingSources = true;
            SEED.Debug.LogWarning(
                "AmbientLoop: 音源が 2 つ揃っていません（スロット \"" + DefaultSlotNameA +
                "\" / \"" + DefaultSlotNameB + "\" の AudioSource が必要）。再生を行いません。");
        }
    }

    /// <summary>
    /// 両音源をクロスフェード運用に適した状態へ揃える。
    /// - Loop は必ず false（末尾でエンジンに巻き戻されるとフェード管理が破綻するため）
    /// - PlayOnStart は必ず false（再生タイミングは本スクリプトが握るため）
    /// - <see cref="audioPath"/> 指定時はパスを上書きし、両者が同一素材であることを保証する
    /// </summary>
    private void InitializeSources()
    {
        ApplySourceSettings(current);
        ApplySourceSettings(next);
    }

    /// <summary>1 つの音源へ共通の初期設定を適用する。</summary>
    /// <param name="source">対象音源（null なら何もしない）。</param>
    private void ApplySourceSettings(SEED.AudioSource? source)
    {
        if (source is not { } s)
        {
            return;
        }

        s.Loop = false;
        s.PlayOnStart = false;

        if (audioPath.Length > 0)
        {
            s.Path = audioPath;
        }
    }

    // ─── 内部処理: クロスフェード ────────────────────────────

    /// <summary>
    /// 実効クリップ長（秒）。0 や負値を弾いて無限ループを防ぐ。
    /// </summary>
    private float EffectiveClipSeconds()
        => SEED.Mathf.Max(clipSeconds, MinimumClipSeconds);

    /// <summary>
    /// 実効クロスフェード長（秒）。
    /// クリップ長以上のフェードは成立しないので、その場合は最小フェードへ縮退させる
    /// （＝ほぼ即時に鳴らし直す。継ぎ目は隠れないが破綻はしない）。
    /// </summary>
    private float EffectiveCrossfadeSeconds()
    {
        float clip = EffectiveClipSeconds();
        if (crossfadeSeconds < MinimumCrossfadeSeconds || crossfadeSeconds >= clip)
        {
            return MinimumCrossfadeSeconds;
        }
        return crossfadeSeconds;
    }

    /// <summary>
    /// クロスフェードを開始する経過秒（＝クリップ長 − フェード長）。
    /// 下限を最小フェード長にして、0 以下になって毎フレーム発火するのを防ぐ。
    /// </summary>
    private float CrossfadeStartSeconds()
        => SEED.Mathf.Max(EffectiveClipSeconds() - EffectiveCrossfadeSeconds(), MinimumCrossfadeSeconds);

    /// <summary>
    /// クロスフェードを開始する。次の音源を音量 0 で鳴らし始め、以降 <see cref="AdvanceCrossfade"/> が
    /// 両者の音量を等パワー曲線で入れ替える。
    /// </summary>
    /// <param name="incoming">これから前面に出る音源。</param>
    private void BeginCrossfade(SEED.AudioSource incoming)
    {
        crossfading = true;
        nextElapsed = 0.0f;

        incoming.Volume = SilentVolume;
        incoming.Play();
    }

    /// <summary>
    /// クロスフェードを 1 フレーム進める。完了したら旧音源を停止して役割を入れ替える。
    /// </summary>
    private void AdvanceCrossfade()
    {
        if (current is not { } cur || next is not { } nxt)
        {
            return;
        }

        float fade = EffectiveCrossfadeSeconds();

        // 進捗 t ∈ [0,1]
        float t = nextElapsed / fade;
        SEED.Mathf.Clamp01(ref t);

        // 等パワー曲線（cos² + sin² = 1 なので合成パワーが一定に保たれる）
        float phase = t * EqualPowerPhase;
        cur.Volume = volume * SEED.Mathf.Cos(phase);
        nxt.Volume = volume * SEED.Mathf.Sin(phase);

        if (t < 1.0f)
        {
            return;
        }

        // ── フェード完了: 旧音源を止めて役割を交代する ──
        cur.Stop();
        nxt.Volume = volume;

        current = nxt;
        next = cur;
        // 新しい current は「フェード長ぶん」既に鳴っているので、その経過を引き継ぐ
        currentElapsed = nextElapsed;
        nextElapsed = 0.0f;
        crossfading = false;
    }

    /// <summary>両音源を停止する（解決できていないものは無視する）。</summary>
    private void StopAll()
    {
        if (current is { } cur)
        {
            cur.Stop();
        }
        if (next is { } nxt)
        {
            nxt.Stop();
        }
    }
}
