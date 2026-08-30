using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Panels.SpriteRig.Mesh;
using SEEDEditor.Panels.SpriteRig.Model;

namespace SEEDEditor.Panels;

/// <summary>
/// <see cref="SpriteRigPanel"/> のうち Phase B1b（ボーン一覧・ウェイト設定 UI）を担う部分。
///
/// <para>
/// ここも本体と同じ方針で、<b>編集ロジックは一切持たない</b>。
/// 役割は「<see cref="SpriteRigDocument"/> の状態を WPF のコントロールへ写す」ことと、
/// 「コントロールの操作をドキュメントの操作メソッドへ橋渡しする」ことだけである。
/// </para>
/// </summary>
public partial class SpriteRigPanel
{
    /// <summary>ボーン一覧のインデント 1 段ぶんの幅（文字数）。</summary>
    private const int BoneTreeIndentSpaces = 2;

    /// <summary>影響詳細行のボーン名欄の幅（px）。</summary>
    private const double InfluenceNameWidth = 96.0;

    /// <summary>影響詳細行の数値欄の幅（px）。</summary>
    private const double InfluenceValueWidth = 56.0;

    /// <summary>影響詳細行の色見本の一辺（px）。</summary>
    private const double InfluenceSwatchSize = 9.0;

    /// <summary>ボーン一覧に付ける色見本の一辺（px）。</summary>
    private const double BoneSwatchSize = 9.0;

    /// <summary>ウェイト数値の表示書式。</summary>
    private const string WeightFormat = "0.###";

    // ============================================================
    //  UI 同期
    // ============================================================

    /// <summary>
    /// 編集モードに応じて左パネルの道具立てを出し分け、ボーン／ウェイトの UI を同期する。
    /// </summary>
    /// <param name="document">アクティブなドキュメント。</param>
    private void UpdateRiggingUi(SpriteRigDocument document)
    {
        PanelMeshTools.Visibility = Visible(document.EditMode == SpriteRigEditMode.Mesh);
        PanelBoneTools.Visibility = Visible(document.EditMode == SpriteRigEditMode.Bone);
        PanelWeightTools.Visibility = Visible(document.EditMode == SpriteRigEditMode.Weight);

        _suppressUiEvents = true;
        try
        {
            BoneToolCreate.IsChecked = document.BoneTool == SpriteRigBoneTool.Create;
            BoneToolSelect.IsChecked = document.BoneTool == SpriteRigBoneTool.Select;

            RebuildBoneList(document, ListBones);
            RebuildBoneList(document, ListWeightBones);
            RebuildParentCombo(document);

            TxtBoneName.Text = document.SelectedBoneIndex >= 0
                ? document.Mesh.Bones[document.SelectedBoneIndex].Name
                : string.Empty;
            TxtBoneName.IsEnabled = document.SelectedBoneIndex >= 0;
            CmbBoneParent.IsEnabled = document.SelectedBoneIndex >= 0;
            BtnDeleteBone.IsEnabled = document.SelectedBoneIndex >= 0 && document.Mesh.Bones.Count > 1;

            SldWeightFalloff.Value = document.AutoWeightOptions.Falloff;
            ChkSuppressAcrossContour.IsChecked = document.AutoWeightOptions.SuppressAcrossContour;
            ChkAutoWeightSelectedOnly.IsChecked = document.AutoWeightSelectedOnly;
            SldBrushRadius.Value = document.Brush.Radius;
            SldBrushStrength.Value = document.Brush.Strength;
            CmbBrushMode.SelectedIndex = (int)document.Brush.Mode;
            ChkAllBoneColors.IsChecked = document.ShowAllBoneColors;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        TbWeightFalloff.Text = document.AutoWeightOptions.Falloff.ToString("0.0", CultureInfo.InvariantCulture);
        TbBrushRadius.Text = document.Brush.Radius.ToString("0", CultureInfo.InvariantCulture);
        TbBrushStrength.Text = document.Brush.Strength.ToString("0.00", CultureInfo.InvariantCulture);

        RebuildInfluenceRows(document);
    }

    /// <summary>真偽値を <see cref="Visibility"/> へ変換する。</summary>
    /// <param name="visible">表示するか。</param>
    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// ボーン一覧を階層順（深さぶんインデント）で組み直し、選択を同期する。
    /// </summary>
    /// <param name="document">アクティブなドキュメント。</param>
    /// <param name="list">組み直す対象のリスト。</param>
    private static void RebuildBoneList(SpriteRigDocument document, ListBox list)
    {
        list.Items.Clear();
        var palette = SpriteRig.SpriteRigCanvas.BuildBonePalette(document.Mesh.Bones.Count);

        foreach (var (index, depth) in SpriteRigSkeleton.BuildDisplayOrder(document.Mesh.Bones))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = BoneSwatchSize,
                Height = BoneSwatchSize,
                Fill = new SolidColorBrush(palette[index]),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(depth * BoneTreeIndentSpaces * 4.0, 0.0, 5.0, 0.0),
            });
            row.Children.Add(new TextBlock
            {
                Text = document.Mesh.Bones[index].Name,
                VerticalAlignment = VerticalAlignment.Center,
            });

