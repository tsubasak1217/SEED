// ============================================================
//  LocaleJsonDocument.cs — 多言語のデータファイル 1 つを「項目の並び」として読み書きする（純粋な計算）
//
//  【役割】
//  文字列表のパネル（editor/src/Panels/LocalizationPanel）が index.json・<言語>.json を編集するための、
//  形を失わない読み込み。実行中の読み込み（SEED.Localization の LocaleTable）は値だけを残して
//  null・説明の鍵（_about）・配列の形・鍵の並びを捨てるが、編集して書き戻すにはそれらが要る。
//  そこでファイルを「鍵の道筋 + 値」の項目の並び（LocaleJsonWriter.Entry）として持ち、書き出しは
//  LocaleJsonWriter（scripting の Model。平たん化の逆）へ渡す。
//
//  【同じ規則を二重に持たない】
//  読み方の約束（コメントと末尾のカンマを許す・"_" で始まる鍵は説明・"." でつなぐ・配列は番号のキー）は
//  LocaleJson の定数と関数をそのまま使う。値の解釈（複数形のまとまり・重なったキーの勝ち負け）は
//  LocaleTable.Parse に任せ、ここは形を保つことだけを受け持つ（LocaleTableModel が両方を突き合わせる）。
//
//  【項目の種類】
//    - キーの項目 … 道筋に説明の鍵・空の鍵を含まない値（文字列・数・真偽値・null）。表の行になる
//    - 説明の項目 … 道筋のどこかが "_" で始まる（_about など）。行にはしないが、書き戻しで残す
//    - 空のまとまり … 中身の無い {} / []。字句（Raw）として残す（その下にキーを足すときに消す）
//  同じ道筋が 2 度出てきたら後の値で上書きする（LocaleTable と同じく後のものが勝つ）。
//
//  【改行】読んだファイルに "\r\n" があれば "\r\n"、無ければ "\n" で書き戻す（差分を改行だけにしない）。
//  【制限】JSON のコメント（// や /* */）は読めるが書き戻しで消える。エスケープ（バックスラッシュ u3042 の形など）は文字に戻る。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SEED.Localization;

namespace SEEDEditor.Localization.Json;

/// <summary>多言語のデータファイル 1 つ（項目の並び）。</summary>
public sealed class LocaleJsonDocument
{
    /// <summary>Windows の改行。</summary>
    private const string CrLf = "\r\n";

    /// <summary>空のオブジェクトの字句。</summary>
    public const string EmptyObjectLiteral = "{}";

    /// <summary>空の配列の字句。</summary>
    public const string EmptyArrayLiteral = "[]";

    /// <summary>真の字句。</summary>
    private const string TrueLiteral = "true";

    /// <summary>偽の字句。</summary>
    private const string FalseLiteral = "false";

    /// <summary>道筋の照合の鍵で、鍵の長さと中身を区切る文字（長さを前に付けるので中身に何があっても一意）。</summary>
    private const char PathKeyLengthSeparator = ':';

    /// <summary>項目（ファイルの順）。</summary>
    private readonly List<LocaleJsonWriter.Entry> _entries = new();

    /// <summary>項目ごとの平たいキー（並びは _entries と同じ）。</summary>
    private readonly List<string> _flatKeys = new();

    /// <summary>平たいキー → キーの項目の位置（同じキーは後のもの）。変更で捨て、使うときに作り直す。</summary>
    private Dictionary<string, int>? _keyIndex;

    /// <summary>書き戻しの改行（読んだファイルに合わせる）。</summary>
    public string NewLine { get; }

    /// <summary>壊れた JSON の理由（読めたら空）。</summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>JSON として読めたか。</summary>
    public bool IsValid => Error.Length == 0;

    /// <summary>項目の数（説明・空のまとまりを含む）。</summary>
    public int Count => _entries.Count;

    /// <summary>項目（ファイルの順）。</summary>
    public IReadOnlyList<LocaleJsonWriter.Entry> Entries => _entries;

    /// <summary>空の文書を作る（Parse・CreateEmpty から）。</summary>
    /// <param name="newLine">書き戻しの改行。</param>
    private LocaleJsonDocument(string newLine)
    {
        NewLine = newLine;
    }

