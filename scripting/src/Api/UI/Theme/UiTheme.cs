using System;
using System.IO;

namespace SEED.UI;

// ============================================================
//  UiTheme.cs — 今のテーマ（部品が見た目を決めるときに読む。W2-4。W2-9 で読み込み・継承・明暗・切り替えを本格化）
//
//  【使い方】（docs/ui_theme.md §7）
//      var forest = UiTheme.Load("assets://ui/themes/forest.json");   // 継承を解いたテーマ（読めなければ null・警告はログ）
//      UiTheme.Apply(forest);                                        // すぐ切り替える（animate: true で色を補間）
//      UiTheme.SetBrightnessMode(UiBrightnessMode.System);           // 端末の明暗に従う（Light / Dark で強制）
//      UiTheme.Changed += change => …;                               // 切り替えのたびに 1 回
//  【切り替えの当て直し】Apply・明暗の変化で、登録簿（UiRegistry）の表示中の全部品の見た目（ApplyLook）をその場で当て直す
//  （同じフレームで全部品が変わる。部品を持たない見た目は ThemeStyle を付けるか Changed で当てる）。色の補間の間は毎フレーム
//  途中の表で当て直す（ScriptBridge の BeginFrame から TickFrame。描画を止める設定でも補間の間は描き続ける）。
//  【既定】最初は組み込みの既定のテーマ（Theme/default_theme.json。SEEDScripting に埋め込み。暗い方＋light の節）・
//  明暗の選び方は Theme（テーマの明暗のまま）。スクリプトを読み直すと（コンパイル・ホットリロード・事前コンパイル DLL の読み込み）
//  既定へ戻る（ResetForReload）。エディタに埋め込んだ Play の開始・停止ではスクリプトを読み直さないので、前の Play の最後の
//  テーマ・選び方が残る（SEEDScripting の静的な状態。アプリは起動のスクリプトの OnStart でテーマを当てる。docs/backlog.md）。
// ============================================================

/// <summary>今のテーマ。</summary>
public static class UiTheme
{
    /// <summary>既定のテーマの埋め込みの名前（SEEDScripting.csproj の LogicalName と一致）。</summary>
    internal const string DefaultResourceName = "SEED.UI.default_theme.json";

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[SEED.UI]";

    /// <summary>JSON から作ったテーマの既定の出どころ（警告の頭）。</summary>
    private const string InlineOrigin = "(json)";

    /// <summary>組み込みの既定のテーマの JSON（読み込み済み）。</summary>
    private static UiThemeSource? _builtInSource;
    /// <summary>組み込みの既定のテーマ。</summary>
    private static UiThemeDefinition? _builtIn;
    /// <summary>当てているテーマ（null の間は組み込みの既定のテーマ）。</summary>
    private static UiThemeDefinition? _definition;
    /// <summary>当てているテーマを今の明暗で解いた表（null = まだ作っていない）。</summary>
    private static UiThemeData? _target;
    /// <summary>色の補間の途中の表（補間していなければ null）。</summary>
    private static UiThemeData? _blended;
    /// <summary>色の補間（していなければ null）。</summary>
    private static UiThemeTransition? _transition;
    /// <summary>明暗の選び方。</summary>
    private static UiBrightnessMode _mode = UiBrightnessMode.Theme;
    /// <summary>「テーマがその明暗に対応していない」を警告したテーマと選び方（同じ組では 1 度だけ）。</summary>
    private static (UiThemeDefinition?, UiBrightnessMode) _warnedUnsupported;

    /// <summary>見た目が変わるたびに増える番号（切り替え・明暗の変化・色の補間の毎フレーム。部品が見た目を作り直す合図）。</summary>
    public static int Version { get; private set; }

    /// <summary>組み込みの既定のテーマ（SEEDScripting に埋め込んだ default_theme.json。暗い方＋明るい方）。</summary>
    public static UiThemeDefinition BuiltIn => _builtIn ??= LoadBuiltIn();

