using System;

namespace SEED.Binding;

// ============================================================
//  ITwoWayBindTarget.cs — 双方向の結び付けの当てる先（利用者の操作でも値が変わる部品）
//
//  トグル・チェックボックス・スライダ・文字の欄・選択のグループ。値 → 部品は Write、部品 → 値は Listen で受ける。
//  往復（値 → 部品 → 値 → …）は結び付けの側の留め金（TwoWayBinding）で止めるので、部品の側は気にしなくてよい。
// ============================================================

/// <summary>双方向の結び付けの当てる先。</summary>
/// <typeparam name="T">値の型。</typeparam>
public interface ITwoWayBindTarget<T> : IBindTarget<T>
{
    /// <summary>
    /// 部品の側で値が変わったら（利用者の操作）handler を呼ぶようにする。戻り値を Dispose すると外す。
    /// IsReady のときだけ呼ばれ、結び付け 1 つにつき 1 回だけ呼ばれる。
    /// </summary>
    /// <param name="handler">部品の新しい値を受け取る処理。</param>
    /// <returns>外す口。</returns>
    IDisposable Listen(Action<T> handler);
}
