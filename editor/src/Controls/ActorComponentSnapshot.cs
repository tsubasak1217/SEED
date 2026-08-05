using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Controls;

/// <summary>
/// アクタが持つコンポーネント 1 件分の最小情報（参照解決に必要な分だけ）。
/// ACTOR_COMPONENTS JSON の components[] 要素から取り出す。
/// </summary>
/// <param name="TypeId">コンポーネント種別（例: "CameraComponent"）。</param>
/// <param name="SlotIdx">アクタ内のスロット添字。</param>
/// <param name="Name">スロット名（リネーム可能な表示名。空の場合あり）。</param>
internal sealed record ActorComponentEntry(string TypeId, int SlotIdx, string Name);

/// <summary>
/// ACTOR_COMPONENTS 応答 JSON から、参照解決に必要な情報だけを抜き出したスナップショット。
///
/// 参照ピッカーは「落とされたアクタが要求型のコンポーネントを持つか」を判定するために
/// この形へ正規化して扱う。JSON の細かな構造を知る場所をここ 1 箇所に閉じ込める。
/// </summary>
internal sealed class ActorComponentSnapshot
{
    /// <summary>アクタの DFS ID。</summary>
    public int DfsId { get; init; }

    /// <summary>アクタ名（参照値として保存される文字列）。</summary>
    public string ActorName { get; init; } = "";

    /// <summary>3D アクタのルート Transform を持つか。</summary>
    public bool HasTransform { get; init; }

    /// <summary>2D アクタのルート CanvasTransform を持つか。</summary>
    public bool HasCanvasTransform { get; init; }

    /// <summary>スロットに格納されたコンポーネント一覧（スロット添字の昇順とは限らない）。</summary>
    public IReadOnlyList<ActorComponentEntry> Components { get; init; } = Array.Empty<ActorComponentEntry>();

    /// <summary>
    /// ACTOR_COMPONENTS 応答 JSON を解析する。解析できなければ null。
    /// </summary>
    public static ActorComponentSnapshot? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var comps = new List<ActorComponentEntry>();
            if (root.TryGetProperty("components", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var comp in arr.EnumerateArray())
                {
                    var typeId  = comp.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(typeId)) continue;
                    var slotIdx = comp.TryGetProperty("slot", out var s) && s.TryGetInt32(out var si) ? si : 0;
                    var name    = comp.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    comps.Add(new ActorComponentEntry(typeId, slotIdx, name));
                }
            }

            return new ActorComponentSnapshot
            {
                DfsId              = root.TryGetProperty("id", out var idp) && idp.TryGetInt32(out var id) ? id : -1,
                ActorName          = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "",
                HasTransform       = root.TryGetProperty("transform", out _),
                HasCanvasTransform = root.TryGetProperty("canvas_transform", out _),
                Components         = comps,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>指定の ACTOR_COMPONENTS type 文字列に一致するコンポーネントを列挙する。</summary>
    public List<ActorComponentEntry> ComponentsOfType(string typeId)
    {
        var result = new List<ActorComponentEntry>();
        foreach (var c in Components)
            if (c.TypeId == typeId) result.Add(c);
        return result;
    }

    /// <summary>
    /// スロットの表示・保存に使う名前。スロット名が空のときは種別名と添字で代替する
    /// （名前で参照を復元できないため、一意になる表記へフォールバックする）。
    /// </summary>
    public static string SlotDisplayName(ActorComponentEntry entry, string kind)
        => string.IsNullOrEmpty(entry.Name) ? $"{kind}[{entry.SlotIdx}]" : entry.Name;
}

/// <summary>
/// アクタ DFS ID → 直近に受信した <see cref="ActorComponentSnapshot"/> のキャッシュ。
///
/// 用途は「ドラッグ中の可否判定」の 1 点のみ。ドロップ確定時の解決は必ず
/// GET_ACTOR_COMPONENTS の往復（＝最新の実データ）で行うため、
/// このキャッシュが古くても参照が誤って設定されることはない。
/// キャッシュに無いアクタは「判定不能」として扱い、ドロップは許可する
/// （従来どおりドロップ後に検証してエラーを出す挙動へフォールバックする）。
/// </summary>
internal static class ActorComponentCache
{
    /// <summary>DFS ID → スナップショット。</summary>
    private static readonly Dictionary<int, ActorComponentSnapshot> Entries = new();

    /// <summary>受信したスナップショットを登録する（DFS ID 不明のものは無視）。</summary>
    public static void Store(ActorComponentSnapshot snapshot)
    {
        if (snapshot.DfsId < 0) return;
        Entries[snapshot.DfsId] = snapshot;
    }

    /// <summary>DFS ID からスナップショットを引く。未受信なら null。</summary>
    public static ActorComponentSnapshot? TryGet(int dfsId)
        => Entries.TryGetValue(dfsId, out var s) ? s : null;

    /// <summary>
    /// キャッシュを全消去する。DFS ID はシーンの構造が変わると別アクタを指すため、
    /// シーン読み込み・シーン切り替えのタイミングで必ず呼ぶこと。
    /// </summary>
    public static void Clear() => Entries.Clear();
}
