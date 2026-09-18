// ============================================================
//  MergeContext.cs — いま進行中のマージの「向き」を画面へ渡すための器
//
//  【なぜ必要なのか】
//  マージエディタは「取り込み元」「現在」の 2 列を並べるが、その名前は
//  出どころで変わる:
//    ・sync（最新を取得）      … 取り込み元 = リモート / 現在 = 自分の変更
//    ・ブランチのマージ        … 取り込み元 = 取り込み元ブランチ / 現在 = 現在のブランチ
//  出どころ（<see cref="MergeOrigin"/>）は作業コピーに記録してあるが、
//  「どのブランチを取り込んだのか」は記録していないと分からない。
//  この 2 つを 1 つの器で渡し、画面が推測しないようにする。
//
//  【「両方を取り込む」の並び順にも効く】
//  既に共有されていた側を先に置く、という並び順の決定にも
//  <see cref="MergeOrigin"/> を使う（MergeComposer 参照）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// 進行中のマージの出どころと、取り込み元の名前（不変）。
/// </summary>
/// <param name="Origin">出どころ（sync か ブランチのマージか）。</param>
/// <param name="SourceBranch">
/// 取り込み元のブランチ名。sync のときと、記録が無いときは空文字。
/// </param>
public readonly record struct MergeContext(MergeOrigin Origin, string SourceBranch)
{
    /// <summary>記録が無いときの既定（従来どおり sync 扱い）。</summary>
    public static MergeContext Unknown => new(MergeOrigin.Sync, string.Empty);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => SourceBranch.Length == 0 ? Origin.ToString() : $"{Origin}({SourceBranch})";
}
