// ============================================================================
//  InputGate.cs
//  「いまどの操作を受け付けるか」だけを持つ入力ゲート（チュートリアル用の関門）。
// ============================================================================

/// <summary>
/// ゲーム側の操作カテゴリ。
///
/// 釣りスクリプトが入力を読む場所と 1 対 1 で対応させ、
/// チュートリアルが「この手順で使う操作だけを許可する」ために使う。
/// 増やすときは <see cref="InputGate"/> の配列長も自動で追従する（Length を使うため）。
/// </summary>
public enum GameAction
{
    /// <summary>プレイヤーの移動（W / S。InputMap の "Move"）。</summary>
    Move,

    /// <summary>釣りの構えに入る／構えを解除する（左クリックの押下と保持）。</summary>
    Ready,

    /// <summary>構え中の左右の狙い（A / D）。</summary>
    Aim,

    /// <summary>振りかぶり・振り抜き（マウスを左→右へ振るジェスチャ）。</summary>
    Cast,

    /// <summary>巻き取り（マウスホイール）と巻き方向の操舵（A / D）。</summary>
    Reel,

    /// <summary>竿を振る（合わせ・空振り）。左クリック。</summary>
    Hook,

    /// <summary>やり取り中のリズム回答タップ。左クリック。</summary>
    Rhythm,

    /// <summary>釣果表示など UI の決定（左クリック）。</summary>
    UiConfirm,
}

/// <summary>
/// 操作カテゴリごとの受付可否を持つ静的な関門【入力の許可判定の唯一の置き場】。
///
/// 【責務】
/// 「許可されているか」を答えるだけ。入力そのものは読まないし、
/// 誰がいつ制限を掛けたかも知らない（単一責任）。
///
/// 【使い方】
/// 入力を読む直前に 1 行挟む。
/// <code>
/// if (!InputGate.Allows(GameAction.Cast)) { return; }
/// float deltaX = SEED.Input.MouseDelta.x;
/// </code>
///
/// 【既定値】
/// 全許可。制限を掛けるのはチュートリアル（<c>TutorialDirector</c>）だけで、
/// 制限した側が必ず <see cref="AllowAll"/> で戻す責任を持つ。
///
/// 【ホットリロード・シーン遷移との関係】
/// 静的フィールドはスクリプト再コンパイル時に作り直されるため、
/// リロード直後は必ず全許可から始まる（＝制限が焼き付いて操作不能になることはない）。
/// ただし<b>シーン遷移では静的フィールドは作り直されない</b>ので、
/// 制限を掛けたスクリプトは <c>OnDestroy</c> で必ず <see cref="AllowAll"/> を呼ぶこと。
/// </summary>
public static class InputGate
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>既定の受付状態（true = 受け付ける）。</summary>
    private const bool DefaultAllowed = true;

    // ─── 状態 ────────────────────────────────────────────────

    /// <summary>
    /// 操作カテゴリごとの受付可否。添字は <see cref="GameAction"/> の値。
    /// 列挙子を増やしても長さが自動で追従するよう、<c>System.Enum.GetValues</c> の
    /// 件数で確保する（要素数の直書きを避ける）。
    /// </summary>
    private static readonly bool[] Allowed = CreateAllAllowed();

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>
    /// 指定した操作をいま受け付けてよいか。
    /// </summary>
    /// <param name="action">問い合わせる操作カテゴリ。</param>
    /// <returns>受け付けてよければ true。未知の値（列挙外のキャスト）は安全側で true。</returns>
    public static bool Allows(GameAction action)
    {
        int index = (int)action;
        if (index < 0 || index >= Allowed.Length) { return DefaultAllowed; }
        return Allowed[index];
    }

    /// <summary>
    /// 指定した操作の受付可否を設定する。
    /// </summary>
    /// <param name="action">設定する操作カテゴリ。</param>
    /// <param name="allowed">true で受け付ける／false で無視する。</param>
    public static void SetAllowed(GameAction action, bool allowed)
    {
        int index = (int)action;
        if (index < 0 || index >= Allowed.Length) { return; }
        Allowed[index] = allowed;
    }

    /// <summary>
    /// すべての操作を受け付ける状態へ戻す【制限解除の唯一の出口】。
    /// 制限を掛けた側は、処理の終了時・破棄時に必ずこれを呼ぶこと。
    /// </summary>
    public static void AllowAll()
    {
        for (int i = 0; i < Allowed.Length; i++) { Allowed[i] = DefaultAllowed; }
    }

    /// <summary>
    /// すべての操作を受け付けない状態にする（チュートリアルが 1 手順ぶんの
    /// 許可を組み立てる前の下地として使う）。
    /// </summary>
    public static void DenyAll()
    {
        for (int i = 0; i < Allowed.Length; i++) { Allowed[i] = !DefaultAllowed; }
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// 全許可で初期化した受付表を作る（列挙子の数に自動追従する）。
    /// </summary>
    /// <returns>すべて <see cref="DefaultAllowed"/> で埋めた配列。</returns>
    private static bool[] CreateAllAllowed()
    {
        int count = System.Enum.GetValues(typeof(GameAction)).Length;
        var table = new bool[count];
        for (int i = 0; i < table.Length; i++) { table[i] = DefaultAllowed; }
        return table;
    }
}
