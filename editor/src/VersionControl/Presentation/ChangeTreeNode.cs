// ============================================================
//  ChangeTreeNode.cs — 変更ファイルのパス群 → フォルダー階層のツリー
//
//  【役割】
//  「変更されたファイルのリポジトリ相対パス」という平坦な並びを、
//  Visual Studio の「Git 変更」と同じ **フォルダー階層のツリー** へ組み替える。
//  ルートは作業コピーのパス、その下にフォルダー、葉にファイルが並ぶ。
//
//  【なぜフォルダー階層にするのか】
//  平坦な一覧は 10 件までなら読めるが、数百件になると
//  「どのフォルダーが荒れているのか」が分からなくなる。
//  アセットは assets/textures/… のように役割ごとにフォルダーが分かれているので、
//  階層で束ねると「今回さわったのはここ」が一目で分かる。
//
//  【なぜ WPF に依存させないのか】
//  パスを区切って木を組む処理は純粋な変換であり、ビューの都合を持たない。
//  ここを切り出しておけば「a/b/c.png と a/d.png が正しく 1 本の a の下に入るか」
//  「並び順が毎回同じか」を単体テストで固定できる（GUI を起動せずに確かめられる）。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.VersionControl.Model;

namespace SEEDEditor.VersionControl.Presentation;

/// <summary>
/// 変更ツリーの節点の種類。
/// </summary>
public enum ChangeTreeNodeKind
{
    /// <summary>ツリーの根（作業コピーのパスを表示する行）。</summary>
    Root,

    /// <summary>フォルダー。</summary>
    Folder,

    /// <summary>ファイル（葉）。</summary>
    File,
}

/// <summary>
/// 変更ツリーの節点 1 つ（不変）。
/// </summary>
public sealed class ChangeTreeNode
{
    /// <summary>節点の種類。</summary>
    public ChangeTreeNodeKind Kind { get; }

    /// <summary>
    /// 行に出す名前。
    /// 根は作業コピーのパス、フォルダーとファイルは階層 1 段ぶんの名前。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// リポジトリ相対パス（スラッシュ区切り）。根は空文字。
    /// 開閉状態の保存キーと、右クリックメニューの対象にこれを使う。
    /// </summary>
    public string RelativePath { get; }

    /// <summary>子（フォルダーが先、名前順）。ファイルでは空。</summary>
    public IReadOnlyList<ChangeTreeNode> Children { get; }

    /// <summary>対応する変更（ファイル節点のときだけ。それ以外は null）。</summary>
    public ChangedFile? File { get; }

    /// <summary>この節点の配下にあるファイルの総数（根・フォルダーの件数表示に使う）。</summary>
    public int FileCount { get; }

    /// <summary>子を持つか（開閉ハンドルを出すかの判定に使う）。</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>フォルダーまたは根か（＝畳める節点か）。</summary>
    public bool IsContainer => Kind != ChangeTreeNodeKind.File;

    /// <summary>全項目を指定して生成する。</summary>
    /// <param name="kind">節点の種類。</param>
    /// <param name="name">表示名。</param>
    /// <param name="relativePath">リポジトリ相対パス（根は空文字）。</param>
    /// <param name="children">子の並び。</param>
    /// <param name="file">対応する変更（ファイル節点のときだけ）。</param>
    public ChangeTreeNode(
        ChangeTreeNodeKind kind,
        string name,
        string relativePath,
        IReadOnlyList<ChangeTreeNode>? children = null,
        ChangedFile? file = null)
    {
        Kind         = kind;
        Name         = name         ?? string.Empty;
        RelativePath = relativePath ?? string.Empty;
        Children     = children     ?? Array.Empty<ChangeTreeNode>();
        File         = file;

        FileCount = kind == ChangeTreeNodeKind.File
            ? 1
            : Children.Sum(c => c.FileCount);
    }

    /// <summary>ログ向けの 1 行表現。</summary>
    public override string ToString()
        => $"{Kind} {RelativePath} (files={FileCount}, children={Children.Count})";
}

/// <summary>
/// 変更ファイルの一覧からフォルダー階層のツリーを組み立てる純関数。
/// </summary>
public static class ChangeTreeBuilder
{
    /// <summary>パスの区切り（Lore はスラッシュで返すが、念のため両方を受ける）。</summary>
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>ツリーのパスを組み立てるときに使う区切り（保存キーにもなるので固定する）。</summary>
    public const char PATH_SEPARATOR = '/';

