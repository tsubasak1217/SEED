namespace SEED;

/// <summary>
/// セーブデータを読み込んだとき、どこから読んだか（<see cref="SaveData.RecoveredFrom"/>）。
///
/// 数値は Rust 側 <c>runtime/src/engine/core/save/recovery.rs</c> の <c>RECOVERY_CODE_*</c> と一致させること。
/// </summary>
public enum SaveRecovery
{
    /// <summary>普段どおり本体（save.json）を読めた、またはまだ保存が無い（初めての起動）。</summary>
    None = 0,

    /// <summary>
    /// 本体が無い・壊れていたので、1 つ前の世代（save.json.bak）から読んだ。
    /// 直前の保存（最後の 1 回ぶん）が失われている可能性がある。次の書き出しで本体は作り直される。
    /// </summary>
    Backup = 1,

    /// <summary>
    /// 本体か 1 つ前の世代が有ったのに、どれも読めなかった。空の状態で始めた。
    /// 壊れた本体は上書きせずに save.json.corrupt-&lt;時刻&gt; として残してある（中身は手で調べられる）。
    /// </summary>
    Lost = 2,
}