    /// <summary>当てているテーマ。</summary>
    public static UiThemeDefinition Definition => _definition ?? BuiltIn;

    /// <summary>今の値の表（部品が読む。色の補間の間は途中の表）。</summary>
    public static UiThemeData Current => _blended ?? Target;

    /// <summary>組み込みの既定のテーマの、今の明暗の表（JSON を直接重ねるときの代わりのテーマ）。</summary>
    public static UiThemeData Default => BuiltIn.Resolve(Brightness);

    /// <summary>表示している明暗。</summary>
    public static UiBrightness Brightness => Target.Brightness;

    /// <summary>明暗の選び方（既定は <see cref="UiBrightnessMode.Theme"/>）。</summary>
    public static UiBrightnessMode BrightnessMode => _mode;

    /// <summary>端末の明暗（選び方が System のときに問い合わせた値。取れない・まだ問い合わせていなければ null）。</summary>
    public static UiBrightness? SystemBrightness => UiSystemBrightness.Known;

    /// <summary>色を補間している途中か。</summary>
    public static bool IsTransitioning => _transition is { IsDone: false };

    /// <summary>端末の明暗の変化（System の選び方）で切り替えるとき、色を補間するか（既定 false = すぐ）。</summary>
    public static bool AnimateSystemChanges { get; set; }

    /// <summary>
    /// テーマ・明暗が切り替わったとき（切り替えごとに 1 回。色の補間の途中のフレームでは呼ばない）。
    /// 足したスクリプトは OnDestroy で外す（エディタに埋め込んだ Play の停止では外れない）。スクリプトを読み直すと全部外れる。
    /// 受け手の例外はログに残して残りの受け手を呼ぶ。
    /// </summary>
    public static event Action<UiThemeChange>? Changed;

    /// <summary>今のテーマの色のトークン。</summary>
    public static Color Color(string token) => Current.Color(token);

    /// <summary>今のテーマの数のトークン。</summary>
    public static float Number(string token, float fallback = 0f) => Current.Number(token, fallback);

    /// <summary>今のテーマの文字列のトークン。</summary>
    public static string Text(string token, string fallback = "") => Current.Text(token, fallback);

    /// <summary>当てているテーマを今の明暗で解いた表。</summary>
    private static UiThemeData Target => _target ??= Definition.Resolve(ComputeBrightness(Definition));

    /// <summary>
    /// アセットのテーマの JSON を読み、継承（extends）を解く（当てない）。知らない名前・型の誤り・読めない基のテーマは
    /// 警告をログへ出して既定の値を使う。
    /// </summary>
    /// <param name="assetPath">assets:// のパス。</param>
    /// <returns>テーマ（ファイルを読めない・JSON が壊れていれば null）。</returns>
    public static UiThemeDefinition? Load(string assetPath)
    {
        if (!Assets.TryReadText(assetPath, out var json))
        {
            Debug.LogWarning($"{LogPrefix} テーマを読めません: {assetPath}");
            return null;
        }
        return FromJson(json, assetPath);
    }

    /// <summary>
    /// JSON からテーマを作る（当てない。extends の相対パスは <paramref name="origin"/> から数える）。種の色と明暗だけのテーマを
    /// データから作るとき（Wake or Pay の themes.json の seedColor など）にも使う。
    /// </summary>
    /// <param name="json">テーマの JSON。</param>
    /// <param name="origin">出どころ（assets:// のパスなら extends の相対パスの起点。警告の頭）。</param>
    /// <returns>テーマ（JSON が壊れていれば null）。</returns>
    public static UiThemeDefinition? FromJson(string json, string origin = InlineOrigin)
    {
        var leaf = UiThemeSource.Parse(json, origin);
        if (!leaf.IsValid)
        {
            Debug.LogWarning($"{LogPrefix} テーマの JSON が壊れています: {origin}: {leaf.Error}");
            return null;
        }
        var theme = UiThemeResolver.Build(leaf, BuiltInSource, ReadAsset);
        foreach (var warning in theme.Warnings) Debug.LogWarning($"{LogPrefix} {warning}");
        return theme;
    }

