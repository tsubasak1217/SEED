// ============================================================================
//  Zukan.cs
//  図鑑シーンの本体（レベルごとに 1 ページ。捕獲状況をカードで並べる）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 魚図鑑の表示【図鑑ページ構築の唯一の置き場】。
///
/// 【責務】
/// <c>FishCatalog</c>（何の魚がいるか）と <c>FishRecords</c>（何を釣ったか）を
/// 突き合わせて、シーンに用意されたカード枠へ流し込む。
/// 魚のデータも釣果の保存方法も知らない（どちらも上記 2 つの責務）。
///
/// 【ページの単位】
/// 魚レベル 1 ページ（Lv1 〜 <c>FishCatalog.MaxLevel</c>）。
/// そのレベルの魚を <c>FishCatalog.ForLevel</c> の順（＝アクタ名順）で左から並べる。
///
/// 【カード枠の持ち方 — なぜプレハブを動的生成しないか】
/// スクリプトの <c>[SerializeField]</c> 参照は「アクタ名」で解決され、
/// 探索はシーン全体の DFS で最初に一致したものを返す。
/// そのためカードをプレハブから複数生成すると、2 枚目以降の参照が
/// すべて 1 枚目の子アクタへ吸われてしまう。
/// よってカード枠はシーンへ固定で並べ（名前が一意になる）、
/// 位置とテキストだけをこのスクリプトが書き換える方式にしてある。
/// 枠を増やすときはシーンへカードを 1 つ足し、下の各配列へ結線するだけでよい。
///
/// 【未捕獲の表示】
/// 画像は真っ黒（シルエット）、名前は「Lv{N} ????」、
/// ベスト・ランク・釣った数は空にする。
/// </summary>
public class Zukan : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>最初に開くページ（＝魚レベル）。</summary>
    private const int FirstLevel = 1;

    /// <summary>ページを 1 つ戻す向き。</summary>
    public const int PageStepPrev = -1;

    /// <summary>ページを 1 つ進める向き。</summary>
    public const int PageStepNext = 1;

    /// <summary>不透明を表すアルファ。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>完全に透明（＝隠す）を表すアルファ。</summary>
    private const float AlphaHidden = 0f;

    /// <summary>カードを左右対称に並べるための中心係数（(n-1)/2 の 2）。</summary>
    private const float CenteringDivisor = 2f;

    // ─── 静的アクセサ ────────────────────────────────────────

    /// <summary>実行中のインスタンス（矢印スプライトのクリックから呼ぶ）。</summary>
    public static Zukan? Current { get; private set; }

    // ─── インスペクタ設定（文言・書式）─────────────────────────

    /// <summary>見出しの書式（{0} にレベル番号が入る）。</summary>
    [Header("文言"), SerializeField(Label = "見出しの書式")]
    private string headerFormat = "ずかん   Lv {0}";

    /// <summary>ページ位置の書式（{0} = 現在のレベル / {1} = 最大レベル）。</summary>
    [SerializeField(Label = "ページ表示の書式")]
    private string pageFormat = "{0} / {1}";

    /// <summary>未捕獲の魚の名前の書式（{0} にレベル番号が入る）。</summary>
    [SerializeField(Label = "未捕獲の名前")]
    private string unknownNameFormat = "Lv{0} ????";

    /// <summary>捕獲済みの魚の名前の書式（{0} = レベル / {1} = 表示名）。</summary>
    [SerializeField(Label = "捕獲済みの名前")]
    private string knownNameFormat = "Lv{0} {1}";

    /// <summary>ベストサイズ行の書式（{0} = サイズ / {1} = 単位）。</summary>
    [SerializeField(Label = "ベスト行の書式")]
    private string bestFormat = "ベスト: {0:F1}{1}";

    /// <summary>ランク行の書式（{0} = ランク文字）。</summary>
    [SerializeField(Label = "ランク行の書式")]
    private string rankFormat = "ランク: {0}";

    /// <summary>釣った数の行の書式（{0} = 回数）。</summary>
    [SerializeField(Label = "釣った数の書式")]
    private string countFormat = "つった数: {0}";

    /// <summary>サイズの単位ラベル。</summary>
    [SerializeField(Label = "サイズの単位")]
    private string sizeUnit = "cm";

    /// <summary>ベストランクが未記録（旧セーブデータ）のときに出す文字。</summary>
    [SerializeField(Label = "ランク未記録の表示")]
    private string unknownRankLabel = "-";

    /// <summary>操作案内の文言。</summary>
    [SerializeField(Label = "操作案内")]
    private string hintLabel = "A / D ・ ← → でページ切り替え     Esc / B でもどる";

    // ─── インスペクタ設定（レイアウト・配色）───────────────────

    /// <summary>カードの横間隔（キャンバスピクセル）。</summary>
    [Header("レイアウト"), SerializeField(Label = "カードの横間隔")]
    private float cardSpacing = 290f;

    /// <summary>カードを並べる帯の Y 座標（カード枠の親から見た相対）。</summary>
    [SerializeField(Label = "カード列のY")]
    private float cardRowY = 0f;

    /// <summary>未捕獲の魚のシルエット色（RGB。既定は真っ黒）。</summary>
    [Header("配色"), SerializeField(Label = "シルエット色(RGB)")]
    private SEED.Vector3 silhouetteColor = new(0f, 0f, 0f);

    /// <summary>捕獲済みの魚の画像色（RGB。既定は素の色）。</summary>
    [SerializeField(Label = "捕獲済みの画像色(RGB)")]
    private SEED.Vector3 caughtImageColor = new(1f, 1f, 1f);

    // ─── インスペクタ設定（遷移）───────────────────────────────

    /// <summary>戻り先のシーン名を保存してあるセーブキー。</summary>
    [Header("遷移"), SerializeField(Label = "戻り先のセーブキー")]
    private string returnSceneKey = "zukan_return";

    /// <summary>戻り先が保存されていないときの既定のシーン名。</summary>
    [SerializeField(Label = "既定の戻り先シーン")]
    private string defaultReturnScene = "title";

    // ─── 参照（シーン内の固定カード枠。インスペクタで結線）───────

    /// <summary>見出しのテキスト。</summary>
    [Header("参照（ヘッダ・フッタ）"), SerializeField(Label = "見出しText")]
    private SEED.Text? headerText;

    /// <summary>ページ位置のテキスト。</summary>
    [SerializeField(Label = "ページ表示Text")]
    private SEED.Text? pageText;

    /// <summary>操作案内のテキスト。</summary>
    [SerializeField(Label = "操作案内Text")]
    private SEED.Text? hintText;

    /// <summary>カードの根（表示/非表示と位置決めに使う）。</summary>
    [Header("参照（カード枠。要素数＝枠の数）"), SerializeField(Label = "カードのアクタ")]
    private SEED.GameObject[] cards = System.Array.Empty<SEED.GameObject>();

    /// <summary>カードの魚画像。</summary>
    [SerializeField(Label = "カードの画像")]
    private SEED.Sprite[] cardImages = System.Array.Empty<SEED.Sprite>();

    /// <summary>カードの名前テキスト。</summary>
    [SerializeField(Label = "カードの名前")]
    private SEED.Text[] cardNames = System.Array.Empty<SEED.Text>();

    /// <summary>カードのベストサイズテキスト。</summary>
    [SerializeField(Label = "カードのベスト")]
    private SEED.Text[] cardBests = System.Array.Empty<SEED.Text>();

    /// <summary>カードのランクテキスト。</summary>
    [SerializeField(Label = "カードのランク")]
    private SEED.Text[] cardRanks = System.Array.Empty<SEED.Text>();

    /// <summary>カードの釣った数テキスト。</summary>
    [SerializeField(Label = "カードの釣った数")]
    private SEED.Text[] cardCounts = System.Array.Empty<SEED.Text>();

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>いま表示しているページ（＝魚レベル）。</summary>
    private int currentLevel = FirstLevel;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>開始時の初期化。1 ページ目を組み立てる。</summary>
    public override void OnStart()
    {
        Current = this;

        // 図鑑はマウスで矢印を押せる必要があるので、カーソルロックは必ず外す
        // （釣りシーンからロック状態のまま遷移してくる経路があるため）。
        SEED.Input.CursorLocked = false;

        SetContent(hintText, hintLabel);
        currentLevel = FirstLevel;
        BuildPage();
    }

    /// <summary>破棄時の後始末。静的アクセサを取り消す。</summary>
    public override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) { Current = null; }
    }

    /// <summary>毎フレームの入力処理（ページ送りと退出）。</summary>
    public override void Update(ref NativeFrameContext ctx)
    {
        if (SEED.Input.GetKeyDown(SEED.KeyCode.A) || SEED.Input.GetKeyDown(SEED.KeyCode.LeftArrow))
        {
            ChangePage(PageStepPrev);
        }
        if (SEED.Input.GetKeyDown(SEED.KeyCode.D) || SEED.Input.GetKeyDown(SEED.KeyCode.RightArrow))
        {
            ChangePage(PageStepNext);
        }
        if (SEED.Input.GetKeyDown(SEED.KeyCode.Escape) || SEED.Input.GetKeyDown(SEED.KeyCode.B))
        {
            LeaveZukan();
        }
    }

    // ─── ページ操作 ──────────────────────────────────────────

    /// <summary>
    /// ページを送る【ページ変更の唯一の入口】（矢印スプライトのクリックからも呼ばれる）。
    /// 端は反対側へ巻き戻る。
    /// </summary>
    /// <param name="step">送る向き（<see cref="PageStepPrev"/> / <see cref="PageStepNext"/>）。</param>
    public void ChangePage(int step)
    {
        int max = SafeMaxLevel();
        if (max < FirstLevel) { return; }

        // レベルは 1 始まりなので、0 始まりへ直してから剰余で巻き戻す
        int zeroBased = currentLevel - FirstLevel + step;
        int wrapped = ((zeroBased % max) + max) % max;
        currentLevel = wrapped + FirstLevel;
        BuildPage();
    }

    /// <summary>図鑑を閉じて、開く前のシーンへ戻る。</summary>
    private void LeaveZukan()
    {
        string scene = SEED.SaveData.GetString(returnSceneKey, defaultReturnScene);
        if (string.IsNullOrWhiteSpace(scene)) { scene = defaultReturnScene; }
        SEED.Scene.Transition(scene);
    }

    // ─── ページの組み立て ────────────────────────────────────

    /// <summary>
    /// 現在のページ（レベル）ぶんのカードを作り直す
    /// 【カードの内容と配置を決める唯一の場所】。
    ///
    /// カード枠は使い回し（生成も破棄もしない）。今回のページで使わない枠は
    /// <c>Visible = false</c> で隠す。
    /// </summary>
    private void BuildPage()
    {
        SetContent(headerText, string.Format(headerFormat, currentLevel));
        SetContent(pageText, string.Format(pageFormat, currentLevel, SafeMaxLevel()));

        // 今のページに載る魚を、枠の数を上限に集める
        int slotCount = cards.Length;
        var entries = new FishCatalogEntry[slotCount];
        int used = 0;
        foreach (FishCatalogEntry entry in FishCatalog.ForLevel(currentLevel))
        {
            if (used >= slotCount)
            {
                SEED.Debug.LogWarning(
                    $"[Zukan] Lv{currentLevel} の魚がカード枠({slotCount})を超えたため一部を表示できない");
                break;
            }
            entries[used] = entry;
            used++;
        }

        // 使う枠だけを中央揃えで並べ、余った枠は隠す
        for (int i = 0; i < slotCount; i++)
        {
            bool inUse = i < used;
            SetCardVisible(i, inUse);
            if (!inUse) { continue; }

            PlaceCard(i, used);
            FillCard(i, entries[i]);
        }
    }

    /// <summary>カード 1 枚を、使用枚数に対して中央揃えになる位置へ置く。</summary>
    /// <param name="slot">カード枠の添字。</param>
    /// <param name="usedCount">今のページで使う枚数。</param>
    private void PlaceCard(int slot, int usedCount)
    {
        if (!IsSlotValid(slot)) { return; }
        if (cards[slot].GetComponent<SEED.CanvasTransform>() is not { } ct) { return; }

        float centerOffset = (usedCount - 1) / CenteringDivisor;
        ct.Position = new SEED.Vector2((slot - centerOffset) * cardSpacing, cardRowY);
    }

    /// <summary>カード 1 枚へ、1 種ぶんのカタログと釣果を流し込む。</summary>
    /// <param name="slot">カード枠の添字。</param>
    /// <param name="entry">その枠に載せる魚。</param>
    private void FillCard(int slot, FishCatalogEntry entry)
    {
        bool caught = FishRecords.IsCaught(entry.displayName);

        // 画像は捕獲の有無に関わらず同じテクスチャ。未捕獲は真っ黒に塗ってシルエットにする
        if (GetAt(cardImages, slot) is { } image && image.IsValid)
        {
            image.TexturePath = entry.imagePath;
            SEED.Vector3 rgb = caught ? caughtImageColor : silhouetteColor;
            image.Color = new SEED.Color(rgb.x, rgb.y, rgb.z, AlphaOpaque);
        }

        SetContent(GetAt(cardNames, slot), caught
            ? string.Format(knownNameFormat, entry.level, entry.displayName)
            : string.Format(unknownNameFormat, entry.level));

        // 未捕獲のときは記録欄を空にする（枠だけ残す）
        if (!caught)
        {
            SetContent(GetAt(cardBests, slot), string.Empty);
            SetContent(GetAt(cardRanks, slot), string.Empty);
            SetContent(GetAt(cardCounts, slot), string.Empty);
            return;
        }

        string rank = FishRecords.BestRank(entry.displayName);
        if (string.IsNullOrWhiteSpace(rank)) { rank = unknownRankLabel; }

        SetContent(GetAt(cardBests, slot),
            string.Format(bestFormat, FishRecords.BestSize(entry.displayName), sizeUnit));
        SetContent(GetAt(cardRanks, slot), string.Format(rankFormat, rank));
        SetContent(GetAt(cardCounts, slot),
            string.Format(countFormat, FishRecords.CatchCount(entry.displayName)));
    }

    // ─── 小さなヘルパー ──────────────────────────────────────

    /// <summary>カード枠まるごとの表示・非表示。</summary>
    /// <param name="slot">カード枠の添字。</param>
    /// <param name="visible">表示するか。</param>
    private void SetCardVisible(int slot, bool visible)
    {
        if (!IsSlotValid(slot)) { return; }
        cards[slot].Visible = visible;
    }

    /// <summary>その添字のカード枠が使えるか（範囲内かつ生存しているか）。</summary>
    /// <param name="slot">カード枠の添字。</param>
    private bool IsSlotValid(int slot)
        => slot >= 0 && slot < cards.Length && cards[slot].IsValid;

    /// <summary>配列から安全に 1 件取り出す（範囲外は null）。</summary>
    /// <typeparam name="T">要素の型。</typeparam>
    /// <param name="array">対象の配列。</param>
    /// <param name="index">添字。</param>
    private static T? GetAt<T>(T[] array, int index) where T : struct
        => index >= 0 && index < array.Length ? array[index] : null;

    /// <summary>テキストへ文字列を設定する（未設定・破棄済みなら何もしない）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="content">表示する文字列。空文字なら消える。</param>
    private static void SetContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
        t.Color = t.Color.WithAlpha(string.IsNullOrEmpty(content) ? AlphaHidden : AlphaOpaque);
    }

    /// <summary>収録されている最大レベル（0 以下なら図鑑が空＝ページ送り不能）。</summary>
    private static int SafeMaxLevel() => FishCatalog.MaxLevel;
}
