using System;
using System.Collections.Generic;
using System.Globalization;

namespace SEED.Localization;

// ============================================================
//  LocaleCatalog.cs — 多言語の表の本体（言語の一覧・言語の表・今の言語・探す順・欠けの方針。純粋な計算）
//
//  【役割】L10n（静的な窓口）の中身。読み込み元（ILocaleSource）・保存先（ILocaleStore）・警告の出し先・端末の言語を
//  外から受け取るので、エンジン（Assets・SaveData・Debug）無しで検査できる（editor/tests/LocalizationTests）。
//  【読み込み】最初に使われたとき（EnsureLoaded）に 1 回: index.json → 言語を決める → 探す順の言語の表を読む。
//  【言語の決まり方】保存した値（SaveData の "l10n.language"）→ 端末の言語（SystemLanguage）→ index の default。
//  それぞれ LocaleIndex.Match で一覧の言語へ当てる（"en-US" → "en"）。当たらなければ次へ。
//  【引き方】探す順（今の言語 → fallback の連鎖 → 既定の言語）の表を先から見て、最初に見つかった文を差し込む。
//  どこにも無ければ欠けの方針（MissingPolicy）どおりの文を返し、キーごとに 1 回だけ警告する
//  （言語を切り替える・読み直すと数え直す）。
//  【書き換えの検知】PollChanges が index.json と読んだ表の更新の印（ILocaleSource.GetModifiedTime）を読んだときと比べ、
//  違えば読み直す（今の言語を保つ）。
//  スレッド: ゲームのスレッド（スクリプトのライフサイクル）だけから使う（ロックしない）。
// ============================================================

/// <summary>多言語の表の本体。</summary>
public sealed class LocaleCatalog
{
    /// <summary>選んだ言語を保存するキー（SEED.SaveData のキー）。</summary>
    public const string LanguageSaveKey = "l10n.language";

    /// <summary>複数形の数を差し込む名前（"{n} coins"）。</summary>
    public const string PluralCountName = "n";

    /// <summary>探す順を警告に書くときの区切り。</summary>
    private const string ChainSeparator = " → ";

    /// <summary>読み込んだ言語の表 1 つ（表・読んだときの更新の印・パス）。</summary>
    private sealed record LoadedTable(LocaleTable Table, long Stamp, string Path);

    /// <summary>データファイルの読み込み元。</summary>
    private readonly ILocaleSource _source;
    /// <summary>選んだ言語の保存先。</summary>
    private readonly ILocaleStore _store;
    /// <summary>警告の出し先。</summary>
    private readonly Action<string> _warn;
    /// <summary>端末の言語を問い合わせる口（null を返してよい）。</summary>
    private readonly Func<string?> _systemLanguage;

    /// <summary>言語のコード → 読み込んだ表（探す順の言語だけ）。</summary>
    private readonly Dictionary<string, LoadedTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>欠けていたキー（警告を 1 回だけにするため・欠けの一覧）。</summary>
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

    /// <summary>置き場。</summary>
    private string _root = LocalePaths.DefaultRoot;
    /// <summary>言語の一覧。</summary>
    private LocaleIndex _index = LocaleIndex.Empty;
    /// <summary>index.json を読んだときの更新の印。</summary>
    private long _indexStamp;
    /// <summary>今の言語のコード（言語が 0 件なら空）。</summary>
    private string _language = string.Empty;
    /// <summary>探す順。</summary>
    private IReadOnlyList<string> _chain = Array.Empty<string>();
    /// <summary>今の言語の書式の文化。</summary>
    private CultureInfo _culture = CultureInfo.InvariantCulture;
    /// <summary>読み込んだか。</summary>
    private bool _loaded;

    /// <summary>本体を作る（まだ読まない。最初に使われたときに読む）。</summary>
    /// <param name="source">データファイルの読み込み元。</param>
    /// <param name="store">選んだ言語の保存先。</param>
    /// <param name="warn">警告の出し先（null なら捨てる）。</param>
    /// <param name="systemLanguage">端末の言語を問い合わせる口（null なら「わからない」）。</param>
    public LocaleCatalog(ILocaleSource source, ILocaleStore store, Action<string>? warn = null, Func<string?>? systemLanguage = null)
    {
        _source = source;
        _store = store;
        _warn = warn ?? (_ => { });
        _systemLanguage = systemLanguage ?? (() => null);
    }

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>どの言語の表にも無いキーを引いたときに返す文の方針。</summary>
    public MissingKeyPolicy MissingPolicy { get; set; } = MissingKeyPolicy.Marked;