    /// <summary>
    /// テーマを当てる（null で組み込みの既定のテーマ）。表示中の全部品の見た目をその場で当て直し、<see cref="Changed"/> を呼ぶ。
    /// </summary>
    /// <param name="theme">当てるテーマ。</param>
    /// <param name="animate">true なら色を行き先のテーマの motion.theme 秒・motion.theme_curve で補間する（既定はすぐ）。</param>
    public static void Apply(UiThemeDefinition? theme, bool animate = false)
    {
        _definition = theme;
        Retarget(animate, force: true);
    }

    /// <summary>
    /// アセットのテーマを読んで当てる（<see cref="Load"/> ＋ <see cref="Apply"/>）。
    /// </summary>
    /// <returns>読めて当てたら true（読めなければ今のテーマのまま）。</returns>
    public static bool LoadAsset(string assetPath, bool animate = false)
    {
        var theme = Load(assetPath);
        if (theme is null) return false;
        Apply(theme, animate);
        return true;
    }

    /// <summary>
    /// 明暗の選び方を変える（System は端末の明暗を問い合わせ、変化のイベントを受ける）。表示する明暗が変わったら当て直す。
    /// </summary>
    /// <param name="mode">選び方。</param>
    /// <param name="animate">true なら色を補間する。</param>
    public static void SetBrightnessMode(UiBrightnessMode mode, bool animate = false)
    {
        _mode = mode;
        if (mode == UiBrightnessMode.System) UiSystemBrightness.Observe();
        Retarget(animate, force: false);
    }

    /// <summary>端末の明暗が変わった（UiSystemBrightness から。選び方が System のときだけ当て直す）。</summary>
    internal static void OnSystemBrightnessChanged()
    {
        if (_mode == UiBrightnessMode.System) Retarget(AnimateSystemChanges, force: false);
    }

    /// <summary>
    /// フレームに 1 回（ScriptBridge の BeginFrame から。スクリプトの Update より前）: 色の補間を進めて全部品へ当て直す。
    /// </summary>
    /// <param name="unscaledDeltaTime">前のフレームからの実時間（秒。時間の倍率に依らない）。</param>
    internal static void TickFrame(float unscaledDeltaTime)
    {
        if (_transition is null) return;
        _blended = _transition.Step(unscaledDeltaTime);
        if (_transition.IsDone)
        {
            _transition = null;
            _blended = null;
        }
        Version++;
        ReapplyAll();
        Redraw.Request();
    }

    /// <summary>
    /// スクリプトを読み直す前（コンパイル・ホットリロード・事前コンパイル DLL の読み込み。ScriptBridge から）: 組み込みの既定の
    /// テーマ・Theme の選び方へ戻し、<see cref="Changed"/> の受け手を外す（古いアセンブリのメソッドを掴んだままにしない）。
    /// エディタに埋め込んだ Play の開始・停止では呼ばれない（スクリプトを読み直さないため）。
    /// </summary>
    internal static void ResetForReload()
    {
        _definition = null;
        _target = null;
        _blended = null;
        _transition = null;
        _mode = UiBrightnessMode.Theme;
        _warnedUnsupported = default;
        AnimateSystemChanges = false;
        Changed = null;
        UiSystemBrightness.Reset();
        Version++;
    }

