using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Controls;

/// <summary>
/// 音声辞書（AudioDictionaryComponent）のワイヤ表現を扱う純ロジック。
///
/// ランタイムから届く <c>ACTOR_COMPONENTS</c> の <c>"groups"</c> 配列 JSON を
/// エディタが扱う型へ落とし、逆に編集結果を <c>SET_AUDIO_DICT</c> 用の JSON へ戻す。
/// キー（<c>グループ名/用途名</c>）の組み立て規則もここに集約する。
///
/// <b>WPF に一切依存しない</b>（単体テスト <c>editor/tests/AudioDictionaryTests</c> が
/// このファイルだけをリンクして検証できるようにするため）。
/// </summary>
internal static class AudioDictionaryCatalog
{
    /// <summary>キーの区切り文字（Rust 側 <c>AUDIO_DICT_KEY_SEPARATOR</c> と一致必須）。</summary>
    public const char KeySeparator = '/';

    /// <summary>グループが 1 つも無いときのワイヤ表現（空の JSON 配列）。</summary>
    public const string EmptyGroupsJson = "[]";

    /// <summary>行の既定音量（Rust 側 <c>DEFAULT_AUDIO_DICT_VOLUME</c> と一致必須）。</summary>
    public const float DefaultVolume = 1f;

    /// <summary>音量の下限（負の音量は意味を持たないため 0 で止める）。</summary>
    public const float MinVolume = 0f;

    /// <summary>新しく追加したグループの既定名。</summary>
    public const string NewGroupName = "NewGroup";

    /// <summary>新しく追加した行の既定用途名。</summary>
    public const string NewEntryUsage = "new";

    /// <summary>音声ファイルとして受け付ける拡張子（AudioComponent の音声パス欄と同一）。</summary>
    public static readonly string[] AudioExtensions = [".wav", ".ogg", ".mp3", ".flac"];

    // ── 編集用のモデル（ミュータブル：インスペクタが直接書き換える）──

    /// <summary>音声辞書の 1 行（用途名・パス・既定音量）。</summary>
    internal sealed class Entry
    {
        /// <summary>用途名（例 <c>attack</c>）。グループ名と合わせてキーになる。</summary>
        public string Usage { get; set; } = "";

        /// <summary>音声ファイルの assets:// 仮想パス。空 = 未設定。</summary>
        public string Path { get; set; } = "";

        /// <summary>既定音量（1.0 = 等倍）。</summary>
        public float Volume { get; set; } = DefaultVolume;
    }

    /// <summary>音声辞書のグループ（名前 + 行の配列）。</summary>
    internal sealed class Group
    {
        /// <summary>グループ名（例 <c>Player</c>）。</summary>
        public string Name { get; set; } = "";

        /// <summary>このグループに属する行。</summary>
        public List<Entry> Entries { get; } = [];
    }

    // ── キーの組み立て ───────────────────────────────────────

    /// <summary>
    /// グループ名と用途名からキー文字列（<c>グループ名/用途名</c>）を作る。
    /// ランタイム側 <c>dictionary_index::make_key</c> と同じ規則。
    /// </summary>
    public static string MakeKey(string groupName, string usage)
        => groupName + KeySeparator + usage;

    /// <summary>
    /// キーが「引ける形」か（グループ名・用途名がともに非空か）。
    /// 作りかけの行を一覧へ出さないための判定。
    /// </summary>
    public static bool IsCompleteKey(string groupName, string usage)
        => !string.IsNullOrEmpty(groupName) && !string.IsNullOrEmpty(usage);

    // ── ワイヤ表現 ⇔ 編集モデル ──────────────────────────────

    /// <summary>
    /// <c>"groups"</c> 配列 JSON を編集モデルへ読み込む。
    /// 不正な JSON・欠落フィールドは既定値で埋め、例外を投げない
    /// （インスペクタが壊れた応答で落ちないようにするため）。
    /// </summary>
    public static List<Group> ParseGroups(string? groupsJson)
    {
        var result = new List<Group>();
        if (string.IsNullOrWhiteSpace(groupsJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(groupsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var g in doc.RootElement.EnumerateArray())
            {
                var group = new Group
                {
                    Name = g.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "",
                };
                if (g.TryGetProperty("entries", out var ep) && ep.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in ep.EnumerateArray())
                    {
                        group.Entries.Add(new Entry
                        {
                            Usage  = e.TryGetProperty("usage",  out var up) ? up.GetString() ?? "" : "",
                            Path   = e.TryGetProperty("path",   out var pp) ? pp.GetString() ?? "" : "",
                            Volume = e.TryGetProperty("volume", out var vp) && vp.ValueKind == JsonValueKind.Number
                                     ? vp.GetSingle() : DefaultVolume,
                        });
                    }
                }
                result.Add(group);
            }
        }
        catch (JsonException) { /* 不正 JSON は空リスト扱い（呼び出し側を落とさない） */ }

        return result;
    }

