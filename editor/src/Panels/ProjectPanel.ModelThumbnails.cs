using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using SEEDEditor.Assets;

namespace SEEDEditor.Panels;

/// <summary>
/// プロジェクトパネルの「3D モデルのサムネイル」まわり。
///
/// <para>
/// 画像・フォントのサムネイルはエディタ単独で描けるが、3D モデルはそうはいかない。
/// glTF を解釈して PBR で描くにはレンダラが要るので、<b>ランタイム（wgpu）に
/// オフスクリーンで描かせ、PNG をキャッシュしてエディタはそれを表示する</b>。
/// </para>
///
/// <para>表示の手順は 3 段構え:</para>
/// <list type="number">
///   <item><description>キャッシュ PNG があれば即表示する（ランタイムに一切触らない）。</description></item>
///   <item><description>
///     無ければランタイムへ生成を頼み、<c>THUMBNAIL_DONE</c> が返ってからタイルへ貼る。
///   </description></item>
///   <item><description>
///     ランタイムが居ない／Play 中なら何もしない（形式アイコンのまま）。
///   </description></item>
/// </list>
///
/// <para>
/// フォルダを次々に開いたときに、もう画面に無いタイルのためランタイムが描き続けないよう、
/// <see cref="InvalidateModelThumbnailRequests"/> が一覧の作り直しに合わせて要求を忘れる。
/// これは <b>タイルが消えるすべての場所</b>から呼ばれるので、
/// 待ち行列と応答待ち表に入っている要求は常に「今の一覧のもの」になる。
/// </para>
///
/// <para>
/// キャッシュ場所とファイル名の規則は <see cref="ModelThumbnailCacheKey"/> が唯一の持ち主で、
/// ランタイム側（<c>renderer/thumbnail/cache_key.rs</c>）と同じ値を出すことが要件。
/// </para>
/// </summary>
public partial class ProjectPanel
{
    // ── 定数（マジックナンバー禁止）──────────────────────────

    /// <summary>
    /// ランタイムへ同時に投げておく要求の上限。
    ///
    /// <para>
    /// ランタイムは 1 件ずつしか描かない（残りは向こうの待ち行列で待つ）ので、
    /// たくさん投げても速くはならない。少しだけ先読みさせて往復の待ちを隠しつつ、
    /// フォルダを離れたときに無駄になる要求を最小限にする値として 2 を採る。
    /// </para>
    /// </summary>
    private const int ModelThumbnailMaxInFlight = 2;

    /// <summary>IPC の要求で使う引数区切り。パスに含まれていたら要求を諦める。</summary>
    private const char ModelThumbnailArgSeparator = ',';

    /// <summary>モデルサムネイル要求コマンドの接頭辞（ランタイム <c>ipc.rs</c> と対）。</summary>
    private const string ModelThumbnailCommandPrefix = "THUMBNAIL:";

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>
    /// ランタイムへ生成を頼む 1 件ぶんの控え。
    /// </summary>
    /// <param name="Target">サムネイルを貼り付ける先のアイコンコントロール。</param>
    /// <param name="AssetUri">ランタイムへ渡す <c>assets://</c> パス。</param>
    private sealed record ModelThumbnailRequest(Image Target, string AssetUri);

    /// <summary>まだ送っていない要求（送信上限に達している間ここで待つ）。</summary>
    private readonly Queue<ModelThumbnailRequest> _modelThumbWaiting = new();

    /// <summary>送信済みで応答待ちの要求（キーは要求 ID）。</summary>
    private readonly Dictionary<string, ModelThumbnailRequest> _modelThumbInFlight = new();

    /// <summary>要求 ID の採番カウンタ。エディタの生存期間中で一意ならよい。</summary>
    private long _modelThumbNextId;

    /// <summary>
    /// 応答イベントを購読した <see cref="SEEDEditor.Runtime.RuntimeManager"/>。
    ///
    /// <para>
    /// 真偽値ではなく参照を持つのは、<c>SetRuntime</c> で別のインスタンスへ差し替えられた場合に
    /// 「購読済み」と誤判定して新しいランタイムの応答を取りこぼさないため。
    /// </para>
    /// </summary>
    private SEEDEditor.Runtime.RuntimeManager? _modelThumbHookedTo;

