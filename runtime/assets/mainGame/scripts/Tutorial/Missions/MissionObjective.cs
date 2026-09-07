// ============================================================================
//  MissionObjective.cs
//  ミッションの「サブ目標」1 件（チェックボックス 1 行）。
// ============================================================================

/// <summary>
/// ミッションのサブ目標 1 件【パネルのチェック 1 行に対応する唯一の型】。
///
/// 【責務】
/// 「表示名」と「達成済みか」だけを持つ。達成条件の判定は各ミッション
/// （<see cref="IMission"/> の実装）の責務で、この型は結果を受け取るだけ（単一責任）。
///
/// 【なぜクラスか】
/// ミッションが保持したリストを <see cref="MissionPanel"/> がそのまま読み、
/// 達成のたびに中身だけが書き換わる。構造体（値型）にするとリストへ入れた瞬間に
/// 複製されてしまい、「達成した」という変更が表示側へ届かない。
/// </summary>
public sealed class MissionObjective
{
    // ─── 定数（マジックナンバー禁止）─────────────────────────

    /// <summary>
    /// 達成済みのサブ目標の先頭に付ける記号。
    ///
    /// チェック記号（U+2713）は JIS X 0208 に無く、日本語フォントによっては
    /// 字形が入っていない（＝空白として描かれる）。塗り／白抜きの四角は
    /// JIS X 0208 に含まれるので、どのフォントでも必ず出る。
    /// </summary>
    public const string MarkDone = "■";

    /// <summary>未達成のサブ目標の先頭に付ける記号。</summary>
    public const string MarkTodo = "□";

    // ─── 状態 ────────────────────────────────────────────────

    /// <summary>サブ目標の表示名（例「構えて右を向こう」）。</summary>
    public string Label { get; }

    /// <summary>達成済みか。</summary>
    public bool Done { get; private set; }

    // ─── 生成 ────────────────────────────────────────────────

    /// <summary>
    /// 表示名を指定してサブ目標を作る（初期状態は未達成）。
    /// </summary>
    /// <param name="label">サブ目標の表示名。</param>
    public MissionObjective(string label)
    {
        Label = label ?? string.Empty;
        Done  = false;
    }

    // ─── 状態の変更 ─────────────────────────────────────────

    /// <summary>達成済みにする（既に達成済みなら何も起きない）。</summary>
    public void Complete() { Done = true; }

    /// <summary>未達成へ戻す（ミッションのやり直し時に呼ぶ）。</summary>
    public void Reset() { Done = false; }

    // ─── 表示 ────────────────────────────────────────────────

    /// <summary>
    /// パネルへ出す 1 行の文字列（例「構えて右を向こう ✓」）。
    /// </summary>
    /// <returns>記号付きの 1 行。</returns>
    public string ToDisplayLine() => $"{(Done ? MarkDone : MarkTodo)} {Label}";
}
