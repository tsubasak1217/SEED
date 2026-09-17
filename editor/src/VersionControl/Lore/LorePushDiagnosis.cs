// ============================================================
//  LorePushDiagnosis.cs — push が失敗した理由の判定
//
//  【役割】
//  push の失敗を「先に最新を取得すれば直る」ものと、それ以外に切り分ける。
//
//  【なぜ戻り値コードだけでは足りないのか】
//  Lore は分岐（divergent）をブランチ操作のエラーとして扱い、
//  lore-revision/src/branch.rs で
//      BranchError::Divergent(_) => LoreError::InvalidArguments
//  と、汎用の「引数が不正」へ畳んでしまう。
//  つまり「ブランチ名が間違っている」も「分岐している」も同じコードになるため、
//  コードだけで判断すると誤って NeedsSync を返す。
//  そこで Lore が返すメッセージ（lore-base/src/error.rs の
//  #[error("Branch history is divergent")] など）も併せて見る。
//
//  【文字列一致に頼ることの危うさ（承知の上での選択）】
//  Lore のメッセージ文言は pre-1.0 なので変わり得る。変わった場合、
//  この判定は NeedsSync ではなく Failed を返す（= 利用者には「送信できませんでした」
//  と出る）。誤って NeedsSync を返して sync を促し、不要なマージを起こすより安全側。
//  マーカーは下の配列 1 か所にまとめてあるので、文言が変わったらここだけ直す。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.VersionControl.Lore.Backend;

namespace SEEDEditor.VersionControl.Lore;

/// <summary>
/// push の失敗理由。
/// </summary>
public enum PushFailureKind
{
    /// <summary>失敗していない。</summary>
    None,

    /// <summary>
    /// リモートが進んでいる／分岐しているため送れない。
    /// 先に「最新を取得」すれば解消する。
    /// </summary>
    NeedsSync,

    /// <summary>中断・期限切れ。</summary>
    Canceled,

    /// <summary>上記以外の失敗。</summary>
    Other,
}

/// <summary>
/// push の失敗理由を判定する純粋関数。
/// </summary>
public static class LorePushDiagnosis
{
    /// <summary>
    /// 「先に最新を取得すべき」ことを示す Lore のメッセージ断片。
    ///
    /// <para>
    /// 出典（Lore v0.9.0 のソース）:
    /// <list type="bullet">
    ///   <item>lore-base/src/error.rs: <c>#[error("Branch history is divergent")]</c></item>
    ///   <item>lore-client/src/cli/commands/repository.rs:
    ///         "Local branch has diverged, synchronize to merge" / "Local branch is behind remote"</item>
    /// </list>
    /// 部分一致・大文字小文字を区別しない比較で使う。
    /// </para>
    /// </summary>
    public static readonly string[] NEEDS_SYNC_MARKERS =
    {
        "divergent",        // "Branch history is divergent"
        "diverged",         // "Local branch has diverged, synchronize to merge"
        "behind remote",    // "Local branch is behind remote"
        "not a fast-forward",
        "fast forward",
    };

    /// <summary>
    /// push の呼び出し結果から失敗理由を判定する。
    /// </summary>
    /// <param name="pushResult">push の呼び出しの結末。</param>
    /// <returns>失敗理由。</returns>
    public static PushFailureKind Diagnose(LoreCallResult pushResult)
    {
        ArgumentNullException.ThrowIfNull(pushResult);

        if (pushResult.Succeeded) return PushFailureKind.None;
        if (pushResult.WasCanceled) return PushFailureKind.Canceled;

        foreach (var marker in NEEDS_SYNC_MARKERS)
        {
            if (pushResult.MessagesContain(marker)) return PushFailureKind.NeedsSync;
        }

        return PushFailureKind.Other;
    }

    /// <summary>
    /// 「先に最新を取得すべき」か。
    ///
    /// <para>
    /// メッセージからは判定できなかったが、push の直前に取った状態で
    /// リモートが進んでいたことが分かっている場合も真にする。
    /// メッセージ文言の変化に対する保険。
    /// </para>
    /// </summary>
    /// <param name="pushResult">push の呼び出しの結末。</param>
    /// <param name="remoteWasAhead">
    /// push の直前の状態でリモートが進んでいた（または分岐していた）か。
    /// 分からない場合は偽を渡す。
    /// </param>
    public static bool NeedsSync(LoreCallResult pushResult, bool remoteWasAhead)
    {
        var kind = Diagnose(pushResult);
        if (kind == PushFailureKind.NeedsSync) return true;

        // 中断は「sync すれば直る」話ではないので、ここでは真にしない。
        return kind == PushFailureKind.Other && remoteWasAhead;
    }
}
