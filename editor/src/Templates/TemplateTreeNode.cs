// ============================================================
//  TemplateTreeNode.cs — インポート画面のツリー表示モデル
//
//  【役割】
//  TemplateImportWindow の左ペインに出すチェックボックス付きツリーの 1 ノード。
//  カテゴリノード（フォルダ名）とエントリノード（テンプレート 1 件）を同じ型で表す。
//
//  【なぜ表示モデルを分けるか】
//  走査結果（TemplateLibrary / TemplateEntry）は不変の値オブジェクトで、
//  「チェックされたか」「検索に一致するか」といった画面都合の状態を持たない。
//  その可変状態をここへ閉じ込めることで、走査・インポートのロジックを
//  WPF 非依存のまま保てる（単体テストが UI を参照せずに済む）。
//
//  【チェック状態の伝播】
//  ・カテゴリをチェック / 解除 → 配下エントリすべてに伝播する
//  ・エントリを変更          → 親カテゴリを 3 状態（全 / 一部 = null / 無）へ更新する
//  再入を避けるため、伝播方向を引数で制御する（SetIsChecked）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SEEDEditor.Templates;

/// <summary>
/// インポート画面のツリー 1 ノード（カテゴリまたはエントリ）。
/// </summary>
public sealed class TemplateTreeNode : INotifyPropertyChanged
{
    // ============================================================
    //  不変の情報
    // ============================================================

    /// <summary>UI に出す名前（カテゴリ表示名 / エントリ名）。</summary>
    public string DisplayName { get; }

    /// <summary>右側に薄く出す補足（件数・サイズ）。</summary>
    public string DetailText { get; }

    /// <summary>
    /// エントリのライブラリ相対パス。カテゴリノードは null。
    /// インポート時にそのまま <see cref="TemplateImporter.CreatePlan"/> へ渡す。
    /// </summary>
    public string? RelPath { get; }

    /// <summary>行頭に出すアイコン（ファイル形式 / フォルダ）。</summary>
    public ImageSource? Icon { get; }

    /// <summary>子ノード（カテゴリのときだけ中身がある）。</summary>
    public ObservableCollection<TemplateTreeNode> Children { get; } = [];

    /// <summary>親ノード（カテゴリノードは null）。</summary>
    public TemplateTreeNode? Parent { get; private set; }

    /// <summary>チェック状態が変わったときに呼ぶ通知（画面側の再計算用）。</summary>
    private readonly Action? _onCheckedChanged;

    /// <summary>カテゴリノード（＝エントリを持たない枝）なら true。</summary>
    public bool IsCategory => RelPath is null;

    // ============================================================
    //  可変の状態
    // ============================================================

    /// <summary>チェック状態（カテゴリは一部選択で null になる 3 状態）。</summary>
    private bool? _isChecked = false;