    /// <summary>
    /// 編集モデルを <c>SET_AUDIO_DICT</c> のペイロード JSON（<c>{"groups":[...]}</c>）へ変換する。
    /// ランタイム側 <c>AudioDictionaryComponentData</c> と serde 互換。
    /// </summary>
    public static string ToPayloadJson(IReadOnlyList<Group> groups)
    {
        var payload = new
        {
            groups = System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(groups, g => new
                {
                    name = g.Name,
                    entries = System.Linq.Enumerable.ToArray(
                        System.Linq.Enumerable.Select(g.Entries, e => new
                        {
                            usage = e.Usage,
                            path = e.Path,
                            volume = e.Volume,
                        })),
                })),
        };
        return JsonSerializer.Serialize(payload);
    }

    // ── キー一覧の整形（ピッカー表示用）────────────────────────

    /// <summary>ピッカーに出す 1 行分（キーと補足表示）。</summary>
    /// <param name="Key">保存されるキー文字列（<c>グループ名/用途名</c>）。</param>
    /// <param name="Usage">用途名（グループ見出しの下に並べる表示テキスト）。</param>
    /// <param name="Path">音声ファイルの assets:// パス（ツールチップ・副表示用）。</param>
    internal sealed record KeyRow(string Key, string Usage, string Path);

    /// <summary>ピッカーに出すグループ 1 つ分（見出し + 行）。</summary>
    /// <param name="GroupName">グループ名（見出しテキスト）。</param>
    /// <param name="Rows">このグループのキー行（入力順を保つ）。</param>
    internal sealed record KeyGroup(string GroupName, IReadOnlyList<KeyRow> Rows);

    /// <summary>
    /// グループ配列 JSON から「グループごとにまとめたキー一覧」を作る。
    ///
    /// 一覧へ出すのは <b>引ける行だけ</b>（グループ名・用途名・パスがすべて非空）。
    /// 作りかけの行を選べてしまうと、選んだ瞬間に解決できないキーが保存されるため。
    /// 行が 1 つも残らないグループは見出しごと落とす。
    /// </summary>
    public static List<KeyGroup> BuildKeyGroups(string? groupsJson)
        => BuildKeyGroups(ParseGroups(groupsJson));

    /// <summary>
    /// 編集モデルから「グループごとにまとめたキー一覧」を作る（上のオーバーロードの実体）。
    /// </summary>
    public static List<KeyGroup> BuildKeyGroups(IReadOnlyList<Group> groups)
    {
        var result = new List<KeyGroup>();
        foreach (var g in groups)
        {
            if (string.IsNullOrEmpty(g.Name)) continue;

            var rows = new List<KeyRow>();
            foreach (var e in g.Entries)
            {
                if (!IsCompleteKey(g.Name, e.Usage)) continue;
                if (string.IsNullOrEmpty(e.Path)) continue;
                rows.Add(new KeyRow(MakeKey(g.Name, e.Usage), e.Usage, e.Path));
            }
            if (rows.Count > 0) result.Add(new KeyGroup(g.Name, rows));
        }
        return result;
    }

    /// <summary>
    /// キー一覧に含まれるキーの総数（「N 件のキー」表示用）。
    /// </summary>
    public static int CountKeys(IReadOnlyList<KeyGroup> keyGroups)
    {
        var n = 0;
        foreach (var g in keyGroups) n += g.Rows.Count;
        return n;
    }

    // ── ACTOR_COMPONENTS からの抽出 ──────────────────────────

    /// <summary>
    /// <c>ACTOR_COMPONENTS</c> の応答 JSON から、最初の AudioDictionaryComponent スロットの
    /// <c>"groups"</c> 配列を生 JSON で取り出す（無ければ null）。
    ///
    /// 参照ピッカーの共通経路（<c>ActorComponentSnapshot</c>）は型と名前しか運ばないため、
    /// 辞書の中身が要るこの用途だけは応答 JSON を直接読む。
    /// </summary>
    /// <param name="actorComponentsJson">ACTOR_COMPONENTS の JSON 本文。</param>
    /// <param name="componentTypeId">抽出対象の型名（通常 "AudioDictionaryComponent"）。</param>
    public static string? ExtractGroupsJson(string actorComponentsJson, string componentTypeId)
    {
        if (string.IsNullOrWhiteSpace(actorComponentsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(actorComponentsJson);
            if (!doc.RootElement.TryGetProperty("components", out var comps) ||
                comps.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var c in comps.EnumerateArray())
            {
                if (!c.TryGetProperty("type", out var t)) continue;
                if (!string.Equals(t.GetString(), componentTypeId, StringComparison.Ordinal)) continue;
                if (c.TryGetProperty("groups", out var g)) return g.GetRawText();
            }
        }
        catch (JsonException) { /* 壊れた応答は「辞書なし」と同じ扱い */ }
        return null;
    }
}
