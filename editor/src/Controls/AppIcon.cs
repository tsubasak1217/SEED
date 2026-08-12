using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SEEDEditor.Controls;

/// <summary>
/// ベクターアイコンを 1 個描画する共通コントロール。
///
/// 新規に足すアイコンは絵文字・記号文字ではなく必ずこのコントロール
/// （または <see cref="IconImages"/>）を通す。ユーザーが用意した既存の PNG
/// アイコン（ギズモ・プレイバー・検索・ファイル形式）はそのまま PNG を使う。
/// 形状データは resources/icons/Icons.xaml の <c>Geometry</c> リソースから
/// <see cref="IconKey"/> で引く（キー一覧と用途は docs/editor_icons.md が正典）。
///
/// 色はハードコードしない。内部の <see cref="Path"/> の Fill は
/// <see cref="TextElement.ForegroundProperty"/> へバインドしてあるため、
/// 親の Button / TextBlock / パネルが持つ Foreground をそのまま継承する
/// （Control.Foreground は TextElement.Foreground の AddOwner なので、
/// ボタンの Foreground を変えるだけでアイコン色も追従する）。
/// 祖先が誰も Foreground を指定していない場合だけ、黒アイコンがダーク背景に
/// 溶けるのを防ぐため <see cref="DefaultBrushKey"/> のブラシで描く。
///
/// XAML 例:
///   &lt;ctrl:AppIcon IconKey="Icon.Play" Width="14" Height="14"/&gt;
/// コード例:
///   var icon = AppIcon.Create("Icon.Play", size: 14);
/// </summary>
public sealed class AppIcon : Viewbox
{
    /// <summary>アイコンの既定の一辺サイズ（px）。ツールバー・行内アイコンの標準寸法。</summary>
    public const double DefaultSize = 16.0;

    /// <summary>Icons.xaml の全 Geometry が前提とする viewBox の一辺（MDI は 24x24 固定）。</summary>
    private const double IconViewBoxSize = 24.0;

    /// <summary>祖先が Foreground を持たないときに使う既定色ブラシのリソースキー。</summary>
    public const string DefaultBrushKey = "Icon.DefaultBrush";

    /// <summary>Foreground が一切解決できなかった場合の最終フォールバック色。</summary>
    private static readonly Brush HardFallbackBrush = Brushes.White;

    /// <summary>実体の描画要素。Viewbox 直下の Canvas に置き、viewBox 座標のまま描く。</summary>
    private readonly Path _path = new() { Stretch = Stretch.None };

    /// <summary>Icons.xaml の Geometry リソースキー（例 "Icon.Play"）。</summary>
    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.Register(
            nameof(IconKey), typeof(string), typeof(AppIcon),
            new PropertyMetadata(null, OnIconKeyChanged));

    /// <inheritdoc cref="IconKeyProperty"/>
    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>
    /// アイコンの塗り色。<see cref="TextElement.ForegroundProperty"/> の AddOwner なので、
    /// 指定しなければ親（Button / TextBlock / パネル）の Foreground をそのまま継承し、
    /// 指定すればここから下だけ色が変わる。Viewbox 自体は Foreground を持たないため、
    /// XAML から Foreground="..." と書けるようにこの別名を用意している。
    /// </summary>
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(AppIcon));

    /// <inheritdoc cref="ForegroundProperty"/>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public AppIcon()
    {
        // 24x24 の viewBox を固定した Canvas を Viewbox で等比縮小する。
        // Path を直接 Stretch=Uniform で描くとアイコンごとに実際の描画範囲
        // （余白の量）が違うぶん見かけの大きさがバラつくため、必ず viewBox を挟む。
        Stretch = Stretch.Uniform;
        Width   = DefaultSize;
        Height  = DefaultSize;
        Child   = new Canvas
        {
            Width    = IconViewBoxSize,
            Height   = IconViewBoxSize,
            Children = { _path },
        };

        // 親から継承した Foreground をそのまま塗り色にする。
        _path.SetBinding(Shape.FillProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
            Path           = new PropertyPath(TextElement.ForegroundProperty),
        });

        Loaded += OnLoadedApplyFallbackBrush;
    }

    /// <summary>
    /// コードビハインドからアイコンを 1 行で生成するためのファクトリ。
    /// </summary>
    /// <param name="iconKey">Icons.xaml のリソースキー。</param>
    /// <param name="size">一辺のサイズ（px）。既定は <see cref="DefaultSize"/>。</param>
    /// <param name="brush">塗り色を明示したい場合に指定する。null なら親の Foreground を継承する。</param>
    public static AppIcon Create(string iconKey, double size = DefaultSize, Brush? brush = null)
    {
        var icon = new AppIcon { IconKey = iconKey, Width = size, Height = size };
        if (brush != null) icon.SetBrush(brush);
        return icon;
    }

    /// <summary>
    /// 塗り色を明示指定する（Foreground の継承より優先される）。
    /// 状態に応じて色を切り替えるボタン等から使う。
    /// </summary>
    public void SetBrush(Brush brush) => _path.Fill = brush;

    /// <summary>
    /// 「アイコン＋ラベル」の横並びを作る共通ヘルパー。
    /// Content="⚙ 設定" のような絵文字混じり文字列を置き換える用途。
    /// </summary>
    /// <param name="iconKey">Icons.xaml のリソースキー。</param>
    /// <param name="text">アイコンの右に並べるラベル文字列。</param>
    /// <param name="size">アイコン一辺のサイズ（px）。</param>
    /// <param name="gap">アイコンとラベルの間隔（px）。</param>
    public static StackPanel WithText(string iconKey, string text,
                                      double size = DefaultSize, double gap = 5.0)
    {
        var panel = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        panel.Children.Add(Create(iconKey, size));
        panel.Children.Add(new TextBlock
        {
            Text              = text,
            Margin            = new Thickness(gap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    /// <summary>IconKey 変更時に Geometry リソースを引き直して Path へ反映する。</summary>
    private static void OnIconKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var icon = (AppIcon)d;
        icon._path.Data = ResolveGeometry(e.NewValue as string);
    }

    /// <summary>
    /// リソースキーから Geometry を引く。未登録キーはここで null になり、
    /// 「何も描かれない」だけで例外にはしない（起動不能を避ける）。
    /// キーの綴り間違いはビルドでは検出できないため docs/editor_icons.md の
    /// 一覧と .claude/rules/editor-icons.md の運用ルールで担保する。
    /// </summary>
    private static Geometry? ResolveGeometry(string? iconKey)
    {
        if (string.IsNullOrEmpty(iconKey)) return null;
        return Application.Current?.TryFindResource(iconKey) as Geometry;
    }

    /// <summary>
    /// ツリーへ載った時点で Foreground の解決元を調べ、誰も指定していない
    /// （＝WPF 既定の黒のまま）ならダーク UI 用の既定ブラシへ差し替える。
    /// </summary>
    private void OnLoadedApplyFallbackBrush(object sender, RoutedEventArgs e)
    {
        // SetBrush で明示指定済みなら Fill のバインディングは外れている。上書きしない。
        if (!BindingOperations.IsDataBound(_path, Shape.FillProperty)) return;

        var source = DependencyPropertyHelper
            .GetValueSource(_path, TextElement.ForegroundProperty);
        if (source.BaseValueSource != BaseValueSource.Default) return;

        _path.Fill = Application.Current?.TryFindResource(DefaultBrushKey) as Brush
                     ?? HardFallbackBrush;
    }
}