            list.Items.Add(new ListBoxItem { Content = row, Tag = index });
        }

        // 選択の同期（Tag に入れた添字で引き当てる）
        list.SelectedItem = null;
        foreach (ListBoxItem item in list.Items)
        {
            if (item.Tag is int index && index == document.SelectedBoneIndex)
            {
                list.SelectedItem = item;
                break;
            }
        }
    }

    /// <summary>親ボーン選択コンボを組み直す（自分と自分の子孫は候補から外す）。</summary>
    /// <param name="document">アクティブなドキュメント。</param>
    private void RebuildParentCombo(SpriteRigDocument document)
    {
        CmbBoneParent.Items.Clear();
        CmbBoneParent.Items.Add(new ComboBoxItem { Content = "（ルート）", Tag = -1 });

        int selected = document.SelectedBoneIndex;
        if (selected < 0)
        {
            CmbBoneParent.SelectedIndex = 0;
            return;
        }

        var parents = SpriteRigSkeleton.BuildParentIndices(document.Mesh.Bones);
        for (int i = 0; i < document.Mesh.Bones.Count; i++)
        {
            // 循環になる候補（自分自身と自分の子孫）は最初から見せない
            if (SpriteRigSkeleton.IsDescendantOf(document.Mesh.Bones, i, selected)) continue;
            CmbBoneParent.Items.Add(new ComboBoxItem { Content = document.Mesh.Bones[i].Name, Tag = i });
        }

        int currentParent = parents[selected];
        CmbBoneParent.SelectedIndex = 0;
        for (int i = 0; i < CmbBoneParent.Items.Count; i++)
        {
            if (CmbBoneParent.Items[i] is ComboBoxItem { Tag: int tag } && tag == currentParent)
            {
                CmbBoneParent.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// 選択頂点の影響一覧（ボーン名 + 数値編集欄）を組み直す。
    ///
    /// 複数選択されている場合は<b>添字が最小の 1 頂点</b>を代表として出す
    /// （数値編集は 1 頂点に対する操作なので、複数を同時に見せても編集できないため）。
    /// </summary>
    /// <param name="document">アクティブなドキュメント。</param>
    private void RebuildInfluenceRows(SpriteRigDocument document)
    {
        ListInfluences.Items.Clear();

        int vertex = -1;
        foreach (int index in document.SelectedVertices)
        {
            if (vertex < 0 || index < vertex) vertex = index;
        }

        if (vertex < 0 || vertex >= document.Mesh.Weights.Count)
        {
            ListInfluences.Items.Add(new TextBlock
            {
                Text = "Shift + 左クリックで頂点を選ぶと、影響が数値で編集できます。",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                FontSize = 10.0,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        ListInfluences.Items.Add(new TextBlock
        {
            Text = document.SelectedVertices.Count > 1
                ? $"頂点 #{vertex}（{document.SelectedVertices.Count} 個選択中の先頭）"
                : $"頂点 #{vertex}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 10.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 3.0),
        });

        var palette = SpriteRig.SpriteRigCanvas.BuildBonePalette(document.Mesh.Bones.Count);
        foreach (var influence in document.Mesh.Weights[vertex])
        {
            if (influence.BoneIndex < 0 || influence.BoneIndex >= document.Mesh.Bones.Count) continue;
            ListInfluences.Items.Add(BuildInfluenceRow(document, vertex, influence, palette));
        }
    }

    /// <summary>影響 1 本ぶんの編集行（色見本 + ボーン名 + 数値欄）を作る。</summary>
    /// <param name="document">アクティブなドキュメント。</param>
    /// <param name="vertexIndex">対象頂点の添字。</param>
    /// <param name="influence">表示する影響。</param>
    /// <param name="palette">ボーン色分けのパレット。</param>
    private FrameworkElement BuildInfluenceRow(
        SpriteRigDocument document, int vertexIndex, SpriteRigInfluence influence, Color[] palette)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0.0, 1.0, 0.0, 1.0) };

        row.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = InfluenceSwatchSize,
            Height = InfluenceSwatchSize,
            Fill = new SolidColorBrush(palette[influence.BoneIndex]),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 4.0, 0.0),
        });
        row.Children.Add(new TextBlock
        {
            Text = document.Mesh.Bones[influence.BoneIndex].Name,
            Width = InfluenceNameWidth,
            FontSize = 10.0,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var editor = new TextBox
        {
            Text = influence.Weight.ToString(WeightFormat, CultureInfo.InvariantCulture),
            Width = InfluenceValueWidth,
            FontSize = 10.0,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            // どの頂点のどのボーンを編集しているかを行そのものに持たせる
            Tag = (vertexIndex, influence.BoneIndex),
        };
        editor.KeyDown += OnInfluenceValueKeyDown;
        editor.LostFocus += OnInfluenceValueCommitted;
        row.Children.Add(editor);

        return row;
    }

    // ============================================================
    //  ボーン編集のイベント
    // ============================================================

    /// <summary>ボーンツール（作成 / 選択・移動）の切り替え。</summary>
    private void OnBoneToolSelected(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document)
        {
            BoneToolCreate.IsChecked = true;
            BoneToolSelect.IsChecked = false;
            return;
        }

        document.BoneTool = ReferenceEquals(sender, BoneToolSelect)
            ? SpriteRigBoneTool.Select
            : SpriteRigBoneTool.Create;
        // ツールを変えたら作りかけの骨と連鎖は捨てる
        document.CancelBoneCreate();

        BoneToolCreate.IsChecked = document.BoneTool == SpriteRigBoneTool.Create;
        BoneToolSelect.IsChecked = document.BoneTool == SpriteRigBoneTool.Select;
        ActiveCanvas?.Refresh();
    }

    /// <summary>ボーン一覧での選択変更（ボーンモード側）。</summary>
    private void OnBoneListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyBoneListSelection(ListBones);

    /// <summary>ボーン一覧での選択変更（ウェイトモード側の対象ボーン）。</summary>
    private void OnWeightBoneSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyBoneListSelection(ListWeightBones);

    /// <summary>一覧の選択をドキュメントの選択ボーンへ反映する。</summary>
    /// <param name="list">操作された一覧。</param>
    private void ApplyBoneListSelection(ListBox list)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;
        if (list.SelectedItem is not ListBoxItem { Tag: int index }) return;
        if (document.SelectedBoneIndex == index) return;

        document.SelectedBoneIndex = index;
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>ボーン一覧のダブルクリックで名前欄へフォーカスを移す。</summary>
    private void OnBoneListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_documents.Active?.SelectedBoneIndex is not >= 0) return;
        BeginBoneRename();
        e.Handled = true;
    }

    /// <summary>名前欄へフォーカスを移し、全選択して打ち替えられる状態にする。</summary>
    private void BeginBoneRename()
    {
        TxtBoneName.Focus();
        TxtBoneName.SelectAll();
    }

    /// <summary>キャンバスでボーンがダブルクリックされたときに名前欄へ誘導する。</summary>
    /// <param name="boneIndex">対象ボーンの添字（UI 同期のあと選択済みになっている）。</param>
    private void OnCanvasBoneRenameRequested(int boneIndex)
    {
        UpdateUiForActiveDocument();
        BeginBoneRename();
    }

    /// <summary>キャンバス側でボーン／頂点の選択が変わったときに一覧を追随させる。</summary>
    private void OnCanvasRigSelectionChanged() => UpdateUiForActiveDocument();

    /// <summary>名前欄で Enter を押したら確定する。</summary>
    private void OnBoneNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitBoneName();
        e.Handled = true;
    }

    /// <summary>名前欄からフォーカスが外れたら確定する。</summary>
    private void OnBoneNameLostFocus(object sender, RoutedEventArgs e) => CommitBoneName();

    /// <summary>名前欄の内容をボーン名へ反映する。</summary>
    private void CommitBoneName()
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;
        int index = document.SelectedBoneIndex;
        if (index < 0) return;

        string newName = TxtBoneName.Text;
        if (string.Equals(document.Mesh.Bones[index].Name, newName, StringComparison.Ordinal)) return;

        if (!document.RenameBone(index, newName))
        {
            // 空・重複は受け付けない。元の名前へ戻して知らせる
            ShowError("ボーン名が空か、他のボーンと重複しています。");
        }
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>親ボーンコンボの変更。</summary>
    private void OnBoneParentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;
        int index = document.SelectedBoneIndex;
        if (index < 0) return;
        if (CmbBoneParent.SelectedItem is not ComboBoxItem { Tag: int newParent }) return;

        var parents = SpriteRigSkeleton.BuildParentIndices(document.Mesh.Bones);
        if (parents[index] == newParent) return;

        if (!document.ReparentBone(index, newParent))
            ShowError("その親には付け替えられません（親子関係が循環します）。");

        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>「ボーンを削除」ボタン。</summary>
    private void OnDeleteBone(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;
        if (!document.DeleteBone(document.SelectedBoneIndex)) return;

        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    // ============================================================
    //  ウェイト編集のイベント
    // ============================================================

    /// <summary>自動ウェイト / ブラシのスライダー変更。</summary>
    private void OnWeightParameterChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;

        document.AutoWeightOptions.Falloff = SldWeightFalloff.Value;
        document.Brush.Radius = SldBrushRadius.Value;
        document.Brush.Strength = SldBrushStrength.Value;

        TbWeightFalloff.Text = document.AutoWeightOptions.Falloff.ToString("0.0", CultureInfo.InvariantCulture);
        TbBrushRadius.Text = document.Brush.Radius.ToString("0", CultureInfo.InvariantCulture);
        TbBrushStrength.Text = document.Brush.Strength.ToString("0.00", CultureInfo.InvariantCulture);
        ActiveCanvas?.Refresh();
    }

    /// <summary>ウェイト系チェックボックスの変更。</summary>
    private void OnWeightToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;

        document.AutoWeightOptions.SuppressAcrossContour = ChkSuppressAcrossContour.IsChecked == true;
        document.AutoWeightSelectedOnly = ChkAutoWeightSelectedOnly.IsChecked == true;
        document.ShowAllBoneColors = ChkAllBoneColors.IsChecked == true;
        ActiveCanvas?.Refresh();
    }

    /// <summary>ブラシモードの変更。</summary>
    private void OnBrushModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;
        document.Brush.Mode = (WeightBrushMode)Math.Max(0, CmbBrushMode.SelectedIndex);
    }

    /// <summary>「自動ウェイト」ボタン。</summary>
    private void OnAutoWeight(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            int applied = document.ApplyAutoWeights();
            if (applied == 0)
                ShowError("ウェイトを割り当てる頂点がありません。先にメッシュとボーンを作ってください。");
        }
        catch (Exception ex)
        {
            ShowError($"自動ウェイトに失敗しました:\n{ex.Message}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>「ウェイトを初期化」ボタン。</summary>
    private void OnResetWeights(object sender, RoutedEventArgs e)
    {
        if (_documents.Active is not { } document) return;
        document.ResetWeights();
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }

    /// <summary>影響の数値欄で Enter を押したら確定する。</summary>
    private void OnInfluenceValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitInfluenceValue(sender);
        e.Handled = true;
    }

    /// <summary>影響の数値欄からフォーカスが外れたら確定する。</summary>
    private void OnInfluenceValueCommitted(object sender, RoutedEventArgs e) => CommitInfluenceValue(sender);

    /// <summary>影響の数値欄の内容をウェイトへ反映する。</summary>
    /// <param name="sender">編集された数値欄。</param>
    private void CommitInfluenceValue(object sender)
    {
        if (_suppressUiEvents || _documents.Active is not { } document) return;
        if (sender is not TextBox { Tag: ValueTuple<int, int> target } box) return;

        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double weight))
        {
            UpdateUiForActiveDocument();   // 数値として読めない入力は表示を元へ戻すだけ
            return;
        }

        double current = document.GetInfluenceWeight(target.Item1, target.Item2);
        if (Math.Abs(current - weight) < double.Epsilon) return;

        document.SetInfluenceWeight(target.Item1, target.Item2, weight);
        ActiveCanvas?.Refresh();
        UpdateUiForActiveDocument();
    }
}
