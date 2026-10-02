namespace SEED.Binding;

// ============================================================
//  ListChangeKind.cs — 一覧（ObservableList）の変化の種類
// ============================================================

/// <summary>一覧の変化の種類。</summary>
public enum ListChangeKind
{
    /// <summary>項目を入れた（Add・Insert。Index = 入れた位置、Item = 入れた項目）。</summary>
    Insert = 0,

    /// <summary>項目を外した（RemoveAt・Remove。Index = 外した位置、OldItem = 外した項目）。</summary>
    Remove = 1,

    /// <summary>項目を置き換えた（this[i] の set。Index = 位置、Item = 新しい項目、OldItem = 前の項目）。</summary>
    Replace = 2,

    /// <summary>項目を動かした（Move。OldIndex = 元の位置、Index = 移した先、Item = 動かした項目）。</summary>
    Move = 3,

    /// <summary>全体が変わった（Clear・ReplaceAll。位置は −1。今の一覧を読み直す）。</summary>
    Reset = 4,
}
