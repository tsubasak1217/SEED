using System;

namespace SEED;

/// <summary>
/// 名前付きイベント（<see cref="Events"/>）の購読 1 件を表すハンドル。
///
/// <c>Events.Subscribe(...)</c> が返す。解除するには <see cref="Dispose"/> を呼ぶか、
/// <see cref="Events.Unsubscribe"/> へ渡す。二重解除は無害（2 回目以降は何もしない）。
///
/// 【ALC 安全性】このハンドルはイベント名（string）・種別（enum）・ハンドラ
/// （デリゲート）だけを持ち、ユーザースクリプトの <c>Type</c> は保持しない。
/// ホットリロード時は <see cref="Events.ClearAll"/> でテーブルごと捨てるため、
/// アンロード可能な AssemblyLoadContext を掴み続けない。
///
/// 【推奨】スクリプトからは <c>this.On("name", handler)</c>
/// （SEEDScript.On）を使うこと。そのスクリプトの破棄時に自動で解除されるため解除漏れが起きない。
/// </summary>
public sealed class EventSubscription : IDisposable
{
    /// <summary>購読の一意 ID（テーブル内での同一性判定・診断用）。</summary>
    internal long Id { get; }

    /// <summary>購読しているイベント名（大文字小文字を区別する）。</summary>
    public string Name { get; }

    /// <summary>この購読が受け取る引数の種別。</summary>
    internal EventArgKind Kind { get; }

    /// <summary>
    /// 呼び出すハンドラ本体（<c>Action</c> / <c>Action&lt;string&gt;</c> /
    /// <c>Action&lt;float&gt;</c> / <c>Action&lt;GameObject&gt;</c> のいずれか）。
    /// 解除時に null にして、解除済みハンドルがユーザーオブジェクトを掴み続けないようにする。
    /// </summary>
    internal Delegate? Handler { get; private set; }

    /// <summary>まだ購読中か（解除済みなら false）。</summary>
    public bool IsActive => Handler is not null;

    /// <summary>エンジン内部用: 購読テーブルへの登録時にのみ生成する。</summary>
    internal EventSubscription(long id, string name, EventArgKind kind, Delegate handler)
    {
        Id      = id;
        Name    = name;
        Kind    = kind;
        Handler = handler;
    }

    /// <summary>
    /// エンジン内部用: 解除済みとしてマークし、ハンドラ参照を手放す。
    /// 購読テーブルからの除去は <see cref="EventBus"/> 側で行う。
    /// </summary>
    internal void MarkReleased() => Handler = null;

    /// <summary>
    /// 購読を解除する（<see cref="Events.Unsubscribe"/> と等価）。二重解除は無害。
    /// イベント発火中に呼んだ場合、その発火中のハンドラ列には反映されず、
    /// 次回の発火から反映される（発火中のコレクション変更を避けるための仕様）。
    /// </summary>
    public void Dispose() => Events.Unsubscribe(this);
}
