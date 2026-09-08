// ============================================================================
//  ZukanCard.cs
//  図鑑カード 1 枚ぶんの表示。プレハブ assets://zukan/actors/ZukanCard.actor の中身。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 図鑑カード 1 枚の見た目【カード 1 枚の表示を決める唯一の場所】。
///
/// 【責務】
/// 「この魚を、捕獲済み／未捕獲のどちらとして描くか」だけを知っている。
/// どの魚をどのページに並べるか（＝ページング・レイアウト）は
/// <see cref="Zukan"/> の責務で、こちらは <see cref="Show"/> / <see cref="Hide"/> /
/// <see cref="PlaceAt"/> を呼ばれるだけである。
///
/// 【プレハブであること】
/// このスクリプトはプレハブ <c>assets://zukan/actors/ZukanCard.actor</c> のルートに載る。
/// 子アクタ（Image / Name / Best / Rank / Count）への参照は
/// <b>相対パス指定（<c>./Image</c> など）</b>で結線してあるため、
/// 同じプレハブをシーンへ何枚並べても、各インスタンスが自分の子だけを掴む。
/// （素のアクタ名で結線すると、シーン全体 DFS の先頭＝ 1 枚目の子へ全部吸われる。）
///
/// 【未捕獲の表示】
/// 画像は真っ黒（シルエット）、名前は「Lv{N} ????」、
/// ベスト・ランク・釣った数は空にする。
/// </summary>
public class ZukanCard : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>不透明を表すアルファ。</summary>
    private const float AlphaOpaque = 1f;

    /// <summary>完全に透明（＝隠す）を表すアルファ。</summary>
    private const float AlphaHidden = 0f;

    // ─── インスペクタ設定（文言・書式）─────────────────────────

    /// <summary>未捕獲の魚の名前の書式（{0} にレベル番号が入る）。</summary>
    [Header("文言"), SerializeField(Label = "未捕獲の名前")]
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

    // ─── インスペクタ設定（配色）───────────────────────────────

    /// <summary>未捕獲の魚のシルエット色（RGB。既定は真っ黒）。</summary>
    [Header("配色"), SerializeField(Label = "シルエット色(RGB)")]
    private SEED.Vector3 silhouetteColor = new(0f, 0f, 0f);

    /// <summary>捕獲済みの魚の画像色（RGB。既定は素の色）。</summary>
    [SerializeField(Label = "捕獲済みの画像色(RGB)")]
    private SEED.Vector3 caughtImageColor = new(1f, 1f, 1f);

    // ─── 参照（自分の子アクタ。相対パスで結線）───────────────────

    /// <summary>魚の画像。</summary>
    [Header("参照（自分の子。相対パスで結線）"), SerializeField(Label = "画像Sprite")]
    private SEED.Sprite? image;

    /// <summary>魚の名前テキスト。</summary>
    [SerializeField(Label = "名前Text")]
    private SEED.Text? nameText;

    /// <summary>ベストサイズのテキスト。</summary>
    [SerializeField(Label = "ベストText")]
    private SEED.Text? bestText;

    /// <summary>ランクのテキスト。</summary>
    [SerializeField(Label = "ランクText")]
    private SEED.Text? rankText;

    /// <summary>釣った数のテキスト。</summary>
    [SerializeField(Label = "釣った数Text")]
    private SEED.Text? countText;

    // ─── 公開 API（Zukan から呼ばれる）───────────────────────────

    /// <summary>
    /// カードへ 1 種ぶんの内容を流し込んで表示する
    /// 【カードの中身を書き換える唯一の入口】。
    /// </summary>
    /// <param name="entry">載せる魚のカタログ項目。</param>
    /// <param name="caught">その魚を 1 匹以上釣っているか。</param>
    /// <param name="best">ベストサイズ（未捕獲なら無視される）。</param>
    /// <param name="rank">ベストランク文字（空なら「未記録」表示になる）。</param>
    /// <param name="count">釣った数（未捕獲なら無視される）。</param>
    public void Show(FishCatalogEntry entry, bool caught, float best, string rank, int count)
    {
        // gameObject はプロパティ（値型を返す）なのでメンバへ直接代入できない（CS1612）。
        // ローカルへ受けてから設定する。Visible の setter は FFI 呼び出しで
        // 構造体自体を書き換えないため、コピー経由でも意味は変わらない。
        var self = gameObject;
        self.Visible = true;

        // 画像は捕獲の有無に関わらず同じテクスチャ。未捕獲は真っ黒に塗ってシルエットにする
        if (image is { } sprite && sprite.IsValid)
        {
            sprite.TexturePath = entry.imagePath;
            SEED.Vector3 rgb = caught ? caughtImageColor : silhouetteColor;
            sprite.Color = new SEED.Color(rgb.x, rgb.y, rgb.z, AlphaOpaque);
        }

        SetContent(nameText, caught
            ? string.Format(knownNameFormat, entry.level, entry.displayName)
            : string.Format(unknownNameFormat, entry.level));

        // 未捕獲のときは記録欄を空にする（枠だけ残す）
        if (!caught)
        {
            SetContent(bestText,  string.Empty);
            SetContent(rankText,  string.Empty);
            SetContent(countText, string.Empty);
            return;
        }

        string rankLabel = string.IsNullOrWhiteSpace(rank) ? unknownRankLabel : rank;

        SetContent(bestText,  string.Format(bestFormat, best, sizeUnit));
        SetContent(rankText,  string.Format(rankFormat, rankLabel));
        SetContent(countText, string.Format(countFormat, count));
    }

    /// <summary>カードまるごとを隠す（このページで使わない枠）。</summary>
    public void Hide()
    {
        // Show と同じ理由でローカルへ受けてから設定する（CS1612 回避）。
        var self = gameObject;
        self.Visible = false;
    }

    /// <summary>カードの根を、親から見た相対位置へ置く。</summary>
    /// <param name="position">親（カード列の親アクタ）から見たローカル座標。</param>
    public void PlaceAt(SEED.Vector2 position)
    {
        if (gameObject.GetComponent<SEED.CanvasTransform>() is not { } ct) { return; }
        ct.Position = position;
    }

    // ─── 小さなヘルパー ──────────────────────────────────────

    /// <summary>テキストへ文字列を設定する（未設定・破棄済みなら何もしない）。</summary>
    /// <param name="text">対象のテキスト。</param>
    /// <param name="content">表示する文字列。空文字なら消える。</param>
    private static void SetContent(SEED.Text? text, string content)
    {
        if (text is not { } t || !t.IsValid) { return; }
        t.Content = content;
        t.Color = t.Color.WithAlpha(string.IsNullOrEmpty(content) ? AlphaHidden : AlphaOpaque);
    }
}
