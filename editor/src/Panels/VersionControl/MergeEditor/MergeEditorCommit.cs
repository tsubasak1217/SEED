// ============================================================
//  MergeEditorCommit.cs — マージエディタが「確定」で呼ぶ窓口の型
//
//  【なぜ Lore を直接呼ばせないのか】
//  マージエディタは **印つきテキストを合成する画面** であって、
//  バージョン管理の仕組みを知る必要が無い。ここを Lore に繋いでしまうと、
//  ・サンプルの印つきファイルを開いて動作を確かめる、ができなくなる
//  ・作業コピーの外のファイルを開けなくなる
//  ・パネル側の「解決 → 状態の取り直し」の流れが 2 か所に散る
//  ので、確定は「テキストを渡すと結果が返る関数」として外から差す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（型だけの小さなファイル）。
// ============================================================

using System.Threading.Tasks;

namespace SEEDEditor.Panels.VersionControl.MergeEditor;

/// <summary>
/// 確定を頼んだ結果（不変）。
/// </summary>
/// <param name="Succeeded">確定できたか（真ならウィンドウを閉じてよい）。</param>
/// <param name="Message">
/// 利用者へ見せる 1 行。失敗の理由、または成功したことの説明。
/// </param>
public readonly record struct MergeEditorCommitResult(bool Succeeded, string Message)
{
    /// <summary>成功を表す結果を作る。</summary>
    /// <param name="message">利用者へ見せる 1 行。</param>
    public static MergeEditorCommitResult Ok(string message) => new(true, message);

    /// <summary>失敗を表す結果を作る。</summary>
    /// <param name="message">失敗の理由。</param>
    public static MergeEditorCommitResult Failed(string message) => new(false, message);
}

/// <summary>
/// マージエディタの「確定」で呼ばれる処理。
/// </summary>
/// <param name="resolvedText">
/// 合成された結果のテキスト（印を含まない。改行と BOM は元のファイルに合わせてある）。
/// </param>
/// <returns>確定できたか、と利用者へ見せる 1 行。</returns>
public delegate Task<MergeEditorCommitResult> MergeEditorCommitHandler(string resolvedText);