    /// <summary>置き場（"assets://locale"）。</summary>
    public string Root => _root;

    /// <summary>読み込んだか。</summary>
    public bool IsLoaded => _loaded;

    /// <summary>今の言語のコード（言語が 0 件なら空）。</summary>
    public string Language
    {
        get
        {
            EnsureLoaded();
            return _language;
        }
    }

    /// <summary>言語の一覧。</summary>
    public LocaleIndex Index
    {
        get
        {
            EnsureLoaded();
            return _index;
        }
    }

    /// <summary>言語（index.json の順）。</summary>
    public IReadOnlyList<LocaleLanguage> Languages => Index.Languages;

    /// <summary>探す順（今の言語 → fallback の連鎖 → 既定の言語）。</summary>
    public IReadOnlyList<string> FallbackChain
    {
        get
        {
            EnsureLoaded();
            return _chain;
        }
    }

    /// <summary>今の言語の書式の文化（Invariant の環境では不変文化）。</summary>
    public CultureInfo Culture
    {
        get
        {
            EnsureLoaded();
            return _culture;
        }
    }

    /// <summary>欠けていたキー（今の言語にしてから・読み直してから）。</summary>
    public IReadOnlyCollection<string> MissingKeys => _missing;

    /// <summary>言語を保存していない（端末の言語に従っている）か。</summary>
    public bool IsFollowingSystem => _store.GetString(LanguageSaveKey) is null;

    /// <summary>端末の言語（問い合わせの口の答え。わからなければ null）。</summary>
    public string? SystemLanguage => _systemLanguage();

    // ============================================================
    //  読み込み・置き場・切り替え
    // ============================================================

    /// <summary>
    /// 置き場を変える。読み込み済みで置き場が変わったら、すぐ読み直す（今の言語を保つ）。
    /// </summary>
    /// <param name="root">置き場（"assets://mygame/locale"。空なら既定の置き場）。</param>
    /// <returns>読み直したら true（L10n はこのとき Changed を知らせる）。</returns>
    public bool Configure(string? root)
    {
        string normalized = LocalePaths.NormalizeRoot(root);
        bool same = string.Equals(normalized, _root, StringComparison.Ordinal);
        _root = normalized;
        if (!_loaded || same) return false;
        Reload();
        return true;
    }

    /// <summary>まだなら読み込む。</summary>
    public void EnsureLoaded()
    {
        if (!_loaded) Load(keepLanguage: null);
    }

    /// <summary>index.json と探す順の表を読み直す（今の言語を保つ。一覧から消えていたら決め直す）。</summary>
    public void Reload() => Load(keepLanguage: _loaded ? _language : null);

    /// <summary>
    /// 言語を切り替える（表を読み替える）。<paramref name="save"/> なら保存先へ保存する（同じ値なら書かない）。
    /// </summary>
    /// <param name="code">言語のコード（"en"・"en-US" など。一覧の言語へ当てる）。</param>
    /// <param name="save">保存するか。</param>
    /// <returns>結果（一覧に無い・同じ・切り替えた）。</returns>
    public LocaleSwitchResult SetLanguage(string? code, bool save = true)
    {
        EnsureLoaded();
        string? match = _index.Match(code);
        if (match is null)
        {
            _warn($"一覧に無い言語です: {code}（{LocalePaths.IndexPath(_root)}）");
            return LocaleSwitchResult.Unknown;
        }
        if (save && !string.Equals(_store.GetString(LanguageSaveKey), match, StringComparison.Ordinal))
            _store.SetString(LanguageSaveKey, match);
        if (string.Equals(match, _language, StringComparison.OrdinalIgnoreCase)) return LocaleSwitchResult.Unchanged;
        Activate(match);
        return LocaleSwitchResult.Changed;
    }

    /// <summary>
    /// 保存した言語を消して、端末の言語（当たらなければ既定の言語）へ戻す（言語を選ぶ画面の「端末に合わせる」）。
    /// </summary>
    /// <returns>結果（同じ・切り替えた）。</returns>
    public LocaleSwitchResult FollowSystemLanguage()
    {
        EnsureLoaded();
        if (_store.GetString(LanguageSaveKey) is not null) _store.Delete(LanguageSaveKey);
        string target = _index.Match(_systemLanguage()) ?? _index.DefaultCode;
        if (string.Equals(target, _language, StringComparison.OrdinalIgnoreCase)) return LocaleSwitchResult.Unchanged;
        Activate(target);
        return LocaleSwitchResult.Changed;
    }

