// ============================================================
//  LockInfo.cs — ロック 1 件と、取得を試みた結果
//
//  【役割】
//  「このファイルは誰かが編集中か」を上の層へ伝える。
//
//  【所有者が分からない場合があること（重要）】
//  Lore サーバに `[server.auth]`（JWT / OIDC）を設定しない構成では、
//  ロックの所有者も push したユーザーも <c>&lt;unknown&gt;</c> になる。
//  この状態では「他人のロック」という概念が成立しないので、
//  <see cref="IsUnknownOwner"/> を真にして UI 側が
//  「誰かが編集中（利用者不明）」と言えるようにしておく。
//  ここを bool の「自分のものか」だけで表すと、認証が無い環境で
//  必ず嘘をつくことになるため、3 値（自分 / 他人 / 不明）で持つ。
//
//  【二重取得について】
//  Lore の lock acquire は既に取得済みでも成功（終了コード 0）を返す。
//  したがって「取得できた」と「もともと誰かが持っていた」は終了コードでは
//  区別できない。取得後に status を引き直して所有者を見る必要があり、
//  その判定結果が <see cref="LockAcquireOutcome"/>。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// ロックの持ち主が誰なのか。
/// </summary>
public enum LockHolder
{
    /// <summary>ロックされていない。</summary>
    None,

    /// <summary>自分が持っている（解放できる）。</summary>
    Self,

    /// <summary>他の利用者が持っている。</summary>
    Other,

    /// <summary>
    /// 誰かが持っているが、誰かは分からない。
    /// サーバ認証が無い構成（所有者が <c>&lt;unknown&gt;</c>）で起こる。
    /// </summary>
    Unknown,
}

/// <summary>
/// ロック取得を試みた結果。
/// </summary>
public enum LockAcquireOutcome
{
    /// <summary>取得できた（自分が持っている）。</summary>
    Acquired,

    /// <summary>もともと自分が持っていた（何も変わっていない）。</summary>
    AlreadyMine,

    /// <summary>他の利用者が持っていたため取得できなかった。</summary>
    HeldByOther,

    /// <summary>
    /// 誰かが持っているが所有者が不明で、自分のものかどうか判断できない。
    /// サーバ認証を入れるまではこの結果が出る。
    /// </summary>
    HeldByUnknown,

    /// <summary>エラーで取得できなかった。</summary>
    Failed,
}

/// <summary>
/// ロック 1 件（不変）。
/// </summary>
public sealed class LockInfo
{
    /// <summary>Lore が所有者不明のときに返す文字列。</summary>
    public const string UNKNOWN_OWNER = "<unknown>";

    /// <summary>ロック対象のリポジトリ相対パス。</summary>
    public string Path { get; }

    /// <summary>所有者の表示名（Lore が返した値をそのまま保持する）。</summary>
    public string Owner { get; }

    /// <summary>誰が持っているか（判定済み）。</summary>
    public LockHolder Holder { get; }

    /// <summary>ロックが取得されたブランチ名。</summary>
    public string BranchName { get; }

    /// <summary>取得時刻（UTC）。取得できなければ既定値。</summary>
    public DateTime AcquiredAtUtc { get; }

    /// <summary>所有者が不明か（サーバ認証なしの構成）。</summary>
    public bool IsUnknownOwner => Holder == LockHolder.Unknown;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="path">リポジトリ相対パス。</param>
    /// <param name="owner">所有者の表示名。</param>
    /// <param name="holder">誰が持っているか。</param>
    /// <param name="branchName">ブランチ名。</param>
    /// <param name="acquiredAtUtc">取得時刻（UTC）。</param>
    public LockInfo(
        string path, string? owner, LockHolder holder,
        string? branchName = null, DateTime acquiredAtUtc = default)
    {
        Path          = path  ?? string.Empty;
        Owner         = owner ?? string.Empty;
        Holder        = holder;
        BranchName    = branchName ?? string.Empty;
        AcquiredAtUtc = acquiredAtUtc;
    }

    /// <summary>
    /// Lore が返した所有者名が「不明」を表すか判定する。
    ///
    /// <para>
    /// 判定を 1 か所へ閉じ込めるための共通関数。呼び出し側で
    /// <c>owner == "&lt;unknown&gt;"</c> を書かないこと。
    /// </para>
    /// </summary>
    /// <param name="owner">Lore が返した所有者名。</param>
    public static bool IsUnknownOwnerName(string? owner)
        => string.IsNullOrWhiteSpace(owner)
           || string.Equals(owner.Trim(), UNKNOWN_OWNER, StringComparison.OrdinalIgnoreCase);

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"{Path} <- {Owner} ({Holder})";
}

/// <summary>
/// ロック取得の結果（不変）。結末と、判定の根拠になったロック情報を持つ。
/// </summary>
public sealed class LockAcquireResult
{
    /// <summary>結末。</summary>
    public LockAcquireOutcome Outcome { get; }

    /// <summary>対象のロック情報（取得できなかった場合は既存の持ち主が入る）。</summary>
    public LockInfo Lock { get; }

    /// <summary>結末とロック情報を指定して生成する。</summary>
    /// <param name="outcome">結末。</param>
    /// <param name="lockInfo">対象のロック情報。</param>
    public LockAcquireResult(LockAcquireOutcome outcome, LockInfo lockInfo)
    {
        Outcome = outcome;
        Lock    = lockInfo;
    }

    /// <summary>編集してよいか（自分が持っている状態になったか）。</summary>
    public bool CanEdit
        => Outcome is LockAcquireOutcome.Acquired or LockAcquireOutcome.AlreadyMine;

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString() => $"{Outcome}: {Lock}";
}
