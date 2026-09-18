// ============================================================
//  AutoLockLedger.cs — 「自分が自動で取ったロック」の台帳
//
//  【役割】
//  エディタが利用者に代わって取得したロックのパスを覚えておく。
//  覚えているのは **自動で取ったものだけ** で、パネルから手で取ったロックは
//  絶対に入れない。
//
//  【なぜ台帳が要るのか（これが無いと事故る）】
//  解放のとき「自分が持っているロックを全部外す」としてしまうと、
//  利用者が「このファイルは自分が押さえておきたい」と手で掛けたロックまで
//  シーンを切り替えた拍子に外れる。自動と手動を区別できるのは
//  **取った側の記憶だけ**（Lore のロックにその区別は無い）なので、
//  ここで持つ。
//
//  【もともと自分が持っていたロックを入れない理由】
//  自動取得の結果が <c>AlreadyMine</c> だったときは、そのロックは
//  自動で取ったものではない（手で掛けたか、前のセッションの残り）。
//  台帳へ入れると、閉じたときに勝手に外してしまう。入れるのは
//  <c>Acquired</c>（このエディタが新しく取った）だけ。
//
//  【スレッド】
//  自動取得はワーカースレッドから、解放は UI スレッドや終了処理から呼ばれる。
//  すべての操作をロックで囲って直列化する。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.VersionControl.Locking;

/// <summary>
/// 自動で取得したロックの台帳（スレッドセーフ）。
/// </summary>
public sealed class AutoLockLedger
{
    /// <summary>台帳を触るときのロック。</summary>
    private readonly object _gate = new();

    /// <summary>
    /// 自動で取得したロックのリポジトリ相対パス。
    /// Windows の作業コピーを前提に、大文字小文字は区別しない。
    /// </summary>
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 自動で取得したロックとして記録する。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    /// <returns>新しく記録したら真（すでに記録済みなら偽）。</returns>
    public bool Add(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        lock (_gate) { return _paths.Add(relativePath); }
    }

    /// <summary>
    /// 台帳から外す（解放したとき、または手動ロックへ格上げするとき）。
    /// </summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    /// <returns>台帳にあったら真。</returns>
    public bool Remove(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        lock (_gate) { return _paths.Remove(relativePath); }
    }

    /// <summary>台帳に入っているか。</summary>
    /// <param name="relativePath">リポジトリ相対パス。</param>
    public bool Contains(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        lock (_gate) { return _paths.Contains(relativePath); }
    }

    /// <summary>いま台帳に入っている件数。</summary>
    public int Count
    {
        get { lock (_gate) { return _paths.Count; } }
    }

    /// <summary>
    /// 台帳の中身をコピーして返す（列挙中に変更されても壊れないようにするため）。
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) { return new List<string>(_paths); }
    }

    /// <summary>
    /// 台帳を空にし、それまで入っていたパスを返す（まとめて解放するときに使う）。
    /// </summary>
    public IReadOnlyList<string> TakeAll()
    {
        lock (_gate)
        {
            var all = new List<string>(_paths);
            _paths.Clear();
            return all;
        }
    }
}
