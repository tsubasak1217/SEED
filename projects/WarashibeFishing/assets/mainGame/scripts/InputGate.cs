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

    /// <summary>
    /// 台詞・チュートリアル説明の送り（Enter / Space / 左クリック）【進行系】。
    ///
    /// 釣りの操作と違い、手順ごとの制限（<see cref="InputGate.DenyAll"/> /
    /// <see cref="InputGate.SetAllowed"/>）の対象外にしてある。説明中は釣り操作を
    /// すべて塞ぐが、説明そのものを送れなくなると先へ進めなくなるため。
    /// ポーズ中だけは <see cref="InputGate.Suspend"/> によって他の操作と一緒に塞がれる。
    /// </summary>
    Advance,
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

    /// <summary>停止解除の刻の初期値（どのフレームの実時刻とも一致しない値）。</summary>
    private const float NoResumeStamp = -1f;

    /// <summary>
    /// 手順ごとの制限の対象外にする操作【進行系の定義表】。
    ///
    /// ここに挙げた操作は <see cref="DenyAll"/> / <see cref="SetAllowed"/> では
    /// 塞がれず、<see cref="Suspend"/>（ポーズ）でだけ塞がる。
    /// 増減はこの配列を書き換えるだけで済む（判定側に条件を散らさない）。
    /// </summary>
    private static readonly GameAction[] UnrestrictableActions = { GameAction.Advance };

    // ─── 状態 ────────────────────────────────────────────────

    /// <summary>
    /// 操作カテゴリごとの受付可否。添字は <see cref="GameAction"/> の値。
    /// 列挙子を増やしても長さが自動で追従するよう、<c>System.Enum.GetValues</c> の
    /// 件数で確保する（要素数の直書きを避ける）。
    /// </summary>
    private static readonly bool[] Allowed = CreateAllAllowed();

    /// <summary>
    /// すべての操作を上から塞いでいるか（ポーズ中か）。
    /// 受付表（<see cref="Allowed"/>）とは独立した「蓋」なので、
    /// 解除すればチュートリアルが組んだ手順ごとの許可がそのまま戻る。
    /// </summary>
    private static bool suspended;

    /// <summary>
    /// 停止を解除した実時間の刻（<c>SEED.Time.UnscaledElapsedTime</c>）。
    ///
    /// ポーズを閉じたのと同じフレームでは、閉じるのに使った Esc / クリック /
    /// 決定キーがまだ「押された瞬間」として観測される。スクリプトの実行順に
    /// 依存せずこれを捨てるため、「解除と同じ刻の入力は通さない」形にしてある。
    /// </summary>
    private static float resumeStamp = NoResumeStamp;

    // ─── 公開 API ────────────────────────────────────────────

    /// <summary>
    /// 指定した操作をいま受け付けてよいか。
    /// </summary>
    /// <param name="action">問い合わせる操作カテゴリ。</param>
    /// <returns>受け付けてよければ true。未知の値（列挙外のキャスト）は安全側で true。</returns>
    public static bool Allows(GameAction action)
    {
        // ポーズ中と、ポーズを閉じたそのフレームは、どの操作も通さない
        if (IsBlockedGlobally()) { return false; }

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
        // 進行系（台詞送りなど）は手順の制限対象外。ここで塞げてしまうと
        // 「説明中に説明を送れない」状態を作れてしまうため無視する。
        if (IsUnrestrictable(action)) { return; }

        int index = (int)action;
        if (index < 0 || index >= Allowed.Length) { return; }
        Allowed[index] = allowed;
    }

    /// <summary>
    /// すべての操作を受け付ける状態へ戻す【制限解除の唯一の出口】。
    /// 制限を掛けた側は、処理の終了時・破棄時に必ずこれを呼ぶこと。
    ///
    /// ポーズによる停止（<see cref="Suspend"/>）は別系統なので、ここでは解けない
    /// （解いてしまうと、チュートリアルの後始末がポーズを勝手に無効化してしまう）。
    /// </summary>
    public static void AllowAll()
    {
        for (int i = 0; i < Allowed.Length; i++) { Allowed[i] = DefaultAllowed; }
    }

    /// <summary>
    /// 手順の制限対象になる操作をすべて受け付けない状態にする
    /// （チュートリアルが 1 手順ぶんの許可を組み立てる前の下地として使う）。
    ///
    /// 進行系（<see cref="UnrestrictableActions"/>）は既定の受付状態のまま残す。
    /// </summary>
    public static void DenyAll()
    {
        for (int i = 0; i < Allowed.Length; i++)
        {
            Allowed[i] = IsUnrestrictable((GameAction)i) ? DefaultAllowed : !DefaultAllowed;
        }
    }

    // ─── 全体停止（ポーズ）──────────────────────────────────

    /// <summary>
    /// いま全操作を止めているか（＝ポーズ中か）。
    /// タイマーなど「入力以外の進行」も止めたい側は、これを見て自分の更新を飛ばす。
    /// </summary>
    public static bool IsSuspended => suspended;

    /// <summary>
    /// すべての操作の受付を一時停止する【ポーズ側の入口】。
    ///
    /// 受付表は書き換えずに上から蓋をするだけなので、<see cref="Resume"/> すれば
    /// チュートリアルが組んだ手順ごとの許可がそのまま戻る。
    /// </summary>
    public static void Suspend()
    {
        suspended   = true;
        resumeStamp = NoResumeStamp;
    }

    /// <summary>
    /// 一時停止を解除する【ポーズ解除側の出口】。
    /// 解除と同じ刻（同じフレーム）の入力は捨てる（<see cref="resumeStamp"/> 参照）。
    /// </summary>
    public static void Resume()
    {
        suspended   = false;
        resumeStamp = SEED.Time.UnscaledElapsedTime;
    }

    // ─── 内部処理 ────────────────────────────────────────────

    /// <summary>
    /// このフレームは操作カテゴリを問わず入力を捨てるべきか
    /// （停止中、または停止を解除したそのフレーム）。
    /// </summary>
    /// <returns>捨てるべきなら true。</returns>
    private static bool IsBlockedGlobally()
        => suspended
        || (resumeStamp > NoResumeStamp && SEED.Time.UnscaledElapsedTime <= resumeStamp);

    /// <summary>
    /// その操作が手順ごとの制限の対象外（進行系）か。
    /// </summary>
    /// <param name="action">判定する操作カテゴリ。</param>
    /// <returns>対象外なら true。</returns>
    private static bool IsUnrestrictable(GameAction action)
    {
        for (int i = 0; i < UnrestrictableActions.Length; i++)
        {
            if (UnrestrictableActions[i] == action) { return true; }
        }
        return false;
    }

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
