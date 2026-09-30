// ============================================================
//  TemplateActorCategoryItem.cs — テンプレートアクタの窓の左の木の 1 行（表示モデル）
//
//  【役割】
//  カテゴリ（2 段まで）と「すべて」の行を表す。件数は検索のたびに数え直して表示する
//  （いまの検索語に当たるテンプレートが、そのカテゴリに何件あるか）。
// ============================================================

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// 左の木の 1 行の表示モデル。
/// </summary>
public sealed class TemplateActorCategoryItem : INotifyPropertyChanged
{
    /// <summary>カテゴリのキー（「すべて」の行は null）。</summary>
    public string? Key { get; }

    /// <summary>この段の名前。</summary>
    public string Name { get; }

    /// <summary>子のカテゴリ（2 段目）。</summary>
    public ObservableCollection<TemplateActorCategoryItem> Children { get; } = [];

    private int  _count;
    private bool _isExpanded = true;
    private bool _isSelected;

    /// <summary>いまの検索語に当たるテンプレートの件数。</summary>
    public int Count
    {
        get => _count;
        set { if (_count == value) return; _count = value; OnChanged(); OnChanged(nameof(CountText)); }
    }

    /// <summary>件数の表示。</summary>
    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>開いているか（既定で開く。2 段しか無いので最初から全部見せる）。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnChanged(); }
    }

    /// <summary>選ばれているか（コードから「すべて」を選ぶために使う）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); }
    }

    /// <summary>行を作る。</summary>
    /// <param name="key">カテゴリのキー（「すべて」は null）。</param>
    /// <param name="name">表示する名前。</param>
    public TemplateActorCategoryItem(string? key, string name)
    {
        Key  = key;
        Name = name;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>値の変化を知らせる。</summary>
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