    /// <summary>
    /// index.json と読んだ表の更新の印を読んだときと比べ、違えば読み直す（今の言語を保つ）。
    /// まだ読んでいなければ何もしない。印が取れない（0。PAK 同梱）ファイルは変わらない扱い。
    /// </summary>
    /// <returns>読み直したら true。</returns>
    public bool PollChanges()
    {
        if (!_loaded) return false;
        bool changed = _source.GetModifiedTime(LocalePaths.IndexPath(_root)) != _indexStamp;
        if (!changed)
        {
            foreach (var loaded in _tables.Values)
            {
                if (_source.GetModifiedTime(loaded.Path) == loaded.Stamp) continue;
                changed = true;
                break;
            }
        }
        if (!changed) return false;
        Reload();
        return true;
    }

    /// <summary>読み込む（表と欠けの記録を捨てて index.json から。言語は保つ言語 → 決まり方の順）。</summary>
    private void Load(string? keepLanguage)
    {
        _tables.Clear();
        LoadIndex();
        string? kept = keepLanguage is null ? null : _index.Match(keepLanguage);
        Activate(kept ?? ResolveStartupLanguage());
        _loaded = true;
    }

    /// <summary>index.json を読む（読めない・壊れていれば言語 0 件で警告）。</summary>
    private void LoadIndex()
    {
        string path = LocalePaths.IndexPath(_root);
        // 印を先に取る（取ってから読むまでの間に書き換わっても、次の PollChanges で違いが出て読み直せる）
        _indexStamp = _source.GetModifiedTime(path);
        if (!_source.TryReadText(path, out var json))
        {
            _index = LocaleIndex.Empty;
            _warn($"言語の一覧を読めません: {path}（多言語の表なし。キーは欠けの方針どおりの文になります）");
            return;
        }
        _index = LocaleIndex.Parse(json, path);
        if (!_index.IsValid) _warn($"言語の一覧の JSON が壊れています: {path}: {_index.Error}");
        foreach (var warning in _index.Warnings) _warn(warning);
        if (_index.IsValid && _index.IsEmpty) _warn($"言語の一覧に言語がありません: {path}");
    }

    /// <summary>起動のときの言語を決める（保存した値 → 端末の言語 → 既定の言語）。</summary>
    private string ResolveStartupLanguage()
    {
        string? saved = _store.GetString(LanguageSaveKey);
        if (saved is not null)
        {
            if (_index.Match(saved) is { } fromSaved) return fromSaved;
            _warn($"保存した言語「{saved}」は一覧にありません（端末の言語か既定の言語で始めます）");
        }
        if (_index.Match(_systemLanguage()) is { } fromSystem) return fromSystem;
        return _index.DefaultCode;
    }

    /// <summary>言語を今の言語にする（探す順を作り、その表を読み、書式の文化を引く。欠けの記録は数え直す）。</summary>
    private void Activate(string code)
    {
        _language = code;
        _chain = _index.BuildChain(code);
        foreach (var chainCode in _chain) EnsureTable(chainCode);
        _culture = LocaleCulture.Resolve(_index.Find(code)?.CultureName);
        _missing.Clear();
    }

    /// <summary>言語の表をまだ読んでいなければ読む（読めない・壊れていれば空の表で警告）。</summary>
    private void EnsureTable(string code)
    {
        if (_tables.ContainsKey(code)) return;
        string path = LocalePaths.TablePath(_root, code);
        long stamp = _source.GetModifiedTime(path);
        if (!_source.TryReadText(path, out var json))
        {
            _warn($"言語の表を読めません: {path}（この言語のキーは次の言語から探します）");
            _tables[code] = new LoadedTable(LocaleTable.Empty, stamp, path);
            return;
        }
        var table = LocaleTable.Parse(json, path);
        if (!table.IsValid) _warn($"言語の表の JSON が壊れています: {path}: {table.Error}");
        foreach (var warning in table.Warnings) _warn(warning);
        _tables[code] = new LoadedTable(table, stamp, path);
    }

    // ============================================================
    //  引き方
    // ============================================================

    /// <summary>キーが探す順のどれかの表にあるか（警告しない）。</summary>
    /// <param name="key">キー。</param>
    /// <returns>あれば true。</returns>
    public bool Has(string? key)
    {
        EnsureLoaded();
        return TryFindTemplate(key, out _);
    }

