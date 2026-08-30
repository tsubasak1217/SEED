using System;
using System.Collections.Generic;

namespace SEEDEditor.Panels.SpriteRig.Model;

/// <summary>
/// スプライトリグパネルが開いている編集ドキュメント（＝タブ）の集合。
///
/// 「別の画像をインポートしても、編集中のドキュメントは破棄されずタブが増えるだけ」
/// という要件をここ 1 箇所で保証する。UI（TabControl）はこの集合の写しでしかないので、
/// タブ管理の正しさは WPF 抜きで単体テストできる。
///
/// 同一性の判定は次の順で行う:
///   1. すでに同じ <c>.sprite_mesh</c> を開いていればそのタブを再利用する
///   2. まだ保存していないタブで、同じ画像を開いていればそのタブを再利用する
///   3. どちらでもなければ新しいタブを作る
/// </summary>
public sealed class SpriteRigDocumentSet
{
    /// <summary>開いているドキュメント（タブの並び順）。</summary>
    private readonly List<SpriteRigDocument> _documents = new();

    /// <summary>開いているドキュメント一覧（読み取り専用）。</summary>
    public IReadOnlyList<SpriteRigDocument> Documents => _documents;

    /// <summary>現在アクティブなドキュメント（1 つも無ければ null）。</summary>
    public SpriteRigDocument? Active { get; private set; }

    /// <summary>開いているタブ数。</summary>
    public int Count => _documents.Count;

    /// <summary>未保存の変更を持つタブが 1 つでもあるか。</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            foreach (var document in _documents)
            {
                if (document.IsDirty) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// ドキュメントを追加してアクティブにする。既存タブと同一なら追加せずそれを返す。
    /// </summary>
    /// <param name="document">追加するドキュメント。</param>
    /// <returns>実際にアクティブになったドキュメント（既存 or 新規）。</returns>
    public SpriteRigDocument AddOrActivate(SpriteRigDocument document)
    {
        var existing = FindEquivalent(document);
        if (existing != null)
        {
            Active = existing;
            return existing;
        }

        _documents.Add(document);
        Active = document;
        return document;
    }

    /// <summary>
    /// 同じ対象を編集している既存ドキュメントを探す。
    /// </summary>
    /// <param name="document">照合したいドキュメント。</param>
    /// <returns>見つかった既存ドキュメント。無ければ null。</returns>
    private SpriteRigDocument? FindEquivalent(SpriteRigDocument document)
    {
        foreach (var existing in _documents)
        {
            // 保存済み同士は .sprite_mesh のパスで同一判定する
            if (document.MeshPath != null && existing.MeshPath != null &&
                PathEquals(document.MeshPath, existing.MeshPath))
            {
                return existing;
            }
            // どちらも未保存なら、対象画像が同じなら同じ作業とみなす
            if (document.MeshPath == null && existing.MeshPath == null &&
                PathEquals(document.ImagePath, existing.ImagePath))
            {
                return existing;
            }
        }
        return null;
    }

    /// <summary>
    /// 既に開いている <c>.sprite_mesh</c> のタブを探す。
    /// </summary>
    /// <param name="meshPath">探す .sprite_mesh のパス。</param>
    public SpriteRigDocument? FindByMeshPath(string meshPath)
    {
        foreach (var document in _documents)
        {
            if (document.MeshPath != null && PathEquals(document.MeshPath, meshPath)) return document;
        }
        return null;
    }

    /// <summary>
    /// 指定ドキュメントをアクティブにする（集合に無い場合は何もしない）。
    /// </summary>
    /// <param name="document">アクティブにするドキュメント。</param>
    public void Activate(SpriteRigDocument document)
    {
        if (!_documents.Contains(document)) return;
        Active = document;
    }

    /// <summary>
    /// ドキュメントを閉じる。閉じた後は隣のタブがアクティブになる。
    /// </summary>
    /// <param name="document">閉じるドキュメント。</param>
    /// <returns>閉じた場合 true。</returns>
    public bool Close(SpriteRigDocument document)
    {
        int index = _documents.IndexOf(document);
        if (index < 0) return false;

        _documents.RemoveAt(index);
        if (!ReferenceEquals(Active, document)) return true;

        // 閉じたタブがアクティブだった場合は、同じ位置（無ければ最後）のタブへ移す
        if (_documents.Count == 0) Active = null;
        else Active = _documents[Math.Min(index, _documents.Count - 1)];
        return true;
    }

    /// <summary>ファイルパスの同一判定（Windows のため大文字小文字を無視する）。</summary>
    /// <param name="a">比較するパス 1。</param>
    /// <param name="b">比較するパス 2。</param>
    private static bool PathEquals(string a, string b)
        => string.Equals(
            System.IO.Path.GetFullPath(a),
            System.IO.Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
