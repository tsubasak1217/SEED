using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SEED.UI;

// ============================================================
//  NavAdapters.cs — 部品（UiWidget）→ 方向キーのアダプタ（IUiNavigable）の表とキャッシュ（2026-10-03。L3-6。docs/ui_navigation.md §7.2）
//
//  部品の型ごとにアダプタの作り方を持つ（データの表。型の継承をたどって最初に見つかったもの）。部品 1 つにつきアダプタは 1 つ
//  （ConditionalWeakTable で部品に弱く結ぶ。部品が消えれば一緒に外れる）。作り方が null を返す型（ChipGroup = 札ごとに移る）は
//  移り先にしない（キャッシュしない）。
//  スクリプト独自の部品（UiWidget の派生）は UiNavigation.RegisterAdapter で作り方を足せる（組み込みより先に見る）。
//  スクリプトの読み直しでは足した作り方とキャッシュを捨てる（旧アセンブリの型・デリゲートを握らない）。
// ============================================================

/// <summary>部品 → 方向キーのアダプタの表。</summary>
internal static class NavAdapters
{
    /// <summary>組み込みの作り方（SEED.UI の部品）。</summary>
    private static readonly Dictionary<Type, Func<UiWidget, IUiNavigable?>> BuiltIns = new()
    {
        [typeof(Button)] = w => new ButtonNav((Button)w),
        [typeof(Toggle)] = w => new ToggleNav((Toggle)w),
        [typeof(Checkbox)] = w => new CheckboxNav((Checkbox)w),
        [typeof(Slider)] = w => new SliderNav((Slider)w),
        [typeof(NumberField)] = w => new NumberFieldNav((NumberField)w),
        [typeof(SegmentedControl)] = w => new SelectionGroupNav((SelectionGroup)w),
        [typeof(RadioGroup)] = w => new SelectionGroupNav((SelectionGroup)w),
        // チップはグループではなく札ごとに移る（SelectItemNav）
        [typeof(ChipGroup)] = _ => null,
        [typeof(SelectItem)] = w => new SelectItemNav((SelectItem)w),
        [typeof(TextField)] = w => new TextFieldNav((TextField)w),
        [typeof(WheelPicker)] = w => new WheelPickerNav((WheelPicker)w),
        [typeof(TabItem)] = w => new TabItemNav((TabItem)w),
        [typeof(DialogItem)] = w => new DialogItemNav((DialogItem)w),
    };

    /// <summary>スクリプトが足した作り方（組み込みより先に見る）。</summary>
    private static readonly Dictionary<Type, Func<UiWidget, IUiNavigable?>> UserFactories = new();

    /// <summary>部品 → アダプタ（部品に弱く結ぶ）。</summary>
    private static ConditionalWeakTable<UiWidget, IUiNavigable> _cache = new();

    /// <summary>部品のアダプタ（移り先にしない型なら null）。</summary>
    /// <param name="widget">部品。</param>
    public static IUiNavigable? For(UiWidget widget)
    {
        if (_cache.TryGetValue(widget, out var cached)) return cached;
        var factory = FactoryOf(widget.GetType());
        if (factory is null) return null;
        var nav = factory(widget);
        if (nav is not null) _cache.AddOrUpdate(widget, nav);
        return nav;
    }

    /// <summary>スクリプト独自の部品の型の作り方を足す（同じ型は置き換える。キャッシュは捨てる）。</summary>
    /// <typeparam name="T">部品の型。</typeparam>
    /// <param name="factory">部品 → アダプタ（null = 移り先にしない）。</param>
    public static void Register<T>(Func<T, IUiNavigable?> factory) where T : UiWidget
    {
        UserFactories[typeof(T)] = w => factory((T)w);
        _cache = new ConditionalWeakTable<UiWidget, IUiNavigable>();
    }

    /// <summary>足した作り方とキャッシュを捨てる（スクリプトの読み直し）。</summary>
    public static void Reset()
    {
        UserFactories.Clear();
        _cache = new ConditionalWeakTable<UiWidget, IUiNavigable>();
    }

    /// <summary>型の作り方（継承をたどる。足した作り方を先に見る。UiWidget まで来たら無し）。</summary>
    private static Func<UiWidget, IUiNavigable?>? FactoryOf(Type type)
    {
        for (var t = type; t is not null && t != typeof(UiWidget); t = t.BaseType)
        {
            if (UserFactories.TryGetValue(t, out var user)) return user;
            if (BuiltIns.TryGetValue(t, out var builtIn)) return builtIn;
        }
        return null;
    }
}
