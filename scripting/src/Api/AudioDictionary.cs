namespace SEED;

/// <summary>
/// アクターに付いた音声辞書（AudioDictionaryComponent）へのアクセサ。
///
/// 辞書は「グループ名 / 用途名」（例 <c>Player/attack</c>）というキーで
/// 音声ファイルのパスと既定音量を引ける対応表で、エディタのインスペクタで編集する。
/// 素材を差し替えたいときは辞書の行を直すだけでよく、参照側（スクリプト・AudioComponent）は
/// キーだけを持てばよい。
///
/// このハンドルは<b>その辞書 1 つだけ</b>を引く（名指し）。
/// シーン全体の辞書を横断して引きたいときは <see cref="Audio.PlayDict"/> を使う。
/// </summary>
public readonly struct AudioDictionary : IComponentHandle<AudioDictionary>
{
    /// <summary>この AudioDictionary が属するエンティティ（スロット entity）。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "AudioDictionary";

    // ── フィールド名の接頭辞（Rust 側 host_api.rs の AUDIO_DICT_FIELD_*_PREFIX と一致させる）──
    //
    // 辞書のキー自体が任意の文字列なので、固定フィールド名との衝突を接頭辞で構造的に防ぐ。
    // 例: キー "Player/attack" のパスは field "path:Player/attack" で引く。

    /// <summary>パスを引くときのフィールド名接頭辞。</summary>
    private const string PathFieldPrefix = "path:";

    /// <summary>既定音量を引くときのフィールド名接頭辞。</summary>
    private const string VolumeFieldPrefix = "volume:";

    /// <summary>
    /// 引けなかったときに返す音量（等倍）。
    /// 「鳴らないより等倍で鳴ったほうが原因に気づける」ため 0 ではなく 1 を返す。
    /// </summary>
    private const float FallbackVolume = 1f;

    /// <summary>
    /// <see cref="Play"/> の volume 引数が「未指定（＝辞書の既定音量を使う）」を意味する値。
    /// 負の音量は物理的に意味がないので、追加の引数なしで「省略」を表現できる。
    /// </summary>
    private const float VolumeUnspecified = -1f;

    internal AudioDictionary(Entity entity) { _entity = entity; }

    // ── IComponentHandle 実装（GetComponent 経由でのみ使われる）──
    static string IComponentHandle<AudioDictionary>.ComponentKindName => Comp;
    static AudioDictionary IComponentHandle<AudioDictionary>.FromEntity(Entity slotEntity) => new(slotEntity);

    /// <summary>
    /// この参照が生存しているか（指すエンティティが実在し AudioDictionary を保持しているか）。
    ///
    /// [SerializeField] の参照フィールドで「解決できたか／破棄されていないか」を判定するために使う。
    /// <b>null は「未設定」</b>、<b>IsValid == false は「未解決または破棄済み」</b>を意味する。
    /// </summary>
    public bool IsValid => ScriptHost.HasComponent(_entity, Comp);

    // ── 引き当て ─────────────────────────────────────────────

    /// <summary>
    /// キー（<c>グループ名/用途名</c>）から音声ファイルのパスを引く。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Player/attack"</c>）。</param>
    /// <param name="path">見つかったときの assets:// 仮想パス。見つからなければ空文字列。</param>
    /// <returns>この辞書にそのキーがあり、パスが設定されていれば true。</returns>
    public bool TryGetPath(string key, out string path)
    {
        if (string.IsNullOrEmpty(key)) { path = ""; return false; }
        return ScriptHost.TryGetString(_entity, Comp, PathFieldPrefix + key, out path);
    }

    /// <summary>
    /// キーに対応する行の既定音量を返す（引けないときは 1.0）。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Player/attack"</c>）。</param>
    public float DefaultVolume(string key)
    {
        if (string.IsNullOrEmpty(key)) return FallbackVolume;
        return ScriptHost.TryGetFloat(_entity, Comp, VolumeFieldPrefix + key, out var v)
            ? v : FallbackVolume;
    }

    // ── 再生 ─────────────────────────────────────────────────

    /// <summary>
    /// キーに対応する効果音を<b>この辞書から</b>引いて再生する（多重再生可）。
    ///
    /// 引けないキーは何も鳴らさない（ランタイムが警告を出す <see cref="Audio.PlayDict"/> と異なり、
    /// こちらは「その辞書に無い」だけなので静かに無視する）。
    /// </summary>
    /// <param name="key">辞書のキー（例 <c>"Player/attack"</c>）。</param>
    /// <param name="volume">音量（1.0 = 等倍）。負の値なら辞書の既定音量を使う。</param>
    public void Play(string key, float volume = VolumeUnspecified)
    {
        if (!TryGetPath(key, out var path)) return;
        Audio.Play(path, volume < 0f ? DefaultVolume(key) : volume);
    }
}
