// ============================================================
//  ExpandStateStore.cs — 折りたたみ UI の展開状態を再構築ごしに保持する汎用ストア
//
//  【なぜ必要か】
//    エディタのインスペクタ系 UI は「値を 1 つ変更 → SET_* IPC → ランタイムが
//    ACTOR_COMPONENTS を再送 → パネル全体を作り直す」という作りになっている。
//    このとき Expander / 自前アコーディオンは新しいインスタンスに置き換わるため、
//    素の IsExpanded 初期値だけでは「編集した瞬間に開いていたヘッダーが閉じる」
//    という症状になる（イベントバブリングではなく UI 再構築による状態消失）。
//
//  【方式】
//    ControlPoint 行のハイライト復元（InspectorPanel._controlPointRows）と同じ
//    「状態はパネル側フィールドに退避し、再構築後に復元する」パターンを汎用化したもの。
//    キー文字列 → 開閉フラグの辞書を持ち、UI 生成時に Track() で結び付ける。
//
//  【キーの決め方】
//    「同じ論理セクションを指す限り再構築をまたいで一致する」文字列であること。
//    表示名のように編集で変わり得る値は避け、スロット添字・マテリアルスロット番号・
//    フィールドのドットパスなど構造上の識別子を使う（詳細は各呼び出し側のコメント）。
//
//  【スコープ】
//    アクタを切り替えたら状態は破棄する（別アクタのスロット添字に前アクタの
//    開閉状態が漏れると、無関係なセクションが勝手に開く）。BeginScope() に
//    「アクタ ID などの現在の対象」を渡すと、変化したタイミングで自動的に捨てる。
// ============================================================

using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace SEEDEditor.Controls;

/// <summary>
/// 折りたたみセクション（WPF <see cref="Expander"/> や自前アコーディオン）の展開状態を
/// キー文字列で保持し、UI 再構築後に復元するためのストア。
/// </summary>
public sealed class ExpandStateStore
{
    /// <summary>キー → 展開中かどうか。未登録キーは「既定値のまま」を意味する。</summary>
    private readonly Dictionary<string, bool> _states = new(StringComparer.Ordinal);

    /// <summary>現在のスコープ識別子（アクタ ID 等）。<see cref="BeginScope"/> で切り替える。</summary>
    private string _scope = "";

    /// <summary>
    /// 対象スコープ（アクタ ID など）を宣言する。前回と異なる場合のみ保持状態を破棄する。
    /// UI 再構築の入口で毎回呼んでよい（同一スコープなら何もしない）。
    /// </summary>
    /// <param name="scope">現在編集対象を一意に表す文字列。</param>
    public void BeginScope(string scope)
    {
        if (string.Equals(_scope, scope, StringComparison.Ordinal)) return;
        _scope = scope;
        _states.Clear();
    }

    /// <summary>保持している展開状態を返す（未登録なら <paramref name="defaultExpanded"/>）。</summary>
    public bool IsExpanded(string key, bool defaultExpanded)
        => _states.TryGetValue(key, out var v) ? v : defaultExpanded;

    /// <summary>展開状態を記録する。</summary>
    public void Set(string key, bool expanded) => _states[key] = expanded;

    /// <summary>
    /// キーの記録を削除する（そのセクション自体が消えたとき用。
    /// 例: 配列要素の削除。残すと添字が繰り上がった別要素へ状態が漏れる）。
    /// </summary>
    public void Remove(string key) => _states.Remove(key);

    /// <summary>
    /// 標準 <see cref="Expander"/> に展開状態の復元・記録を結び付ける。
    /// 生成直後（ビジュアルツリーへ追加する前）に一度呼ぶだけでよい。
    /// </summary>
    /// <param name="expander">対象 Expander。</param>
    /// <param name="key">セクションの安定キー。</param>
    /// <param name="defaultExpanded">初回表示時（未記録時）の既定開閉。</param>
    public void Track(Expander expander, string key, bool defaultExpanded = false)
    {
        expander.IsExpanded = IsExpanded(key, defaultExpanded);
        expander.Expanded  += (_, _) => Set(key, true);
        expander.Collapsed += (_, _) => Set(key, false);
    }

    /// <summary>
    /// 自前ヘッダー（Border ＋ ▼/▶ の矢印など）で開閉を表現するセクションを管理する。
    /// 生成時点で保持状態（または既定値）を <paramref name="apply"/> へ流し込み、
    /// 以後は返り値の <see cref="SectionToggle.Toggle"/> / <see cref="SectionToggle.Set"/>
    /// 経由で開閉することで記録が自動的に付いてくる。
    /// </summary>
    /// <param name="key">セクションの安定キー。</param>
    /// <param name="defaultExpanded">初回表示時（未記録時）の既定開閉。</param>
    /// <param name="apply">開閉を UI へ反映する処理（可視性の切り替え・矢印の差し替えなど）。</param>
    public SectionToggle TrackCustom(string key, bool defaultExpanded, Action<bool> apply)
        => new(this, key, IsExpanded(key, defaultExpanded), apply);

    /// <summary>
    /// 自前ヘッダー式セクションの開閉ハンドル。
    /// 生成時に現在状態を UI へ適用し、以降の開閉でストアへ記録する。
    /// </summary>
    public sealed class SectionToggle
    {
        private readonly ExpandStateStore _store;
        private readonly string           _key;
        private readonly Action<bool>     _apply;

        /// <summary>現在展開中かどうか。</summary>
        public bool IsExpanded { get; private set; }

        internal SectionToggle(ExpandStateStore store, string key, bool initialExpanded, Action<bool> apply)
        {
            _store     = store;
            _key       = key;
            _apply     = apply;
            IsExpanded = initialExpanded;
            _apply(IsExpanded);
        }

        /// <summary>開閉を設定し、ストアへ記録して UI へ反映する。</summary>
        public void Set(bool expanded)
        {
            IsExpanded = expanded;
            _store.Set(_key, expanded);
            _apply(expanded);
        }

        /// <summary>開閉を反転する（ヘッダークリック用）。</summary>
        public void Toggle() => Set(!IsExpanded);
    }
}
