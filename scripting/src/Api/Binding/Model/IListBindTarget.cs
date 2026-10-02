namespace SEED.Binding;

// ============================================================
//  IListBindTarget.cs — 一覧の結び付け（ObservableList → 行の並び）の当てる先
//
//  実行中は ListViewTarget が SEED.UI.ListView の「行の数」（SetCount）と「中身の入れ直し」（Refresh）の口を呼ぶ。
//  テストは偽物を差して、変化の種類ごとにどの口が呼ばれるかを確かめる。
// ============================================================

/// <summary>一覧の結び付けの当てる先（行の並び）。</summary>
public interface IListBindTarget
{
    /// <summary>当てる先がまだあるか（false になったら結び付けは自分を外す）。</summary>
    bool IsAlive { get; }

    /// <summary>行の数を合わせ、見えている行の中身を入れ直す（入れた・外した・全体が変わった）。</summary>
    /// <param name="count">一覧の今の数。</param>
    void SetCount(int count);

    /// <summary>行の数はそのまま、見えている行の中身を入れ直す（項目が動いた）。</summary>
    void Refresh();

    /// <summary>1 行の中身を入れ直す（項目を置き換えた。見えていなければ何もしない）。</summary>
    /// <param name="index">位置。</param>
    void RebindRow(int index);
}
