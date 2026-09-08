// ============================================================================
//  FishingEvents.cs
//  釣りの進行イベント名（SEED.Events）を一元管理する定数クラス。
// ============================================================================

/// <summary>
/// 釣りの各局面で発火する「名前付きイベント」（<c>SEED.Events</c>）の名前を集めた静的クラス。
///
/// 【なぜ定数クラスに切り出すか】
/// イベント名は文字列なので、発火側（釣りスクリプト）と購読側（チュートリアル・HUD・SE）で
/// タイプミスがあってもコンパイルエラーにならず、「イベントが飛ばない」という
/// 発見しづらい不具合になる。docs/scripting_api.md 7.10 の推奨どおり 1 か所へ集約する。
///
/// 【命名規則】
/// すべて <c>fishing.</c> で始まる小文字のスネークケース。
/// イベント名は大文字小文字を区別するため、必ずこの定数を経由して参照すること。
///
/// 【引数】
/// 引数付きのイベントは <see cref="HookJudged"/>（判定名 string）、
/// <see cref="Catch"/>（魚の表示名 string）、<see cref="DriftPickup"/>（漂流物の種類 string）の 3 つ。
/// それ以外は引数なしで発火する。
/// 型が違うと購読側は呼ばれない（暗黙変換なし）ので、購読時の引数の有無をここの記述に合わせること。
/// </summary>
public static class FishingEvents
{
    // ─── 構え・キャスト ─────────────────────────────────────

    /// <summary>構え（狙い）に入った瞬間。引数なし。</summary>
    public const string ReadyBegin = "fishing.ready_begin";

    /// <summary>構えを解除して待機へ戻った瞬間。引数なし。</summary>
    public const string ReadyEnd = "fishing.ready_end";

    /// <summary>竿を振り抜いて仕掛けを投げた瞬間。引数なし。</summary>
    public const string Cast = "fishing.cast";

    /// <summary>仕掛けが着水した瞬間。引数なし。</summary>
    public const string Land = "fishing.land";

    // ─── アタリ・合わせ ─────────────────────────────────────

    /// <summary>前アタリ（コツコツ）が始まった瞬間。引数なし。</summary>
    public const string Nibble = "fishing.nibble";

    /// <summary>本アタリ（合わせ受付が開いた）瞬間。引数なし。</summary>
    public const string Bite = "fishing.bite";

    /// <summary>合わせ判定が確定した瞬間。引数は判定名 string（"Excellent" / "Great" / "Nice" / "Miss"）。</summary>
    public const string HookJudged = "fishing.hook_judged";

    // ─── やり取り（リズム） ─────────────────────────────────

    /// <summary>やり取り（テンションゲージのリズム勝負）が始まった瞬間。引数なし。</summary>
    public const string FightBegin = "fishing.fight_begin";

    /// <summary>隙（スタン）フェーズへ入った瞬間。引数なし。</summary>
    public const string StunBegin = "fishing.stun_begin";

    /// <summary>隙（スタン）フェーズを抜けた瞬間。引数なし。</summary>
    public const string StunEnd = "fishing.stun_end";

    /// <summary>隙の間に巻き取りを始めた瞬間（巻いている間ずっとではなく立ち上がりの 1 回）。引数なし。</summary>
    public const string Reel = "fishing.reel";

    /// <summary>魚が沖へ走り出した瞬間（やり取り開始直後の余白フェーズ）。引数なし。</summary>
    public const string FishRun = "fishing.fish_run";

    /// <summary>糸が切れた瞬間。引数なし。</summary>
    public const string LineBreak = "fishing.line_break";

    // ─── 成果・漂流物 ───────────────────────────────────────

    /// <summary>魚を釣り上げた瞬間（釣果演出の開始）。引数は魚の表示名 string。</summary>
    public const string Catch = "fishing.catch";

    /// <summary>
    /// 釣果の獲得演出を<b>見せ終えて閉じた</b>瞬間。引数は魚の表示名 string。
    ///
    /// <see cref="Catch"/> は演出の「開始」なので、演出中に次の説明を割り込ませたくない
    /// チュートリアルはこちらを待つ。演出が中断（シーン遷移・破棄）されたときは飛ばない。
    /// </summary>
    public const string CatchPresented = "fishing.catch_presented";

    /// <summary>漂流物が 1 個出現した瞬間。引数なし。</summary>
    public const string DriftSpawn = "fishing.drift_spawn";

    /// <summary>
    /// 漂流物を巻き込んだ瞬間。引数は種類 string
    /// （<c>DriftItem.KindStun</c> / <c>KindFishRecover</c> / <c>KindLineRecover</c>）。
    /// </summary>
    public const string DriftPickup = "fishing.drift_pickup";

    /// <summary>わらしべ成立（掛かっている魚をより大きな魚が食べて乗り換わった）瞬間。引数なし。</summary>
    public const string LevelUp = "fishing.level_up";
}
