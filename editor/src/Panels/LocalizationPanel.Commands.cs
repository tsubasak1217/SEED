// ============================================================
//  LocalizationPanel.Commands.cs — 文字列表の操作（キーの追加・名前の変更・削除・複数形の形・言語・見本から作る）
//
//  どの操作もモデル（LocaleTableModel）へ渡し、断られたら理由を説明の帯へ出す（例外にしない）。
//  入力・確認はヘッドレスでも止まらない窓口（Headless/EditorDialogs）を通す。
//  言語の操作は、操作の帯の「言語…」と列の見出しの右クリックで同じメニュー（BuildLanguageMenu）を出す。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SEED.Localization;
using SEEDEditor.Headless;
using SEEDEditor.Localization.IO;
using SEEDEditor.Localization.Model;
using SEEDEditor.Panels.Localization;
using SEEDEditor.Templates;

namespace SEEDEditor.Panels;

public partial class LocalizationPanel
{
    // ============================================================
    //  キー
    // ============================================================

    /// <summary>新しいキーの入力欄で Enter: キーを足す。</summary>
    private void OnNewKeyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddKeyFromInput();
        e.Handled = true;
    }

    /// <summary>［キーを追加］。</summary>
    private void OnAddKeyClick(object sender, RoutedEventArgs e) => AddKeyFromInput();

    /// <summary>入力欄のキーを全部の言語に足し、足した行を選ぶ。</summary>
    private void AddKeyFromInput()
    {
        if (_model is null) return;
        CommitPendingEdit();
        LocaleKeyRules.TryValidate(TxtNewKey.Text, out var key, out _);
        var result = _model.AddKey(TxtNewKey.Text);
        if (!Report(result, "キーを追加")) return;

        TxtNewKey.Clear();
        // 足した行が絞り込みで隠れないよう、検索・未訳だけは外さない（足した行は未訳なので「未訳だけ」でも見える）
        RefreshAfterEdit(structureChanged: false);
        SelectKey(key);
    }

    /// <summary>［名前の変更…］: 選んだキー（複数形の形の行はまとまり）の名前を変える。</summary>
    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || SelectedRows().FirstOrDefault() is not { } row) return;
        CommitPendingEdit();
        string target = row.Source.LogicalKey;
        bool isGroup = row.Source.GroupKey is not null;
        string? newKey = EditorDialogs.ShowTextInput(
            LocalizationPanelMessages.RenamePrompt(target, isGroup), LocalizationPanelMessages.RenameCaption, target, Window.GetWindow(this));
        if (newKey is null) return;

        var result = _model.RenameKey(target, newKey);
        if (!Report(result, "名前を変更")) return;
        RefreshAfterEdit(structureChanged: false);
        // 名前を変えた行を選び直す（複数形はまとまりの最初の形）
        LocaleKeyRules.TryValidate(newKey, out var renamed, out _);
        SelectKey(_rows.FirstOrDefault(r => r.Key == renamed || r.Source.GroupKey == renamed)?.Key);
    }

    /// <summary>［削除］。</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelectedKeys();

    /// <summary>選んだキーを全部の言語の表から消す（確認してから）。</summary>
    private void DeleteSelectedKeys()
    {
        if (_model is null) return;
        CommitPendingEdit();
        var keys = SelectedRows().Select(r => r.Key).ToList();
        if (keys.Count == 0) return;
        var answer = EditorDialogs.Show(LocalizationPanelMessages.DeleteConfirm(keys), LocalizationPanelMessages.DeleteCaption,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        if (!Report(_model.DeleteKeys(keys), "削除")) return;
        RefreshAfterEdit(structureChanged: false);
    }

    /// <summary>［複数形の形を追加…］: 形を選ぶメニューを出す。</summary>
    private void OnPluralClick(object sender, RoutedEventArgs e)
    {
        if (_model is null || SelectedRows().FirstOrDefault() is not { } row) return;
        string group = row.Source.LogicalKey;
        var menu = new ContextMenu();
        foreach (var category in Enum.GetValues<PluralCategory>())
        {
            string formKey = LocaleKeyRules.FormKey(group, category);
            var item = new MenuItem
            {
                Header = formKey,
                // 既に行のある形は足せない（どの言語にも足すものが無い）
                IsEnabled = _allRows.All(r => r.Key != formKey),
            };
            item.Click += (_, _) => AddPluralForm(group, category);
            menu.Items.Add(item);
        }
        OpenMenuBelow(menu, (FrameworkElement)sender);
    }

    /// <summary>複数形の形を足す。</summary>
    private void AddPluralForm(string group, PluralCategory category)
    {
        if (_model is null) return;
        CommitPendingEdit();
        if (!Report(_model.AddPluralForm(group, category), "複数形の形を追加")) return;
        RefreshAfterEdit(structureChanged: false);
        SelectKey(LocaleKeyRules.FormKey(group, category));
    }

    // ============================================================
    //  言語
    // ============================================================

    /// <summary>［言語…］: 言語のメニューを出す（言語ごとの小メニューと「言語を追加…」）。</summary>
    private void OnLanguagesClick(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        var menu = new ContextMenu();
        foreach (var language in _model.Languages)
        {
            var sub = new MenuItem { Header = LocalizationPanelMessages.LanguageLabel(language.Name, language.Code) };
            foreach (var item in BuildLanguageMenuItems(language)) sub.Items.Add(item);
            menu.Items.Add(sub);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var add = new MenuItem { Header = LocalizationPanelMessages.MenuAddLanguage };
        add.Click += (_, _) => AddLanguage();
        menu.Items.Add(add);
        OpenMenuBelow(menu, (FrameworkElement)sender);
    }

    /// <summary>列の見出しの右クリック: その言語のメニューを出す。</summary>
    private void OnTablePreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_model is null || e.OriginalSource is not DependencyObject source) return;
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column is null || !_columnCodes.TryGetValue(header.Column, out var code)) return;
        var language = _model.Languages.FirstOrDefault(l => l.Code == code);
        if (language is null) return;

        var menu = new ContextMenu();
        foreach (var item in BuildLanguageMenuItems(language)) menu.Items.Add(item);
        menu.PlacementTarget = header;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>言語 1 つのメニューの項目（既定にする・次に探す言語・名前・一覧から外す）。</summary>
    private IEnumerable<Control> BuildLanguageMenuItems(LocaleLanguageInfo language)
    {
        var setDefault = new MenuItem { Header = LocalizationPanelMessages.MenuSetDefault, IsEnabled = !language.IsDefault };
        setDefault.Click += (_, _) => ApplyLanguageEdit(_model!.SetDefaultLanguage(language.Code), LocalizationPanelMessages.MenuSetDefault);
        yield return setDefault;

        var fallback = new MenuItem { Header = LocalizationPanelMessages.MenuFallback };
        var none = new MenuItem { Header = LocalizationPanelMessages.MenuFallbackNone, IsCheckable = true, IsChecked = language.Fallback is null };
        none.Click += (_, _) => ApplyLanguageEdit(_model!.SetFallback(language.Code, null), LocalizationPanelMessages.MenuFallback);
        fallback.Items.Add(none);
        foreach (var other in _model!.Languages.Where(l => l.Code != language.Code))
        {
            var item = new MenuItem
            {
                Header = LocalizationPanelMessages.LanguageLabel(other.Name, other.Code),
                IsCheckable = true,
                IsChecked = string.Equals(language.Fallback, other.Code, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ApplyLanguageEdit(_model!.SetFallback(language.Code, other.Code), LocalizationPanelMessages.MenuFallback);
            fallback.Items.Add(item);
        }
        yield return fallback;

        var rename = new MenuItem { Header = LocalizationPanelMessages.MenuRenameLanguage };
        rename.Click += (_, _) =>
        {
            string? name = EditorDialogs.ShowTextInput(LocalizationPanelMessages.RenameLanguagePrompt(language.Code),
                LocalizationPanelMessages.RenameLanguageCaption, language.Name, Window.GetWindow(this));
            if (name is not null) ApplyLanguageEdit(_model!.SetLanguageName(language.Code, name), LocalizationPanelMessages.MenuRenameLanguage);
        };
        yield return rename;

        yield return new Separator();

        var remove = new MenuItem { Header = LocalizationPanelMessages.MenuRemoveLanguage };
        remove.Click += (_, _) =>
        {
            var answer = EditorDialogs.Show(LocalizationPanelMessages.RemoveLanguageConfirm(language.Code, language.FileName),
                LocalizationPanelMessages.RemoveLanguageCaption, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) ApplyLanguageEdit(_model!.RemoveLanguage(language.Code), LocalizationPanelMessages.MenuRemoveLanguage);
        };
        yield return remove;
    }

    /// <summary>言語を足す（コード → 名前の順に尋ねる）。</summary>
    private void AddLanguage()
    {
        if (_model is null) return;
        CommitPendingEdit();
        var owner = Window.GetWindow(this);
        string? code = EditorDialogs.ShowTextInput(LocalizationPanelMessages.AddLanguageCodePrompt,
            LocalizationPanelMessages.AddLanguageCaption, null, owner);
        if (code is null) return;
        if (!LocaleCodeRules.TryValidate(code, out var normalized, out var reason))
        {
            ShowStatus(LocalizationPanelMessages.Failed("言語を追加", reason), isError: true);
            return;
        }
        string? name = EditorDialogs.ShowTextInput(LocalizationPanelMessages.AddLanguageNamePrompt(normalized),
            LocalizationPanelMessages.AddLanguageCaption, normalized, owner);
        if (name is null) return;
        ApplyLanguageEdit(_model.AddLanguage(normalized, name), "言語を追加");
    }

    /// <summary>言語の編集の結果を出し、列から組み直す。</summary>
    private void ApplyLanguageEdit(LocaleEditResult result, string what)
    {
        CommitPendingEdit();
        if (!Report(result, what)) return;
        RefreshAfterEdit(structureChanged: true);
    }

    // ============================================================
    //  見本から作る
    // ============================================================

    /// <summary>［見本から作る］: templates/locale を assets/locale へ取り込んで開き直す。</summary>
    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        if (_assetsRoot is null) return;
        string? library = TemplateLibraryLocator.Resolve();
        if (library is null)
        {
            ShowStatus(LocalizationPanelMessages.TemplateLibraryMissing, isError: true);
            return;
        }

        var result = LocaleTemplateInstaller.Install(library, _assetsRoot);
        EditorLog.Write($"{LogPrefix} {result.Message}");
        if (!result.Succeeded)
        {
            ShowStatus(result.Message, isError: true);
            return;
        }
        if (result.CopiedCount > 0) FilesCreated?.Invoke();
        if (LocaleFolderLocator.DefaultFolder(_assetsRoot) is { } folder) LoadFolder(folder);
        ShowStatus(result.Message, isError: false);
    }

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>結果を説明の帯へ出す（失敗なら理由）。</summary>
    /// <returns>変わったなら true（失敗・変わらなかったなら false）。</returns>
    private bool Report(LocaleEditResult result, string what)
    {
        if (!result.Succeeded)
        {
            ShowStatus(LocalizationPanelMessages.Failed(what, result.Message), isError: true);
            return false;
        }
        TxtStatus.Visibility = Visibility.Collapsed;
        return result.Changed;
    }

    /// <summary>メニューをボタンの下に開く。</summary>
    private static void OpenMenuBelow(ContextMenu menu, FrameworkElement anchor)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 木を上へたどって型の合う祖先を探す（見た目の要素は見た目の木、文字の断片〈Run など〉は論理の木の親へ。
    /// VisualTreeHelper.GetParent は見た目の要素でないものを渡すと例外を投げるため）。
    /// </summary>
    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = ParentOf(current))
            if (current is T found) return found;
        return null;
    }

    /// <summary>親の要素（見た目の要素は見た目の木、ほかは論理の木）。</summary>
    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
