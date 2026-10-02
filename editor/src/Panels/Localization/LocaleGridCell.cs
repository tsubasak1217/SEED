// ============================================================
//  LocaleGridCell.cs — 文字列表の升目 1 つの表示用の入れ物（DataGrid の列が Cells[i].Text で結ぶ）
//
//  【書き換えの流れ】
//  DataGrid の升目の編集を確定すると、結び付け（Binding）が Text の setter を呼ぶ。setter は自分では値を持たず、
//  パネルへ渡す（_commit）。パネルはモデル（LocaleTableModel.SetCell）を書き換え、組み直した行の値を Apply で
//  入れ直す（未訳の状態が変わる・ほかの升目の「要らない」が変わる、を 1 か所で反映するため）。
//  書き換えを断られたら Apply で元の値に戻る。
// ============================================================

using System;
using System.ComponentModel;
using SEEDEditor.Localization.Model;

namespace SEEDEditor.Panels.Localization;

/// <summary>文字列表の升目 1 つ（表示用）。</summary>
public sealed class LocaleGridCell : INotifyPropertyChanged
{
    /// <summary>編集の確定をパネルへ渡す（升目・新しい文）。</summary>
    private readonly Action<LocaleGridCell, string?> _commit;

    /// <summary>今の値（モデルから入れたもの）。</summary>
    private LocaleCell _cell;

    /// <summary>キー。</summary>
    public string Key { get; }

    /// <summary>言語のコード。</summary>
    public string Code => _cell.Code;

    /// <summary>升目を作る。</summary>
    /// <param name="key">キー。</param>
    /// <param name="cell">モデルの升目。</param>
    /// <param name="commit">編集の確定をパネルへ渡す関数。</param>
    public LocaleGridCell(string key, LocaleCell cell, Action<LocaleGridCell, string?> commit)
    {
        Key = key;
        _cell = cell;
        _commit = commit;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>文（編集の確定で setter が呼ばれ、パネルがモデルを書き換える）。</summary>
    public string? Text
    {
        get => _cell.Text;
        set
        {
            // 変わっていなければ何もしない（null と空は同じ「文が無い」）
            if (string.Equals(value ?? string.Empty, _cell.Text ?? string.Empty, StringComparison.Ordinal)) return;
            _commit(this, value);
        }
    }

    /// <summary>未訳か（升目の地の色）。</summary>
    public bool IsMissing => _cell.IsMissing;

    /// <summary>要らない升目か。</summary>
    public bool IsNotNeeded => _cell.State == LocaleCellState.NotNeeded;

    /// <summary>升目のツールチップ（状態の説明。文があれば文。どちらも無ければ null）。</summary>
    public string? ToolTip => _cell.Note.Length > 0 ? _cell.Note : string.IsNullOrEmpty(_cell.Text) ? null : _cell.Text;

    /// <summary>モデルの升目を入れ直す（変わったところだけ知らせる）。</summary>
    /// <param name="cell">モデルの升目。</param>
    public void Apply(LocaleCell cell)
    {
        var before = _cell;
        _cell = cell;
        if (!string.Equals(before.Text, cell.Text, StringComparison.Ordinal)) Raise(nameof(Text));
        if (before.State != cell.State)
        {
            Raise(nameof(IsMissing));
            Raise(nameof(IsNotNeeded));
        }
        if (!string.Equals(before.Note, cell.Note, StringComparison.Ordinal) || !string.Equals(before.Text, cell.Text, StringComparison.Ordinal))
            Raise(nameof(ToolTip));
    }

    /// <summary>今の値を知らせ直す（書き換えを断られたとき、升目の表示を元へ戻す）。</summary>
    public void RaiseTextAgain() => Raise(nameof(Text));

    /// <summary>変わったことを知らせる。</summary>
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
