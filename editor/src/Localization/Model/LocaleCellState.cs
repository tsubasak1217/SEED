// ============================================================
//  LocaleCellState.cs — 文字列表の升目 1 つの状態（値だけの型）
//
//  【未訳の数え方】（docs/localization.md §15）
//    未訳 = 空の文字列（""）・null（訳していない）・その言語の表に無い（欠け）。
//    ただし「要らない」升目は数えない:
//      - 複数形の形のうち、その言語の規則では使わない形（日本語の one など。PluralRules で決まる）
//      - 複数形のまとまりと 1 文のキーのどちらか一方で足りている言語のもう一方
//        （ja は "coins": "コイン {n} 枚" の 1 文、en は coins.one / coins.other。docs/localization.md §5 の ④）
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の升目 1 つの状態。</summary>
public enum LocaleCellState
{
    /// <summary>文がある。</summary>
    Translated,

    /// <summary>空の文字列（""）。実行中はそのまま空の文になる（未訳に数える）。</summary>
    Empty,

    /// <summary>null（訳していない。実行中は次の言語から引く。未訳に数える）。</summary>
    Untranslated,

    /// <summary>その言語の表にキーが無い（実行中は次の言語から引く。未訳に数える）。</summary>
    Absent,

    /// <summary>文は無いが要らない（その言語の複数形の規則で使わない形・別の書き方で足りている）。数えない。</summary>
    NotNeeded,
}

/// <summary><see cref="LocaleCellState"/> の判定。</summary>
public static class LocaleCellStates
{
    /// <summary>未訳に数える状態か（空・null・欠け）。</summary>
    /// <param name="state">状態。</param>
    /// <returns>未訳なら true。</returns>
    public static bool IsMissing(this LocaleCellState state) =>
        state is LocaleCellState.Empty or LocaleCellState.Untranslated or LocaleCellState.Absent;
}
