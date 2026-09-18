// ============================================================
//  ChangedFile.cs — 変更されたファイル 1 件（上の層が扱う唯一の形）
//
//  【役割】
//  Lore の status が返す生の行（path / action / flag_* の 8 個の u8）を、
//  パネルがそのまま一覧に並べられる形へ畳んだもの。
//
//  【設計方針】
//  ・パスは常に「リポジトリルートからの相対パス」。絶対パスを混ぜない
//    （Lore が要求するのは相対パスであり、両方が流れると必ずどこかで取り違える）。
//  ・Lore の flag_conflict / flag_conflict_unresolved / flag_conflict_mine /
//    flag_conflict_theirs の 4 つの組み合わせは、上の層には
//    <see cref="FileConflictState"/> の 1 つの値としてだけ見せる。
//    「mine が実はリモート側」という Lore 固有の逆転をここより上へ漏らさない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.VersionControl.Model;

/// <summary>
/// ファイルに起きた変更の種類。
/// </summary>
public enum FileChangeKind
{
    /// <summary>判別できなかった（新しい Lore が未知の action を返した等）。</summary>
    Unknown,

    /// <summary>追加された。</summary>
    Added,

    /// <summary>内容が変わった。</summary>
    Modified,

    /// <summary>削除された。</summary>
    Deleted,

    /// <summary>移動・改名された（<see cref="ChangedFile.FromPath"/> に元のパスが入る）。</summary>
    Moved,

    /// <summary>複製された（<see cref="ChangedFile.FromPath"/> に元のパスが入る）。</summary>
    Copied,
}

/// <summary>
/// 競合の状態。
///
/// <para>
/// Lore は「競合したか」「未解決か」「どちら側を採ったか」を別々のフラグで返すが、
/// 上の層に必要なのは結局この 1 値なので、変換時に畳んでしまう。
/// </para>
/// </summary>
public enum FileConflictState
{
    /// <summary>競合していない。</summary>
    None,

    /// <summary>競合しており、まだ解決されていない（利用者の選択が要る）。</summary>
    Unresolved,

    /// <summary>競合したが Lore が自動マージで解決した（行が離れている場合など）。</summary>
    AutoMerged,

    /// <summary>競合を「自分の変更を残す」で解決済み。</summary>
    ResolvedKeepMine,

    /// <summary>競合を「リモートを採用」で解決済み。</summary>
    ResolvedTakeRemote,

    /// <summary>
    /// 競合を、作業コピーにある中身のまま解決済み
    /// （マージエディタの結果／「両方を取り込む」／手直しした内容。
    /// Lore の <c>branch merge resolve &lt;path&gt;</c> で mine / theirs を指定しなかった状態）。
    /// </summary>
    ResolvedWithContent,
}

/// <summary>
/// 変更されたファイル 1 件（不変）。
/// </summary>
public sealed class ChangedFile
{
    /// <summary>リポジトリルートからの相対パス。</summary>
    public string Path { get; }

    /// <summary>
    /// 移動・複製の元パス（リポジトリルートからの相対）。
    /// 移動・複製でない場合は空文字。
    /// </summary>
    public string FromPath { get; }

    /// <summary>変更の種類。</summary>
    public FileChangeKind Kind { get; }

    /// <summary>競合の状態。</summary>
    public FileConflictState Conflict { get; }

    /// <summary>
    /// すでに「送信対象」に含まれているか（Lore の staged）。
    /// SEED のパネルは stage の概念を出さないので、表示には使わず、
    /// 「送るものがあるか」の判定にだけ使う。
    /// </summary>
    public bool IsStaged { get; }

    /// <summary>作業ツリー上で実体が変わっているか（Lore の dirty）。</summary>
    public bool IsDirty { get; }

    /// <summary>ファイルサイズ [byte]。ディレクトリや削除では 0 になり得る。</summary>
    public ulong SizeBytes { get; }

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="path">リポジトリ相対パス。</param>
    /// <param name="kind">変更の種類。</param>
    /// <param name="conflict">競合の状態。</param>
    /// <param name="isStaged">送信対象に含まれているか。</param>
    /// <param name="isDirty">作業ツリー上で変わっているか。</param>
    /// <param name="fromPath">移動・複製の元パス（無ければ空文字）。</param>
    /// <param name="sizeBytes">ファイルサイズ。</param>
    public ChangedFile(
        string path,
        FileChangeKind kind,
        FileConflictState conflict = FileConflictState.None,
        bool isStaged = false,
        bool isDirty = false,
        string? fromPath = null,
        ulong sizeBytes = 0)
    {
        Path      = path ?? string.Empty;
        Kind      = kind;
        Conflict  = conflict;
        IsStaged  = isStaged;
        IsDirty   = isDirty;
        FromPath  = fromPath ?? string.Empty;
        SizeBytes = sizeBytes;
    }

    /// <summary>未解決の競合か（パネルが「解決」ボタンを出す条件）。</summary>
    public bool IsUnresolvedConflict => Conflict == FileConflictState.Unresolved;

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => FromPath.Length == 0
            ? $"{Kind} {Path}{(Conflict == FileConflictState.None ? "" : $" [{Conflict}]")}"
            : $"{Kind} {FromPath} -> {Path}";
}