    /// <summary>キーの文を引く（差し込みの値は無し。無ければ false で警告しない）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="text">文（無ければ空）。</param>
    /// <returns>あれば true。</returns>
    public bool TryGet(string? key, out string text)
    {
        EnsureLoaded();
        if (TryFindTemplate(key, out var template))
        {
            text = LocaleFormatter.Format(template, ReadOnlySpan<(string Name, object? Value)>.Empty, _culture);
            return true;
        }
        text = string.Empty;
        return false;
    }

    /// <summary>キーの文を引いて差し込む（無ければ欠けの方針どおりの文。キーごとに 1 回だけ警告）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="args">差し込みの名前と値。</param>
    /// <returns>文。</returns>
    public string Get(string? key, ReadOnlySpan<(string Name, object? Value)> args)
    {
        EnsureLoaded();
        return TryFindTemplate(key, out var template)
            ? LocaleFormatter.Format(template, args, _culture)
            : Missing(key);
    }

    /// <summary>
    /// 複数形の文を引いて差し込む（数は {n}。探す順の言語ごとに、その言語の規則で形を決める）。
    /// 無ければ欠けの方針どおりの文。
    /// </summary>
    /// <param name="key">複数形のまとまりの名前（"coins"）。</param>
    /// <param name="count">数。</param>
    /// <param name="args">ほかの差し込みの名前と値（"n" を渡すとそちらが勝つ）。</param>
    /// <returns>文。</returns>
    public string Plural(string? key, long count, ReadOnlySpan<(string Name, object? Value)> args)
    {
        EnsureLoaded();
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var code in _chain)
            {
                if (!_tables.TryGetValue(code, out var loaded)) continue;
                if (loaded.Table.TryGetPlural(key, count, PluralRules.Select(code, count), out var template))
                    return LocaleFormatter.Format(template, WithCount(args, count), _culture);
            }
        }
        return Missing(key);
    }

    /// <summary>区切りつきの数（今の言語の文化で）。</summary>
    /// <param name="value">数。</param>
    /// <param name="digits">小数の桁。</param>
    /// <returns>書いた数。</returns>
    public string FormatNumber(double value, int digits) => LocaleCulture.FormatNumber(value, digits, Culture);

    /// <summary>日付（今の言語の文化で）。</summary>
    /// <param name="date">日付。</param>
    /// <param name="pattern">.NET の日付の書式（空なら短い日付）。</param>
    /// <returns>書いた日付。</returns>
    public string FormatDate(DateTime date, string? pattern) => LocaleCulture.FormatDate(date, pattern, Culture);

    /// <summary>時刻（今の言語の文化で）。</summary>
    /// <param name="time">時刻。</param>
    /// <param name="pattern">.NET の時刻の書式（空なら短い時刻）。</param>
    /// <returns>書いた時刻。</returns>
    public string FormatTime(TimeOnly time, string? pattern) => LocaleCulture.FormatTime(time, pattern, Culture);

    /// <summary>探す順の表を先から見て、最初に見つかった文（差し込み前）を返す。</summary>
    private bool TryFindTemplate(string? key, out string template)
    {
        if (!string.IsNullOrEmpty(key))
        {
            foreach (var code in _chain)
            {
                if (_tables.TryGetValue(code, out var loaded) && loaded.Table.TryGetText(key, out template)) return true;
            }
        }
        template = string.Empty;
        return false;
    }

    /// <summary>欠けたキーの文（方針どおり。キーごとに 1 回だけ警告して記録する）。</summary>
    private string Missing(string? key)
    {
        string safeKey = key ?? string.Empty;
        if (_missing.Add(safeKey))
        {
            string chain = _chain.Count == 0 ? "（言語なし）" : string.Join(ChainSeparator, _chain);
            _warn($"キー「{safeKey}」がどの言語の表にもありません（探した順: {chain}）");
        }
        return MissingKeyText.Render(MissingPolicy, safeKey);
    }

    /// <summary>差し込みの値の後ろに数（{n}）を足した並び（利用者の "n" が先にあればそちらが勝つ）。</summary>
    private static (string Name, object? Value)[] WithCount(ReadOnlySpan<(string Name, object? Value)> args, long count)
    {
        var all = new (string Name, object? Value)[args.Length + 1];
        args.CopyTo(all);
        all[args.Length] = (PluralCountName, count);
        return all;
    }
}