    // ============================================================
    //  作る・読む
    // ============================================================

    /// <summary>空の文書（新しい言語の表）を作る。</summary>
    /// <param name="newLine">書き戻しの改行（null なら "\n"）。</param>
    /// <returns>文書。</returns>
    public static LocaleJsonDocument CreateEmpty(string? newLine = null) =>
        new(newLine ?? LocaleJsonWriter.DefaultNewLine);

    /// <summary>
    /// JSON を項目の並びとして読む（例外を投げない。壊れていれば項目 0 で <see cref="Error"/> に理由）。
    /// </summary>
    /// <param name="text">ファイルの中身。</param>
    /// <returns>文書。</returns>
    public static LocaleJsonDocument Parse(string? text)
    {
        string source = text ?? string.Empty;
        var document = new LocaleJsonDocument(DetectNewLine(source));
        try
        {
            using var json = JsonDocument.Parse(source, LocaleJson.ReadOptions);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Error = "最上位がオブジェクトではありません";
                return document;
            }

            // 同じ道筋の重なりを見つけるための表（読み込みの間だけ使う）
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            document.Walk(json.RootElement, new List<string>(), seen);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // ArgumentException: 対になっていないサロゲートのエスケープ（バックスラッシュ u D800 の形）を
            // JsonDocument が受け付けない（2026-10-03 に確かめた。Model/LocaleSafeParse の説明を参照）
            document._entries.Clear();
            document._flatKeys.Clear();
            document.Error = ex.Message;
        }
        return document;
    }

    /// <summary>文字列の改行の流儀を調べる（"\r\n" を含めば "\r\n"、無ければ "\n"）。</summary>
    /// <param name="text">ファイルの中身。</param>
    /// <returns>改行。</returns>
    public static string DetectNewLine(string text) =>
        text.Contains(CrLf, StringComparison.Ordinal) ? CrLf : LocaleJsonWriter.DefaultNewLine;

    /// <summary>JSON の値を 1 つ項目にする（オブジェクト・配列は子へ。中身が無ければ空のまとまりの字句）。</summary>
    /// <param name="element">値。</param>
    /// <param name="path">根からの道筋（呼び出しの間だけ伸び縮みする作業用の列）。</param>
    /// <param name="seen">道筋の照合の鍵 → 項目の位置。</param>
    private void Walk(JsonElement element, List<string> path, Dictionary<string, int> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                bool any = false;
                foreach (var property in element.EnumerateObject())
                {
                    any = true;
                    path.Add(property.Name);
                    Walk(property.Value, path, seen);
                    path.RemoveAt(path.Count - 1);
                }
                // 中身の無いオブジェクトは字句で残す（根の {} は項目にしない＝書き出しが {} になる）
                if (!any && path.Count > 0) AddParsed(path, LocaleJsonWriter.EntryKind.Raw, EmptyObjectLiteral, seen);
                break;
            }

            case JsonValueKind.Array:
            {
                int position = 0;
                foreach (var item in element.EnumerateArray())
                {
                    // 配列の要素は番号のキー（LocaleTable の平たん化と同じ書き方）
                    path.Add(position.ToString(CultureInfo.InvariantCulture));
                    Walk(item, path, seen);
                    path.RemoveAt(path.Count - 1);
                    position++;
                }
                if (position == 0) AddParsed(path, LocaleJsonWriter.EntryKind.Raw, EmptyArrayLiteral, seen);
                break;
            }

            case JsonValueKind.String:
                AddParsed(path, LocaleJsonWriter.EntryKind.String, element.GetString() ?? string.Empty, seen);
                break;

            case JsonValueKind.Number:
                AddParsed(path, LocaleJsonWriter.EntryKind.Raw, element.GetRawText(), seen);
                break;

            case JsonValueKind.True:
                AddParsed(path, LocaleJsonWriter.EntryKind.Raw, TrueLiteral, seen);
                break;

            case JsonValueKind.False:
                AddParsed(path, LocaleJsonWriter.EntryKind.Raw, FalseLiteral, seen);
                break;

            case JsonValueKind.Null:
                AddParsed(path, LocaleJsonWriter.EntryKind.Null, null, seen);
                break;
        }
    }

    /// <summary>読んだ項目を足す（同じ道筋が既にあれば、その位置のまま後の値で上書きする）。</summary>
    private void AddParsed(List<string> path, LocaleJsonWriter.EntryKind kind, string? value, Dictionary<string, int> seen)
    {
        var entry = new LocaleJsonWriter.Entry(path.ToArray(), kind, value);
        string pathKey = PathKey(entry.Path);
        if (seen.TryGetValue(pathKey, out int existing))
        {
            _entries[existing] = entry;
            return;
        }
        seen[pathKey] = _entries.Count;
        _entries.Add(entry);
        _flatKeys.Add(JoinPath(entry.Path));
    }

    // ============================================================
    //  書き出し
    // ============================================================

    /// <summary>入れ子の JSON の文字列にする（インデント 2・読んだときの改行・最後に改行）。</summary>
    /// <returns>JSON の文字列。</returns>
    public string Write() =>
        LocaleJsonWriter.Write(_entries, new LocaleJsonWriter.Options { NewLine = NewLine });

    // ============================================================
    //  項目の分類
    // ============================================================

    /// <summary>道筋を平たいキーにする（LocaleJson.Join で "." でつなぐ）。</summary>
    /// <param name="path">道筋。</param>
    /// <returns>平たいキー。</returns>
    public static string JoinPath(IReadOnlyList<string> path)
    {
        string key = string.Empty;
        foreach (var segment in path) key = LocaleJson.Join(key, segment);
        return key;
    }

    /// <summary>その位置の項目の平たいキー。</summary>
    /// <param name="index">項目の位置。</param>
    /// <returns>平たいキー。</returns>
    public string FlatKeyAt(int index) => _flatKeys[index];

    /// <summary>その位置の項目が表の行になるキーの項目か（説明の鍵・空の鍵・空のまとまりではない）。</summary>
    /// <param name="index">項目の位置。</param>
    /// <returns>キーの項目なら true。</returns>
    public bool IsKeyEntry(int index) => IsKeyPath(_entries[index].Path) && !IsPlaceholder(index);

    /// <summary>その位置の項目が説明の項目か（道筋のどこかが "_" で始まる）。</summary>
    /// <param name="index">項目の位置。</param>
    /// <returns>説明の項目なら true。</returns>
    public bool IsCommentEntry(int index)
    {
        foreach (var segment in _entries[index].Path)
            if (LocaleJson.IsComment(segment)) return true;
        return false;
    }

    /// <summary>その位置の項目が空のまとまり（{} / []）の字句か。</summary>
    /// <param name="index">項目の位置。</param>
    /// <returns>空のまとまりなら true。</returns>
    public bool IsPlaceholder(int index)
    {
        var entry = _entries[index];
        return entry.Kind == LocaleJsonWriter.EntryKind.Raw
            && (entry.Value == EmptyObjectLiteral || entry.Value == EmptyArrayLiteral);
    }

    /// <summary>道筋がキーの道筋か（空の鍵・説明の鍵を含まない）。</summary>
    /// <param name="path">道筋。</param>
    /// <returns>キーの道筋なら true。</returns>
    public static bool IsKeyPath(IReadOnlyList<string> path)
    {
        if (path.Count == 0) return false;
        foreach (var segment in path)
        {
            if (segment.Length == 0 || LocaleJson.IsComment(segment)) return false;
        }
        return true;
    }

    /// <summary>
    /// 平たいキーの書き方が「平たいまま」のファイルか（キーの項目がすべて鍵 1 つで、"." を含む鍵がある）。
    /// 新しいキーをこのファイルの書き方に合わせるのに使う（{"menu.start": …} の流儀なら平たいまま足す）。
    /// </summary>
    public bool UsesFlatKeys
    {
        get
        {
            bool anyDotted = false;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!IsKeyEntry(i)) continue;
                var path = _entries[i].Path;
                if (path.Count > 1) return false;
                if (path[0].IndexOf(LocaleJson.KeySeparator) >= 0) anyDotted = true;
            }
            return anyDotted;
        }
    }

    /// <summary>新しいキーの道筋をこのファイルの書き方で作る（平たいままのファイルなら鍵 1 つ、ほかは "." で分ける）。</summary>
    /// <param name="key">平たいキー。</param>
    /// <returns>道筋。</returns>
    public IReadOnlyList<string> PathForNewKey(string key) =>
        UsesFlatKeys ? new[] { key } : LocaleJsonWriter.SplitKey(key);

    // ============================================================
    //  引く
    // ============================================================

    /// <summary>平たいキーのキーの項目の位置を引く（同じキーが 2 つあれば後のもの＝実行中に使われる方）。</summary>
    /// <param name="key">平たいキー。</param>
    /// <returns>位置（無ければ -1）。</returns>
    public int FindKey(string key)
    {
        _keyIndex ??= BuildKeyIndex();
        return _keyIndex.TryGetValue(key, out int index) ? index : -1;
    }

    /// <summary>平たいキー → 位置の表を作り直す（同じキーは後のものが勝つ）。</summary>
    private Dictionary<string, int> BuildKeyIndex()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _entries.Count; i++)
            if (IsKeyEntry(i)) map[_flatKeys[i]] = i;
        return map;
    }

    /// <summary>道筋がちょうど同じ項目の位置を引く。</summary>
    /// <param name="path">道筋。</param>
    /// <returns>位置（無ければ -1）。</returns>
    public int FindPath(IReadOnlyList<string> path)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (CommonPrefixLength(_entries[i].Path, path) == path.Count && _entries[i].Path.Count == path.Count) return i;
        return -1;
    }

    /// <summary>道筋が prefix で始まる項目のうち最後のものの位置を引く（まとまりの末尾に足す位置を決める）。</summary>
    /// <param name="prefix">道筋の頭。</param>
    /// <returns>位置（無ければ -1）。</returns>
    public int LastIndexUnder(IReadOnlyList<string> prefix)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var path = _entries[i].Path;
            if (path.Count > prefix.Count && CommonPrefixLength(path, prefix) == prefix.Count) return i;
        }
        return -1;
    }

    /// <summary>2 つの道筋の頭から同じ鍵が続く数。</summary>
    /// <param name="a">道筋。</param>
    /// <param name="b">道筋。</param>
    /// <returns>同じ鍵の数。</returns>
    public static int CommonPrefixLength(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int limit = Math.Min(a.Count, b.Count);
        int n = 0;
        while (n < limit && string.Equals(a[n], b[n], StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// その道筋に値を置けるかを確かめる（JSON の木として、途中が値・その道筋がまとまり・同じ道筋の項目、ならぶつかる）。
    /// 空のまとまり（{} / []）が道筋の途中かちょうどの位置にあるのはぶつかりにしない（置くときに消す）。
    /// </summary>
    /// <param name="path">置く道筋。</param>
    /// <param name="ignore">確かめから外す項目の位置（改名で動かす項目自身。無ければ null）。</param>
    /// <param name="reason">ぶつかる理由（置けるなら空）。</param>
    /// <returns>置けるなら true。</returns>
    public bool CanPlaceLeaf(IReadOnlyList<string> path, ISet<int>? ignore, out string reason)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (ignore is not null && ignore.Contains(i)) continue;
            var other = _entries[i].Path;
            int common = CommonPrefixLength(path, other);

            // 相手が道筋の途中かちょうどの位置にある（相手の道筋が自分の頭）
            if (common == other.Count)
            {
                if (IsPlaceholder(i)) continue;   // 空のまとまりは置くときに消す
                reason = other.Count == path.Count
                    ? $"「{JoinPath(path)}」は既にあります"
                    : $"「{JoinPath(other)}」は文の入ったキーなので、その下にキーを作れません";
                return false;
            }

            // 相手が自分の下にある（自分の道筋はまとまり）
            if (common == path.Count)
            {
                reason = $"「{JoinPath(path)}」の下にキーがあるので、文を置けません";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    // ============================================================
    //  変える
    // ============================================================

    /// <summary>その位置の項目を置き換える。</summary>
    /// <param name="index">位置。</param>
    /// <param name="entry">新しい項目。</param>
    public void Replace(int index, LocaleJsonWriter.Entry entry)
    {
        _entries[index] = entry;
        _flatKeys[index] = JoinPath(entry.Path);
        _keyIndex = null;
    }

    /// <summary>その位置へ項目を差し込む（位置が数を超えたら末尾）。</summary>
    /// <param name="index">位置。</param>
    /// <param name="entry">項目。</param>
    public void Insert(int index, LocaleJsonWriter.Entry entry)
    {
        int at = Math.Clamp(index, 0, _entries.Count);
        _entries.Insert(at, entry);
        _flatKeys.Insert(at, JoinPath(entry.Path));
        _keyIndex = null;
    }

    /// <summary>末尾へ項目を足す。</summary>
    /// <param name="entry">項目。</param>
    public void Append(LocaleJsonWriter.Entry entry) => Insert(_entries.Count, entry);

    /// <summary>その位置の項目を取り除く。</summary>
    /// <param name="index">位置。</param>
    public void RemoveAt(int index)
    {
        _entries.RemoveAt(index);
        _flatKeys.RemoveAt(index);
        _keyIndex = null;
    }

    /// <summary>
    /// 道筋の途中かちょうどの位置にある空のまとまり（{} / []）を取り除く（その下に値を置く前に呼ぶ）。
    /// </summary>
    /// <param name="path">これから置く道筋。</param>
    /// <returns>取り除いた項目のうち最初のものの位置（無ければ -1。置く位置の目安に使う）。</returns>
    public int RemovePlaceholdersAlong(IReadOnlyList<string> path)
    {
        int first = -1;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var other = _entries[i].Path;
            if (!IsPlaceholder(i) || CommonPrefixLength(path, other) != other.Count) continue;
            RemoveAt(i);
            first = i;
        }
        return first;
    }

    // ============================================================
    //  説明
    // ============================================================

    /// <summary>
    /// キーの説明を集める（キーを包むオブジェクトの "_" で始まる文字列の鍵。近いオブジェクトから順）。
    /// 同じオブジェクトの「別のキー専用の説明」（_greeting は greeting の説明）は外す。
    /// </summary>
    /// <param name="path">キーの道筋。</param>
    /// <returns>説明（どこの鍵か・文）。</returns>
    public IReadOnlyList<LocaleComment> CommentsFor(IReadOnlyList<string> path)
    {
        var result = new List<LocaleComment>();
        for (int depth = path.Count - 1; depth >= 0; depth--)
        {
            // depth 個の頭 = キーを包むオブジェクト（depth = 0 は根）
            string ownChild = path[depth];
            var siblings = ChildNamesUnder(path, depth);
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Kind != LocaleJsonWriter.EntryKind.String) continue;
                if (entry.Path.Count != depth + 1 || CommonPrefixLength(entry.Path, path) < depth) continue;
                string name = entry.Path[depth];
                if (!LocaleJson.IsComment(name)) continue;

                // 「_<別の子>」はその子の説明なので外す（自分の子の名前なら残す）
                string target = name.Substring(LocaleJson.CommentPrefix.Length);
                if (!string.Equals(target, ownChild, StringComparison.Ordinal) && siblings.Contains(target)) continue;

                result.Add(new LocaleComment(JoinPath(entry.Path), entry.Value));
            }
        }
        return result;
    }

    /// <summary>道筋の頭 depth 個のオブジェクトの直下の子の名前（説明の鍵を除く）。</summary>
    private HashSet<string> ChildNamesUnder(IReadOnlyList<string> path, int depth)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (entry.Path.Count <= depth || CommonPrefixLength(entry.Path, path) < depth) continue;
            string name = entry.Path[depth];
            if (!LocaleJson.IsComment(name)) names.Add(name);
        }
        return names;
    }

    // ============================================================
    //  内部
    // ============================================================

    /// <summary>道筋の照合の鍵（鍵ごとに「長さ:中身」をつなぐ。中身に何があっても一意）。</summary>
    private static string PathKey(IReadOnlyList<string> path)
    {
        var builder = new StringBuilder();
        foreach (var segment in path)
        {
            builder.Append(segment.Length.ToString(CultureInfo.InvariantCulture))
                   .Append(PathKeyLengthSeparator)
                   .Append(segment);
        }
        return builder.ToString();
    }
}
