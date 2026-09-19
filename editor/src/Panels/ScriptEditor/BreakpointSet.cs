using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Document;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// 1 つのドキュメントに設定されたブレークポイント集合。
///
/// 行番号ではなく <see cref="TextAnchor"/> で位置を保持するため、上の行を編集
/// （挿入・削除）してもブレークポイントが正しい行に追従する。永続化時は現在の
/// 行番号へ変換して保存する。
///
/// ここでは「どの行に張られているか」だけを管理する（実際の停止・検査は将来の
/// デバッガバックエンドが担う）。
/// </summary>
public sealed class BreakpointSet
{
    private readonly TextDocument _document;
    private readonly List<TextAnchor> _anchors = new();

    /// <summary>ドキュメントと初期行（永続化から復元）でブレークポイント集合を作る。</summary>
    public BreakpointSet(TextDocument document, IEnumerable<int>? initialLines = null)
    {
        _document = document;
        if (initialLines is null) return;
        foreach (var line in initialLines)
            AddAnchor(line);
    }

    /// <summary>指定行にブレークポイントがあるか。</summary>
    public bool Contains(int line)
        => _anchors.Any(a => !a.IsDeleted && a.Line == line);

    /// <summary>指定行のブレークポイントを切り替える（無ければ追加、あれば削除）。</summary>
    public void Toggle(int line)
    {
        var existing = _anchors.FirstOrDefault(a => !a.IsDeleted && a.Line == line);
        if (existing is not null) _anchors.Remove(existing);
        else                      AddAnchor(line);
    }

    /// <summary>現在ブレークポイントが張られている行番号（昇順・重複なし）。</summary>
    public IReadOnlyList<int> Lines()
        => _anchors.Where(a => !a.IsDeleted)
                   .Select(a => a.Line)
                   .Distinct()
                   .OrderBy(n => n)
                   .ToList();

    /// <summary>
    /// 現在のアンカーを全て捨て、指定行で張り直す。
    ///
    /// ディスク上でファイルが書き換わったときの再読み込みのように本文を丸ごと
    /// 差し替えると、アンカーが全て先頭へ寄ってブレークポイントが 1 行目に集まる。
    /// 差し替えの前に <see cref="Lines"/> で行を控えておき、後からこれで張り直す。
    /// 新しい本文の行数を超える行は落とす（短くなったファイルでの復元用）。
    /// </summary>
    /// <param name="lines">張り直す行番号（1 起点）。</param>
    public void ResetTo(IEnumerable<int> lines)
    {
        _anchors.Clear();
        foreach (var line in lines) AddAnchor(line);
    }

    /// <summary>指定行の先頭にアンカーを作って追加する（行が有効な範囲のときのみ）。</summary>
    private void AddAnchor(int line)
    {
        if (line < 1 || line > _document.LineCount) return;
        var offset = _document.GetLineByNumber(line).Offset;
        var anchor = _document.CreateAnchor(offset);
        // 行を消しても消滅せず、行頭に留まるようにする（ブレークポイントの追従用）。
        anchor.SurviveDeletion = true;
        anchor.MovementType    = AnchorMovementType.AfterInsertion;
        _anchors.Add(anchor);
    }
}
