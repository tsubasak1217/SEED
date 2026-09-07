// ============================================================
//  AnimKeySelection.cs — ドープシートのキー複数選択モデル（純ロジック）
//
//  「どのトラックの何番目のキーが選ばれているか」を (trackIndex, keyIndex) の
//  集合として保持する。ドープシートの見た目（◆のハイライト）も、
//  移動・削除・コピー等の一括操作も、すべてこの集合を入力にする。
//
//  【なぜ添字で持ち、どう正規化するか】
//   キーは AnimKey オブジェクトだが、トラック内は常に時刻昇順へ並べ替えられる
//   （AnimKeyEditor.SortKeys）ため、編集のたびに添字は動く。そこで
//    ・並べ替えを伴う操作の直後は SetFromKeys（オブジェクト同一性で引き直す）
//    ・キー削除の直後は NormalizeAfterDelete（消えた分だけ添字を詰める）
//    ・クリップ差し替え・Undo 復元の直後は Normalize（範囲外を捨てる）
//   の 3 経路で必ず正規化する。正規化を忘れると「別のキーが選ばれている」
//   という最悪の壊れ方をするため、経路をこのクラスに閉じ込めてテストで固定する。
//
//  【Undo との連携】
//   Undo スナップショットには選択も一緒に積む（AnimUndoSnapshot）。
//   そのため選択の JSON 直列化・復元もここが持つ。
//
//  本クラスは WPF に依存しない（editor/tests から直接リンクしてテストする）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.Panels.AnimationTimeline;

/// <summary>1 個のキーを指す参照（トラック添字とキー添字の組）。</summary>
/// <param name="TrackIndex">クリップ内のトラック添字。</param>
/// <param name="KeyIndex">トラック内のキー添字（時刻昇順）。</param>
internal readonly record struct AnimKeyRef(int TrackIndex, int KeyIndex);

/// <summary>ドープシート上で選択中のキー集合。純粋なコレクションであり UI へは依存しない。</summary>
internal sealed class AnimKeySelection
{
    /// <summary>選択中のキー参照。順序は持たない（列挙時に <see cref="Ordered"/> で整列する）。</summary>
    private readonly HashSet<AnimKeyRef> _items = new();

    /// <summary>選択件数。</summary>
    public int Count => _items.Count;

    /// <summary>1 個も選ばれていないか。</summary>
    public bool IsEmpty => _items.Count == 0;

    /// <summary>複数選択中か（値エディタの表示切り替えに使う）。</summary>
    public bool IsMultiple => _items.Count > 1;

    /// <summary>指定キーが選択中か。</summary>
    public bool Contains(int trackIndex, int keyIndex) => _items.Contains(new AnimKeyRef(trackIndex, keyIndex));

    /// <summary>選択をすべて解除する。</summary>
    public void Clear() => _items.Clear();

    /// <summary>選択を 1 個だけにする（通常クリック）。</summary>
    public void SelectSingle(int trackIndex, int keyIndex)
    {
        _items.Clear();
        _items.Add(new AnimKeyRef(trackIndex, keyIndex));
    }

    /// <summary>選択へ追加する（Shift+クリック・ラバーバンドの追加選択）。</summary>
    public void Add(int trackIndex, int keyIndex) => _items.Add(new AnimKeyRef(trackIndex, keyIndex));

    /// <summary>複数まとめて追加する。</summary>
    public void AddRange(IEnumerable<AnimKeyRef> keys)
    {
        foreach (var k in keys) _items.Add(k);
    }

    /// <summary>選択から外す。</summary>
    public void Remove(int trackIndex, int keyIndex) => _items.Remove(new AnimKeyRef(trackIndex, keyIndex));

    /// <summary>選択の有無を反転する（Ctrl+クリック）。反転後に選択されていれば true。</summary>
    public bool Toggle(int trackIndex, int keyIndex)
    {
        var key = new AnimKeyRef(trackIndex, keyIndex);
        if (_items.Remove(key)) return false;
        _items.Add(key);
        return true;
    }

    /// <summary>クリップの全キーを選択する（Ctrl+A）。</summary>
    public void SelectAll(IReadOnlyList<AnimTrack> tracks)
    {
        _items.Clear();
        for (int ti = 0; ti < tracks.Count; ti++)
            for (int ki = 0; ki < tracks[ti].Keys.Count; ki++)
                _items.Add(new AnimKeyRef(ti, ki));
    }