    // ── 入口 ──────────────────────────────────────────────────

    /// <summary>
    /// モデルファイルのタイルにサムネイルを出す。
    ///
    /// <para>
    /// キャッシュがあればその場で貼り、無ければランタイムへ要求を積む。
    /// どちらもできない場合は何もしない（形式アイコンが残る）。
    /// </para>
    /// </summary>
    /// <param name="imgCtrl">差し替え先のアイコンコントロール。</param>
    /// <param name="modelPath">モデルファイルの絶対パス。</param>
    private void ScheduleModelThumbnail(Image imgCtrl, string modelPath)
    {
        // アセットルート外のファイル（あり得ないが）は仮想パスを作れないので対象外
        var assetUri = AssetUriPath.ToAssetUri(_assetsRoot, modelPath);
        if (string.IsNullOrEmpty(assetUri)) return;

        var cacheDir = ModelThumbnailCacheKey.CacheDirForAssetsRoot(_assetsRoot);
        var relative = AssetUriPath.ToRelative(_assetsRoot, modelPath);
        var cachePath = ModelThumbnailCacheKey.BuildPathForFile(
            cacheDir, relative, modelPath, ModelThumbnailCacheKey.DefaultSizePx);

        // ── 1. キャッシュヒット: ランタイムに一切触らず表示する ──
        //   生成済み PNG はただの画像なので、画像サムネイルと同じ経路で読める。
        if (cachePath != null && File.Exists(cachePath))
        {
            _ = LoadImagePreviewAsync(imgCtrl, cachePath);
            return;
        }

        // ── 2. キャッシュミス: ランタイムへ頼めるときだけ要求を積む ──
        if (!CanRequestModelThumbnail()) return;

        // 引数はカンマ区切りなので、パスにカンマが入ると復元できない。
        // 黙って壊れた要求を送るより、サムネイルを諦める。
        if (assetUri.IndexOf(ModelThumbnailArgSeparator) >= 0) return;

        HookModelThumbnailEvents();
        _modelThumbWaiting.Enqueue(new ModelThumbnailRequest(imgCtrl, assetUri));
        PumpModelThumbnailQueue();
    }

    /// <summary>
    /// タイルを作り直した（＝それまでのタイルが消えた）ことを伝える。
    /// <b>ファイルグリッドを空にするすべての場所から呼ぶこと。</b>
    ///
    /// <para>
    /// まだ送っていない要求も、応答待ちの要求も忘れる。
    /// 一覧を作り直した時点で、控えが指している <see cref="Image"/> は
    /// もうビジュアルツリーに無い（同じフォルダの再描画でもタイルは作り直される）ので、
    /// 応答が届いても貼る先が無い。
    /// </para>
    ///
    /// <para>
    /// <b>応答待ちを忘れるのは「同時送信の枠を取り戻す」ためでもある。</b>
    /// ランタイムが落ちる・Play へ入るなどで応答が永久に来なくなると、
    /// 枠を占めたままのエントリが残り、以降そのセッションでは一切サムネイルを
    /// 頼めなくなってしまう。忘れておけば、遅れて届いた応答は
    /// <see cref="TakeInFlightModelThumbnail"/> が見つけられず素通りするだけで済む。
    /// </para>
    ///
    /// <para>
    /// 捨てた要求のぶんの描画が無駄になるわけではない。ランタイムは PNG を
    /// キャッシュへ書き終えているので、同じフォルダを開き直せばキャッシュヒットで即表示される。
    /// </para>
    /// </summary>
    private void InvalidateModelThumbnailRequests()
    {
        _modelThumbWaiting.Clear();
        _modelThumbInFlight.Clear();
    }

    // ── ランタイムとのやり取り ────────────────────────────────

    /// <summary>
    /// いまランタイムへサムネイル生成を頼める状態か。
    ///
    /// <para>
    /// Play 中に頼まないのは、撮影がワールド線とカメラを一時的に差し替えるため
    /// （ゲーム画面が一瞬別物になる）。ランタイム側も Play 中は待たせるだけなので、
    /// そもそも送らない。Edit へ戻ってフォルダを開き直せば、また要求が飛ぶ。
    /// </para>
    /// </summary>
    private bool CanRequestModelThumbnail()
    {
        if (_runtime == null) return false;
        if (!_runtime.IsPipeConnected) return false;
        return _runtime.State is SEEDEditor.Runtime.EditorState.Edit;
    }