    /// <summary>
    /// 変更の一覧をフォルダー階層のツリーへ組み替える。
    ///
    /// <para>
    /// 並び順は「フォルダーが先、そのあとファイル。どちらも名前の辞書順
    /// （大文字小文字を区別しない）」に固定する。Lore が返す順は内部表現に依存し
    /// 更新のたびに入れ替わり得るため、ここで固定しないと利用者が同じ行を目で追えない。
    /// </para>
    /// </summary>
    /// <param name="workingCopyRoot">根の行に出す作業コピーのパス（空なら空文字の根になる）。</param>
    /// <param name="changes">変更されたファイル。</param>
    /// <returns>根の節点（変更が無ければ子を持たない根）。</returns>
    public static ChangeTreeNode Build(
        string? workingCopyRoot, IReadOnlyList<ChangedFile>? changes)
    {
        var builder = new MutableNode(string.Empty, string.Empty);

        if (changes is not null)
        {
            foreach (var change in changes)
            {
                Insert(builder, change);
            }
        }

        var children = builder.Freeze();

        return new ChangeTreeNode(
            ChangeTreeNodeKind.Root,
            workingCopyRoot ?? string.Empty,
            string.Empty,
            children);
    }

    /// <summary>
    /// 1 件の変更をツリーへ差し込む（途中のフォルダーは必要なだけ作る）。
    /// </summary>
    /// <param name="root">差し込み先の根（可変）。</param>
    /// <param name="change">差し込む変更。</param>
    private static void Insert(MutableNode root, ChangedFile change)
    {
        var segments = change.Path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

        // パスが空（Lore が壊れた行を返した等）なら木に入れようがないので落とす。
        // 落とした件数は見出しの件数（ChangeListBuilder 側）と食い違うが、
        // 表示できない行を「名前なし」で並べるよりは静かに捨てる方がよい。
        if (segments.Length == 0) return;

        var current = root;

        // 最後の 1 つを除く = 途中のフォルダー。
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current.GetOrAddFolder(segments[i]);
        }

        current.AddFile(segments[^1], change);
    }

    /// <summary>
    /// 組み立て中の可変な節点。<see cref="Freeze"/> で不変のツリーへ畳む。
    ///
    /// <para>
    /// 木を組む間だけ辞書と可変リストが要る。完成品（<see cref="ChangeTreeNode"/>）へ
    /// この都合を持ち込みたくないので、組み立て用の型を内側に分けている。
    /// </para>
    /// </summary>
    private sealed class MutableNode
    {
        /// <summary>この節点の名前（階層 1 段ぶん）。</summary>
        private readonly string _name;

        /// <summary>この節点までのリポジトリ相対パス。</summary>
        private readonly string _path;

        /// <summary>子フォルダー（名前 → 節点）。同じフォルダーを二重に作らないための索引。</summary>
        private readonly Dictionary<string, MutableNode> _folders =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>子ファイル（名前, 変更）。</summary>
        private readonly List<(string Name, ChangedFile File)> _files = new();

        /// <summary>名前とパスを指定して生成する。</summary>
        /// <param name="name">節点の名前。</param>
        /// <param name="path">この節点までのリポジトリ相対パス。</param>
        public MutableNode(string name, string path)
        {
            _name = name;
            _path = path;
        }

        /// <summary>子フォルダーを取り出す（無ければ作る）。</summary>
        /// <param name="name">フォルダー名。</param>
        public MutableNode GetOrAddFolder(string name)
        {
            if (_folders.TryGetValue(name, out var existing)) return existing;

            var child = new MutableNode(name, Combine(_path, name));
            _folders.Add(name, child);
            return child;
        }

        /// <summary>子ファイルを足す。</summary>
        /// <param name="name">ファイル名。</param>
        /// <param name="file">対応する変更。</param>
        public void AddFile(string name, ChangedFile file) => _files.Add((name, file));

        /// <summary>
        /// 子を「フォルダーが先・名前順」に並べて不変の節点へ畳む。
        /// </summary>
        public IReadOnlyList<ChangeTreeNode> Freeze()
        {
            var result = new List<ChangeTreeNode>(_folders.Count + _files.Count);

            foreach (var folder in _folders.Values
                                           .OrderBy(f => f._name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new ChangeTreeNode(
                    ChangeTreeNodeKind.Folder, folder._name, folder._path, folder.Freeze()));
            }

            foreach (var (name, file) in _files
                                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(new ChangeTreeNode(
                    ChangeTreeNodeKind.File, name, Combine(_path, name), null, file));
            }

            return result;
        }

        /// <summary>親のパスと名前を繋ぐ（親が根なら名前だけ）。</summary>
        /// <param name="parentPath">親までの相対パス。</param>
        /// <param name="name">足す名前。</param>
        private static string Combine(string parentPath, string name)
            => parentPath.Length == 0 ? name : parentPath + PATH_SEPARATOR + name;
    }
}
