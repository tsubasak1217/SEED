// ============================================================================
//  ZukanArrow.cs
//  図鑑のページ送り矢印（クリックでページを 1 つ動かす）。
// ============================================================================

using SEEDEditor.Scripting;

/// <summary>
/// 図鑑のページ送り矢印【矢印のマウス操作の唯一の置き場】。
///
/// 【責務】
/// 「自分がどちら向きか」だけを持ち、クリックを <see cref="Zukan"/> へ中継する。
/// ページの範囲や巻き戻しは知らない（<see cref="Zukan.ChangePage"/> の責務）。
///
/// 【使い方（シーン側）】
/// 矢印の Sprite を持つ 2D アクタへ付け、その Sprite の
/// 「ポインタ判定」（raycast_target）を ON にする。
/// <see cref="pageStep"/> に -1（戻る）か +1（進む）を入れる。
/// </summary>
public class ZukanArrow : SEEDScript
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>カーソルが乗っていないときの明るさ倍率。</summary>
    private const float TintNormal = 1.0f;

    /// <summary>カーソルが乗っているときの明るさ倍率。</summary>
    private const float TintHover = 1.35f;

    // ─── インスペクタ設定 ────────────────────────────────────

    /// <summary>この矢印が動かすページ数（-1 = 前へ / +1 = 次へ）。</summary>
    [SerializeField(Label = "ページの向き(-1/+1)")]
    private int pageStep = Zukan.PageStepNext;

    // ─── 実行時の状態 ────────────────────────────────────────

    /// <summary>シーンで設定された素の色（ホバーの明るさを掛ける基準）。</summary>
    private SEED.Color baseColor = SEED.Color.White;

    // ─── ライフサイクル ──────────────────────────────────────

    /// <summary>開始時に素の色を控える（配色はシーン側の値を正典にする）。</summary>
    public override void OnStart()
    {
        if (gameObject.GetComponent<SEED.Sprite>() is { } sprite && sprite.IsValid)
        {
            baseColor = sprite.Color;
        }
    }

    // ─── ポインタイベント ────────────────────────────────────

    /// <summary>カーソルが乗ったら少し明るくする。</summary>
    public override void OnPointerEnter() => ApplyTint(TintHover);

    /// <summary>カーソルが外れたら元の色へ戻す。</summary>
    public override void OnPointerExit() => ApplyTint(TintNormal);

    /// <summary>押し切られたらページを送る。</summary>
    public override void OnPointerClick()
    {
        Zukan.Current?.ChangePage(pageStep);
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>素の色へ明るさ倍率を掛けて塗り直す（アルファは保つ）。</summary>
    /// <param name="scale">明るさの倍率。</param>
    private void ApplyTint(float scale)
    {
        if (gameObject.GetComponent<SEED.Sprite>() is not { } sprite || !sprite.IsValid) { return; }
        sprite.Color = new SEED.Color(
            baseColor.r * scale, baseColor.g * scale, baseColor.b * scale, baseColor.a);
    }
}