    /// <summary>ランタイムの応答イベントを購読する（同じインスタンスへは 1 度だけ）。</summary>
    private void HookModelThumbnailEvents()
    {
        if (_runtime == null || ReferenceEquals(_modelThumbHookedTo, _runtime)) return;
        _runtime.ModelThumbnailCompleted += OnModelThumbnailCompleted;
        _runtime.ModelThumbnailFailed    += OnModelThumbnailFailed;
        _modelThumbHookedTo = _runtime;
    }

    /// <summary>
    /// 送信上限に空きがある間、待ち行列から要求を送り出す。
    /// </summary>
    private void PumpModelThumbnailQueue()
    {
        while (_modelThumbInFlight.Count < ModelThumbnailMaxInFlight && _modelThumbWaiting.Count > 0)
        {
            var request = _modelThumbWaiting.Dequeue();

            // 送る直前にもう一度見る（この間に Play へ入っている場合がある）
            if (!CanRequestModelThumbnail())
            {
                // 送れないなら以降も送れない。待ち行列ごと畳む。
                _modelThumbWaiting.Clear();
                return;
            }

            var id = (_modelThumbNextId++).ToString(CultureInfo.InvariantCulture);
            _modelThumbInFlight[id] = request;

            var sizePx = ModelThumbnailCacheKey.DefaultSizePx.ToString(CultureInfo.InvariantCulture);
            _runtime!.SendToRuntime(
                $"{ModelThumbnailCommandPrefix}{id}{ModelThumbnailArgSeparator}" +
                $"{sizePx}{ModelThumbnailArgSeparator}{request.AssetUri}");
        }
    }

    /// <summary>
    /// 生成成功の応答（PNG が書き出された）。
    ///
    /// <para>
    /// イベントはパイプ受信スレッドから来るので、UI へ触る前に必ず Dispatcher へ渡す。
    /// 状態（待ち行列・応答待ち表）の更新も UI スレッドへ寄せ、ロックを持たずに済ませる。
    /// </para>
    /// </summary>
    /// <param name="requestId">要求 ID。</param>
    /// <param name="pngPath">書き出された PNG の絶対パス。</param>
    private void OnModelThumbnailCompleted(string requestId, string pngPath)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 見つからない＝一覧が作り直されて忘れた要求。貼る先が無いので捨てる
            //（PNG はキャッシュに残るので、次に開いたときは即ヒットする）。
            if (!TakeInFlightModelThumbnail(requestId, out var request)) return;

            if (File.Exists(pngPath))
                _ = LoadImagePreviewAsync(request.Target, pngPath);

            PumpModelThumbnailQueue();
        }));
    }

    /// <summary>
    /// 生成失敗の応答。タイルは形式アイコンのまま残す。
    ///
    /// <para>
    /// 失敗はユーザーの操作ミスではない（読めないモデル・Play 中など）ので、
    /// ダイアログは出さない。理由は <c>RuntimeManager</c> 側でログに残している。
    /// </para>
    /// </summary>
    /// <param name="requestId">要求 ID。</param>
    /// <param name="reason">失敗理由（表示には使わない）。</param>
    private void OnModelThumbnailFailed(string requestId, string reason)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TakeInFlightModelThumbnail(requestId, out _);
            PumpModelThumbnailQueue();
        }));
    }

    /// <summary>
    /// 応答待ち表から 1 件取り出す。見つからなければ <c>false</c>
    /// （一覧を作り直して忘れたあとに応答が届いた場合など）。
    /// 要求 ID は増える一方で再利用しないので、取り違えは起きない。
    /// </summary>
    /// <param name="requestId">要求 ID。</param>
    /// <param name="request">見つかった控え。</param>
    private bool TakeInFlightModelThumbnail(string requestId, out ModelThumbnailRequest request)
    {
        if (_modelThumbInFlight.Remove(requestId, out var found))
        {
            request = found;
            return true;
        }
        request = null!;
        return false;
    }
}
