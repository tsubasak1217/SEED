using System;
using System.Linq;
using System.Text.Json;
using SEEDEditor.Controls;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace AudioDictionaryTests;

/// <summary>
/// 音声辞書（AudioDictionaryComponent）のワイヤ表現・キー一覧整形の単体テスト。
///
/// ここで固定したいのは次の 3 点。
///   1. ランタイムから届く groups 配列を読み、編集し、同じ形で返せる（ラウンドトリップ）
///   2. キー一覧（ピッカー表示用）に「未完成の行」を出さない
///      — 出してしまうと、選んだ瞬間に解決できないキーが保存されて無音になる
///   3. ACTOR_COMPONENTS の応答から辞書の中身だけを取り出せる
///      — 参照ピッカー共通経路（ActorComponentSnapshot）は型と名前しか運ばないため、
///        ここだけは応答 JSON を直接読む必要がある
/// </summary>
public static class Program
{
    public static int Main()
    {
        var h = new TestHarness();

        // ── 1. groups 配列の読み込み ─────────────────────────────

        h.Add("groups 配列を読み込める", () =>
        {
            const string json = """
                [{"name":"Player","entries":[
                    {"usage":"attack","path":"assets://se/atk.wav","volume":0.8},
                    {"usage":"jump","path":"assets://se/jmp.wav","volume":1.0}]}]
                """;
            var groups = AudioDictionaryCatalog.ParseGroups(json);
            Check.Equal(1, groups.Count, "グループ数");
            Check.Equal("Player", groups[0].Name, "グループ名");
            Check.Equal(2, groups[0].Entries.Count, "行数");
            Check.Equal("attack", groups[0].Entries[0].Usage, "用途名");
            Check.Equal("assets://se/jmp.wav", groups[0].Entries[1].Path, "パス");
            Check.Close(0.8, groups[0].Entries[0].Volume, 1e-6, "音量");
        });

        h.Add("volume 省略は既定音量になる", () =>
        {
            // ランタイム側 serde の #[serde(default = "default_entry_volume")] と同じ振る舞い
            const string json = """[{"name":"P","entries":[{"usage":"a","path":"assets://a.wav"}]}]""";
            var groups = AudioDictionaryCatalog.ParseGroups(json);
            Check.Close(AudioDictionaryCatalog.DefaultVolume, groups[0].Entries[0].Volume, 1e-6, "既定音量");
        });

        h.Add("壊れた JSON でも例外を投げず空リストになる", () =>
        {
            // 応答が壊れていてもインスペクタは落としてはいけない
            Check.Equal(0, AudioDictionaryCatalog.ParseGroups("{ これは JSON ではない").Count, "壊れた JSON");
            Check.Equal(0, AudioDictionaryCatalog.ParseGroups(null).Count, "null");
            Check.Equal(0, AudioDictionaryCatalog.ParseGroups("").Count, "空文字列");
            // 配列ではなくオブジェクトが来た場合も空扱い
            Check.Equal(0, AudioDictionaryCatalog.ParseGroups("{\"groups\":[]}").Count, "配列でない");
        });

        // ── 2. ラウンドトリップ ──────────────────────────────────

        h.Add("読み込み → ペイロード JSON → 再読み込みで内容が保たれる", () =>
        {
            const string json = """
                [{"name":"Player","entries":[{"usage":"attack","path":"assets://se/atk.wav","volume":0.5}]},
                 {"name":"Enemy","entries":[{"usage":"roar","path":"assets://se/roar.ogg","volume":1.0}]}]
                """;
            var groups  = AudioDictionaryCatalog.ParseGroups(json);
            var payload = AudioDictionaryCatalog.ToPayloadJson(groups);

            // ペイロードは {"groups":[...]} 形（ランタイムの AudioDictionaryComponentData と serde 互換）
            using var doc = JsonDocument.Parse(payload);
            Check.True(doc.RootElement.TryGetProperty("groups", out var g), "groups プロパティがある");

            var back = AudioDictionaryCatalog.ParseGroups(g.GetRawText());
            Check.Equal(groups.Count, back.Count, "グループ数");
            for (var i = 0; i < groups.Count; i++)
            {
                Check.Equal(groups[i].Name, back[i].Name, $"グループ{i} の名前");
                Check.Equal(groups[i].Entries.Count, back[i].Entries.Count, $"グループ{i} の行数");
                for (var j = 0; j < groups[i].Entries.Count; j++)
                {
                    Check.Equal(groups[i].Entries[j].Usage, back[i].Entries[j].Usage, $"[{i}][{j}] 用途名");
                    Check.Equal(groups[i].Entries[j].Path,  back[i].Entries[j].Path,  $"[{i}][{j}] パス");
                    Check.Close(groups[i].Entries[j].Volume, back[i].Entries[j].Volume, 1e-6, $"[{i}][{j}] 音量");
                }
            }
        });

        h.Add("空のグループ配列もペイロードにできる", () =>
        {
            var payload = AudioDictionaryCatalog.ToPayloadJson([]);
            using var doc = JsonDocument.Parse(payload);
            Check.Equal(0, doc.RootElement.GetProperty("groups").GetArrayLength(), "空配列");
        });

        // ── 3. キーの組み立て ────────────────────────────────────

        h.Add("キーは グループ名/用途名 になる", () =>
        {
            Check.Equal("Player/attack", AudioDictionaryCatalog.MakeKey("Player", "attack"), "キー文字列");
            Check.True(AudioDictionaryCatalog.IsCompleteKey("Player", "attack"), "両方あれば完成");
            Check.True(!AudioDictionaryCatalog.IsCompleteKey("", "attack"), "グループ名が空なら未完成");
            Check.True(!AudioDictionaryCatalog.IsCompleteKey("Player", ""), "用途名が空なら未完成");
        });

        // ── 4. キー一覧の整形（ピッカー表示用）───────────────────

        h.Add("キー一覧はグループごとにまとまる", () =>
        {
            const string json = """
                [{"name":"Player","entries":[
                    {"usage":"attack","path":"assets://a.wav","volume":1.0},
                    {"usage":"jump","path":"assets://b.wav","volume":1.0}]},
                 {"name":"Enemy","entries":[
                    {"usage":"roar","path":"assets://c.wav","volume":1.0}]}]
                """;
            var keyGroups = AudioDictionaryCatalog.BuildKeyGroups(json);
            Check.Equal(2, keyGroups.Count, "グループ数");
            Check.Equal("Player", keyGroups[0].GroupName, "1 つ目のグループ名");
            Check.Equal(2, keyGroups[0].Rows.Count, "Player の行数");
            Check.Equal("Player/attack", keyGroups[0].Rows[0].Key, "1 行目のキー");
            Check.Equal("attack", keyGroups[0].Rows[0].Usage, "1 行目の用途名");
            Check.Equal("assets://a.wav", keyGroups[0].Rows[0].Path, "1 行目のパス");
            Check.Equal("Enemy/roar", keyGroups[1].Rows[0].Key, "2 つ目のグループの行");
            Check.Equal(3, AudioDictionaryCatalog.CountKeys(keyGroups), "キー総数");
        });

        h.Add("未完成の行はキー一覧に出ない", () =>
        {
            // グループ名なし／用途名なし／パスなし はいずれも「選べてはいけない」行。
            // これを出すと、選んだ瞬間に解決できないキーが保存されて無音になる。
            const string json = """
                [{"name":"","entries":[{"usage":"a","path":"assets://a.wav","volume":1.0}]},
                 {"name":"Player","entries":[
                    {"usage":"","path":"assets://b.wav","volume":1.0},
                    {"usage":"jump","path":"","volume":1.0},
                    {"usage":"dash","path":"assets://c.wav","volume":1.0}]}]
                """;
            var keyGroups = AudioDictionaryCatalog.BuildKeyGroups(json);
            Check.Equal(1, keyGroups.Count, "残るグループ数（名前なしグループは落ちる）");
            Check.Equal("Player", keyGroups[0].GroupName, "残るグループ名");
            Check.Equal(1, keyGroups[0].Rows.Count, "残る行数");
            Check.Equal("Player/dash", keyGroups[0].Rows[0].Key, "残る行のキー");
        });

        h.Add("有効な行が 1 つも無いグループは見出しごと落ちる", () =>
        {
            const string json = """[{"name":"Player","entries":[{"usage":"a","path":"","volume":1.0}]}]""";
            var keyGroups = AudioDictionaryCatalog.BuildKeyGroups(json);
            Check.Equal(0, keyGroups.Count, "グループ数");
            Check.Equal(0, AudioDictionaryCatalog.CountKeys(keyGroups), "キー総数");
        });

        // ── 5. ACTOR_COMPONENTS 応答からの抽出 ───────────────────

        h.Add("ACTOR_COMPONENTS から辞書の groups を取り出せる", () =>
        {
            const string json = """
                {"id":3,"name":"SoundBank","components":[
                    {"type":"AudioComponent","slot":0,"name":"Audio","audio_path":"assets://x.wav"},
                    {"type":"AudioDictionaryComponent","slot":1,"name":"AudioDictionary",
                     "groups":[{"name":"Player","entries":[
                        {"usage":"attack","path":"assets://a.wav","volume":1.0}]}]}]}
                """;
            var groupsJson = AudioDictionaryCatalog.ExtractGroupsJson(json, "AudioDictionaryComponent");
            Check.True(groupsJson is not null, "groups を取り出せる");
            var keyGroups = AudioDictionaryCatalog.BuildKeyGroups(groupsJson);
            Check.Equal(1, keyGroups.Count, "グループ数");
            Check.Equal("Player/attack", keyGroups[0].Rows[0].Key, "キー");
        });

        h.Add("辞書を持たないアクタからは null が返る", () =>
        {
            const string json = """
                {"id":3,"name":"NoDict","components":[
                    {"type":"AudioComponent","slot":0,"name":"Audio","audio_path":"assets://x.wav"}]}
                """;
            Check.True(AudioDictionaryCatalog.ExtractGroupsJson(json, "AudioDictionaryComponent") is null,
                "辞書なしは null");
        });

        h.Add("壊れた ACTOR_COMPONENTS でも例外にならない", () =>
        {
            Check.True(AudioDictionaryCatalog.ExtractGroupsJson("{ 壊れた", "AudioDictionaryComponent") is null,
                "壊れた JSON は null");
            Check.True(AudioDictionaryCatalog.ExtractGroupsJson("", "AudioDictionaryComponent") is null,
                "空文字列は null");
            Check.True(AudioDictionaryCatalog.ExtractGroupsJson("{\"id\":1}", "AudioDictionaryComponent") is null,
                "components が無いときは null");
        });

        // ── 6. ランタイム側の定数との一致 ────────────────────────

        h.Add("キー区切りと既定音量はランタイムと同じ値", () =>
        {
            // Rust 側 AUDIO_DICT_KEY_SEPARATOR = '/' / DEFAULT_AUDIO_DICT_VOLUME = 1.0
            // ここがずれると、エディタで作ったキーをランタイムが引けない（無音）。
            Check.Equal('/', AudioDictionaryCatalog.KeySeparator, "キー区切り");
            Check.Close(1.0, AudioDictionaryCatalog.DefaultVolume, 1e-6, "既定音量");
            Check.True(AudioDictionaryCatalog.AudioExtensions.SequenceEqual(
                new[] { ".wav", ".ogg", ".mp3", ".flac" }), "音声拡張子（AudioComponent の音声パス欄と同一）");
        });

        return h.Run();
    }
}