    /// <inheritdoc cref="_isChecked"/>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetIsChecked(value, updateChildren: true, updateParent: true);
    }

    /// <summary>展開状態。</summary>
    private bool _isExpanded = true;

    /// <inheritdoc cref="_isExpanded"/>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Raise(); }
    }

    /// <summary>検索絞り込みの結果として表示するか。</summary>
    private bool _isVisible = true;

    /// <inheritdoc cref="_isVisible"/>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible == value) return; _isVisible = value; Raise(); }
    }

    // ============================================================
    //  構築
    // ============================================================

    /// <summary>ノードを作る（生成は <see cref="CreateCategory"/> / <see cref="CreateEntry"/> から行う）。</summary>
    /// <param name="displayName">表示名。</param>
    /// <param name="detailText">補足表示。</param>
    /// <param name="relPath">エントリの相対パス（カテゴリは null）。</param>
    /// <param name="icon">行頭アイコン。</param>
    /// <param name="onCheckedChanged">チェック変更の通知先。</param>
    private TemplateTreeNode(
        string displayName, string detailText, string? relPath,
        ImageSource? icon, Action? onCheckedChanged)
    {
        DisplayName       = displayName;
        DetailText        = detailText;
        RelPath           = relPath;
        Icon              = icon;
        _onCheckedChanged = onCheckedChanged;
    }

    /// <summary>カテゴリノードを作る。</summary>
    /// <param name="category">走査結果のカテゴリ。</param>
    /// <param name="icon">フォルダアイコン。</param>
    /// <param name="onCheckedChanged">チェック変更の通知先。</param>
    /// <returns>作成したノード（子はまだ空）。</returns>
    public static TemplateTreeNode CreateCategory(
        TemplateCategory category, ImageSource? icon, Action? onCheckedChanged) =>
        new($"{category.DisplayName}（{category.FolderName}）",
            $"{category.Entries.Count} 件 / {ByteSizeText.Format(category.TotalSizeBytes)}",
            relPath: null, icon, onCheckedChanged);

    /// <summary>エントリノードを作る。</summary>
    /// <param name="entry">走査結果のエントリ。</param>
    /// <param name="icon">ファイル形式 / フォルダのアイコン。</param>
    /// <param name="onCheckedChanged">チェック変更の通知先。</param>
    /// <returns>作成したノード。</returns>
    public static TemplateTreeNode CreateEntry(
        TemplateEntry entry, ImageSource? icon, Action? onCheckedChanged) =>
        new(entry.DisplayName,
            entry.IsFolder
                ? $"{entry.FileCount} ファイル / {ByteSizeText.Format(entry.SizeBytes)}"
                : ByteSizeText.Format(entry.SizeBytes),
            entry.RelPath, icon, onCheckedChanged);

    /// <summary>子ノードを追加して親子関係を張る。</summary>
    /// <param name="child">追加する子ノード。</param>
    public void AddChild(TemplateTreeNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    // ============================================================
    //  チェック状態の伝播
    // ============================================================

    /// <summary>
    /// チェック状態を設定し、必要な方向へ伝播する。
    /// </summary>
    /// <param name="value">新しい状態（null = 一部選択）。</param>
    /// <param name="updateChildren">子へ伝播するか。</param>
    /// <param name="updateParent">親の 3 状態を再計算するか。</param>
    private void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;
        _isChecked = value;

        // 子へ伝播（親のチェック = 配下すべてのチェック）。
        // null（一部選択）は親の表示用の状態なので子へは配らない。
        if (updateChildren && value.HasValue)
            foreach (var child in Children)
                child.SetIsChecked(value, updateChildren: true, updateParent: false);

        // 親の 3 状態を再計算する
        if (updateParent) Parent?.RecalculateFromChildren();

        Raise(nameof(IsChecked));
        _onCheckedChanged?.Invoke();
    }

    /// <summary>子のチェック状態から自分の 3 状態を求め直す。</summary>
    private void RecalculateFromChildren()
    {
        if (Children.Count == 0) return;

        bool allChecked  = true;
        bool noneChecked = true;
        foreach (var child in Children)
        {
            if (child.IsChecked != true)  allChecked  = false;
            if (child.IsChecked != false) noneChecked = false;
        }

        // 全部 true → true / 全部 false → false / 混在 → null（一部選択）
        var state = allChecked ? true : noneChecked ? (bool?)false : null;
        SetIsChecked(state, updateChildren: false, updateParent: true);
    }

    // ============================================================
    //  走査
    // ============================================================

    /// <summary>
    /// 自分と子孫のうち、チェックされたエントリの相対パスを集める。
    /// 検索で非表示になっているノードも対象にする（絞り込みは表示だけの都合なので、
    /// 隠れた選択が黙って消えると利用者が気付けない）。
    /// </summary>
    /// <param name="destination">追加先。</param>
    public void CollectCheckedEntries(List<string> destination)
    {
        if (RelPath is not null && _isChecked == true) destination.Add(RelPath);
        foreach (var child in Children) child.CollectCheckedEntries(destination);
    }

    /// <summary>自分と子孫のチェックをすべて外す（子への伝播は SetIsChecked が行う）。</summary>
    public void ClearChecks() =>
        SetIsChecked(false, updateChildren: true, updateParent: false);

    // ============================================================
    //  INotifyPropertyChanged
    // ============================================================

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>プロパティ変更を通知する。</summary>
    /// <param name="name">プロパティ名（呼び出し元から自動で入る）。</param>
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
