using System;
using SEED.UI;

namespace SEED.Binding;

// ============================================================
//  WidgetTarget.cs — 部品（UiWidget）を双方向の当てる先にする共通の土台
//
//  生存・用意は WidgetRef（部品が始まるのを待つ）に任せ、派生は「部品へ書く」「部品の知らせを受ける」だけを書く。
//  部品へは知らせずに書く（SetOn(…, notify: false) など）ので、スクリプトから当てた値で部品の Changed は鳴らない
//  （鳴る部品〈SelectionGroup.Select〉も TwoWayBinding の留め金で止まる）。
// ============================================================

/// <summary>部品を双方向の当てる先にする共通の土台。</summary>
/// <typeparam name="TWidget">部品の型。</typeparam>
/// <typeparam name="T">値の型。</typeparam>
internal abstract class WidgetTarget<TWidget, T> : ITwoWayBindTarget<T> where TWidget : UiWidget
{
    /// <summary>部品の引き当て。</summary>
    private readonly WidgetRef<TWidget> _ref;

    /// <summary>当てる先を作る。</summary>
    /// <param name="widgetRef">部品の引き当て。</param>
    protected WidgetTarget(WidgetRef<TWidget> widgetRef)
    {
        _ref = widgetRef;
    }

    /// <inheritdoc />
    public bool IsAlive => _ref.IsAlive;

    /// <inheritdoc />
    public bool IsReady => _ref.Current is { } widget && IsWidgetReady(widget);

    /// <inheritdoc />
    public void Write(T value)
    {
        if (_ref.Current is { } widget) WriteTo(widget, value);
    }

    /// <inheritdoc />
    public IDisposable Listen(Action<T> handler)
        => _ref.Current is { } widget ? ListenTo(widget, handler) : DisposableAction.Empty;

    /// <summary>部品が値を受けられるか（既定は始まっていれば受けられる。選択のグループは項目が集まってから）。</summary>
    /// <param name="widget">部品。</param>
    /// <returns>受けられれば true。</returns>
    protected virtual bool IsWidgetReady(TWidget widget) => true;

    /// <summary>部品へ値を書く（知らせずに）。</summary>
    /// <param name="widget">部品。</param>
    /// <param name="value">値。</param>
    protected abstract void WriteTo(TWidget widget, T value);

    /// <summary>部品の知らせ（利用者の操作）を受ける口を付ける。</summary>
    /// <param name="widget">部品。</param>
    /// <param name="handler">部品の新しい値を受け取る処理。</param>
    /// <returns>外す口。</returns>
    protected abstract IDisposable ListenTo(TWidget widget, Action<T> handler);
}