    /// <summary>
    /// 指定フレームにキーを持つ全トラックのキーを選択する
    /// （サマリー行「全チャンネル」の◆クリック）。
    /// </summary>
    public void SelectFrame(IReadOnlyList<AnimTrack> tracks, float time, float fps)
    {
        _items.Clear();
        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var ki = AnimKeyEditor.FindKeyAtFrame(tracks[ti], time, fps);
            if (ki >= 0) _items.Add(new AnimKeyRef(ti, ki));
        }
    }

    /// <summary>選択をトラック昇順・キー昇順で列挙する（決定的な順序が要る処理用）。</summary>
    public List<AnimKeyRef> Ordered()
        => _items.OrderBy(k => k.TrackIndex).ThenBy(k => k.KeyIndex).ToList();

    /// <summary>選択をトラックごとにまとめて返す（トラック単位で処理する操作用）。</summary>
    public List<(int TrackIndex, List<int> KeyIndices)> ByTrack()
        => _items.GroupBy(k => k.TrackIndex)
                 .OrderBy(g => g.Key)
                 .Select(g => (g.Key, g.Select(k => k.KeyIndex).OrderBy(i => i).ToList()))
                 .ToList();

    // ── 正規化 ──────────────────────────────────────────────────

    /// <summary>
    /// 範囲外になった選択を捨てる（トラック削除・クリップ差し替え・Undo 復元の直後）。
    /// </summary>
    public void Normalize(IReadOnlyList<AnimTrack> tracks)
        => _items.RemoveWhere(k =>
               k.TrackIndex < 0 || k.TrackIndex >= tracks.Count ||
               k.KeyIndex   < 0 || k.KeyIndex   >= tracks[k.TrackIndex].Keys.Count);

    /// <summary>
    /// キー削除の直後に添字を詰める。削除されたキー自身は選択から外し、
    /// 同じトラック内でそれより後ろにあったキーの添字を削除数だけ前へずらす。
    /// </summary>
    /// <param name="deleted">削除したキー参照（削除前の添字）。</param>
    public void NormalizeAfterDelete(IEnumerable<AnimKeyRef> deleted)
    {
        var deletedSet = new HashSet<AnimKeyRef>(deleted);
        if (deletedSet.Count == 0) return;

        var survivors = new List<AnimKeyRef>(_items.Count);
        foreach (var item in _items)
        {
            if (deletedSet.Contains(item)) continue;   // 消えたキーは選択から外す
            // 同一トラックで自分より前に消えた数だけ添字を詰める
            var shift = deletedSet.Count(d => d.TrackIndex == item.TrackIndex && d.KeyIndex < item.KeyIndex);
            survivors.Add(new AnimKeyRef(item.TrackIndex, item.KeyIndex - shift));
        }

        _items.Clear();
        foreach (var s in survivors) _items.Add(s);
    }

    /// <summary>
    /// 並べ替えを伴う編集（移動・貼り付け）の直後に、キーオブジェクトの同一性から
    /// 添字を引き直す。添字ベースの選択が並べ替えでズレるのを一点で防ぐ。
    /// </summary>
    /// <param name="tracks">クリップの全トラック。</param>
    /// <param name="keys">選択させたい (トラック添字, キーオブジェクト) の列。</param>
    public void SetFromKeys(IReadOnlyList<AnimTrack> tracks, IEnumerable<(int TrackIndex, AnimKey Key)> keys)
    {
        _items.Clear();
        foreach (var (ti, key) in keys)
        {
            if (ti < 0 || ti >= tracks.Count) continue;
            var ki = tracks[ti].Keys.IndexOf(key);
            if (ki >= 0) _items.Add(new AnimKeyRef(ti, ki));
        }
    }

    // ── JSON 直列化（Undo スナップショットへ同梱する）────────────

    /// <summary>選択を JSON 配列（[[track,key],...]）へ直列化する。</summary>
    public string Serialize()
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var k in Ordered())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('[').Append(k.TrackIndex).Append(',').Append(k.KeyIndex).Append(']');
        }
        return sb.Append(']').ToString();
    }

    /// <summary>
    /// <see cref="Serialize"/> の出力から選択を復元する。
    /// 壊れた JSON は「選択なし」として扱う（Undo 復元を例外で止めないため）。
    /// </summary>
    public void Restore(string json)
    {
        _items.Clear();
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var pair in doc.RootElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                _items.Add(new AnimKeyRef(pair[0].GetInt32(), pair[1].GetInt32()));
            }
        }
        catch (JsonException)
        {
            _items.Clear();
        }
    }
}