    /// <summary>
    /// 表示する表を作り直して当て直す。
    /// </summary>
    /// <param name="animate">色を補間するか。</param>
    /// <param name="force">表が同じでも当て直して知らせるか（Apply は同じテーマでも当て直す）。</param>
    private static void Retarget(bool animate, bool force)
    {
        var theme = Definition;
        var target = theme.Resolve(ComputeBrightness(theme));
        if (!force && ReferenceEquals(target, _target)) return;
        var from = Current;
        _target = target;
        float duration = animate ? target.Number(UiTokens.MotionTheme) : 0f;
        if (duration > 0f && !ReferenceEquals(from, target))
        {
            _transition = new UiThemeTransition(from, target, duration, UiCurve.FromTheme(target, UiTokens.MotionThemeCurve, UiCurve.FastOutSlowIn));
            _blended = _transition.Current;
        }
        else
        {
            _transition = null;
            _blended = null;
        }
        Version++;
        ReapplyAll();
        Redraw.Request();
        Debug.Log($"{LogPrefix} テーマ: {theme.Name}（{UiBrightnessRules.ToWord(target.Brightness)}・選び方 {_mode}{(animate ? $"・{duration:0.##} 秒で補間" : string.Empty)}）");
        RaiseChanged(new UiThemeChange(theme, target.Brightness, _transition is not null));
    }

    /// <summary>
    /// <see cref="Changed"/> を受け手ごとに呼ぶ（1 つの受け手の例外で残りを止めない。外し忘れた破棄済みのスクリプトの受け手が
    /// 例外を出しても、テーマの切り替えそのものは済んでいる）。
    /// </summary>
    private static void RaiseChanged(UiThemeChange change)
    {
        if (Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<UiThemeChange>)handler)(change);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} UiTheme.Changed の受け手で例外: {ex}");
            }
        }
    }

    /// <summary>テーマと選び方から表示する明暗を決める（対応していない明暗を求めたら 1 度だけ警告）。</summary>
    private static UiBrightness ComputeBrightness(UiThemeDefinition theme)
    {
        UiBrightness? system = _mode == UiBrightnessMode.System ? UiSystemBrightness.Known : null;
        var desired = UiBrightnessRules.Desired(_mode, theme.Brightness, system);
        var resolved = UiBrightnessRules.Resolve(_mode, theme.Brightness, theme.SupportsLight, theme.SupportsDark, system);
        if (desired != resolved && _warnedUnsupported != (theme, _mode))
        {
            _warnedUnsupported = (theme, _mode);
            Debug.LogWarning($"{LogPrefix} テーマ {theme.Name} は {UiBrightnessRules.ToWord(desired)} に対応していません"
                             + $"（{UiBrightnessRules.ToWord(theme.Brightness)} のまま表示します。対応させるならテーマに \"{UiBrightnessRules.ToWord(desired)}\" の節を書きます）");
        }
        return resolved;
    }

    /// <summary>表示中の全部品の見た目を当て直す（登録簿）。</summary>
    private static void ReapplyAll()
    {
        foreach (var widget in UiRegistry.Snapshot()) widget.ReapplyTheme();
    }

    /// <summary>組み込みの既定のテーマの JSON。</summary>
    private static UiThemeSource BuiltInSource => _builtInSource ??= ReadBuiltInSource();

    /// <summary>組み込みの既定のテーマを作る。</summary>
    private static UiThemeDefinition LoadBuiltIn()
    {
        var source = BuiltInSource;
        var theme = UiThemeResolver.Build(source, source, ReadAsset);
        foreach (var warning in theme.Warnings) Console.Error.WriteLine($"{LogPrefix} {warning}");
        return theme;
    }

    /// <summary>アセットの文字を読む（読めなければ null。継承の基のテーマ）。</summary>
    private static string? ReadAsset(string path) => Assets.TryReadText(path, out var text) ? text : null;

    /// <summary>埋め込みの既定のテーマを読む（読めなければ空＝部品の最後の既定値）。</summary>
    private static UiThemeSource ReadBuiltInSource()
    {
        string json = "{}";
        try
        {
            using var stream = typeof(UiTheme).Assembly.GetManifestResourceStream(DefaultResourceName);
            if (stream is null)
            {
                Console.Error.WriteLine($"{LogPrefix} 既定のテーマが埋め込まれていません: {DefaultResourceName}");
            }
            else
            {
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{LogPrefix} 既定のテーマを読めません: {ex.Message}");
        }
        var source = UiThemeSource.Parse(json, UiThemePaths.BuiltInOrigin);
        if (!source.IsValid) Console.Error.WriteLine($"{LogPrefix} 既定のテーマの JSON が壊れています: {source.Error}");
        return source;
    }
}
