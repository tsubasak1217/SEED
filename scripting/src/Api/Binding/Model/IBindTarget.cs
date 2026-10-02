namespace SEED.Binding;

// ============================================================
//  IBindTarget.cs — 結び付けの当てる先（UI 部品の代わりに挟む小さな口）
//
//  Bind.Text・Bind.Visible・Bind.Toggle… は、部品を直接触らずこの口を通して書く。
//    - 実行中は Binding/Targets/ の小さな型（TextTarget・ToggleTarget…）が部品を包む。
//    - テスト（editor/tests/BindingTests）は偽物を差して、エンジン無しで結び付けの規則を確かめる。
//    - アプリの自作の部品も、この口を実装すれば Bind.OneWay / Bind.TwoWay で結べる。
// ============================================================

/// <summary>一方向の結び付け（値 → 当てる先）の当てる先。</summary>
/// <typeparam name="T">当てる値の型。</typeparam>
public interface IBindTarget<T>
{
    /// <summary>
    /// 当てる先がまだあるか。false になったら結び付けは自分を外す（破棄した GameObject・部品へ書き続けない）。
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// 今当てられるか（部品のスクリプトの OnStart の前・選択の項目がまだ集まっていない等は false）。
    /// false の間は書かずに待ち、フレームの区切り（BindingFrame.Tick）ごとに確かめて、当てられるようになったら最新の値を当てる。
    /// </summary>
    bool IsReady { get; }

    /// <summary>値を当てる（IsAlive かつ IsReady のときだけ呼ばれる）。</summary>
    /// <param name="value">当てる値。</param>
    void Write(T value);
}
