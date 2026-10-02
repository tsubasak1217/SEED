using System;

namespace SEED.Scripting;

// ============================================================
//  ScriptSlotProbe.cs — 「アクタのスロットに、この型のスクリプトが居るか」をランタイムへ問い合わせる口
//
//  登録簿が持ち主を知るのは、そのスクリプトの OnStart の直前（参照フィールドの解決）から。
//  まだ OnStart を迎えていないスクリプト（同じフレームで後から OnStart を迎えるもの・AddScript でできて次のフレームを
//  待つもの・非アクティブで一度も動いていないもの）は、持ち主の分からない記録として登録簿に居る。
//  アクタを指定して引くとき（GetScript 系）は、そうした記録の型ごとにこの口で問い合わせ、そのアクタのスロットに
//  居れば結果に混ぜる。エンジンでは [SerializeField] のスクリプト参照と同じ ScriptHost.TryResolveScriptInstance
//  （スロットを先頭から見て、型の名前＝ .cs のファイル名の語幹が一致する最初のもの）を使う。
//  登録簿をエンジンに依存させないため、口は委譲で受け取る（テストでは偽のスロットを渡す）。
// ============================================================

/// <summary>
/// アクタのスロットから、指定した型の名前のスクリプトのインスタンスを引く（無ければ null）。
/// 同じ型の名前のスクリプトが複数あるときは、スロットの先頭に近い 1 つだけを返す。
/// </summary>
/// <typeparam name="TInstance">インスタンスの型。</typeparam>
/// <param name="owner">アクタ（スクリプトが乗るルートのエンティティ）。</param>
/// <param name="scriptType">探すスクリプトの型（ランタイムは型の名前で照合する）。</param>
/// <returns>そのアクタのスロットに居るインスタンス。居ない・問い合わせできないときは null。</returns>
internal delegate TInstance? ScriptSlotProbe<TInstance>(ScriptOwnerKey owner, Type scriptType) where TInstance : class;
