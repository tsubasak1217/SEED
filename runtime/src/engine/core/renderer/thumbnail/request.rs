// ============================================================
//  request.rs — モデルサムネイル要求の書式・検証・待ち行列
// ------------------------------------------------------------
//  役割:
//    エディタとの通信プロトコル（要求 1 行・応答 1 行）と、
//    受け取った要求を溜めておく待ち行列。どちらも GPU に触れない純粋ロジック。
//
//  プロトコル:
//    要求  THUMBNAIL:{要求ID},{一辺px},{assets:// パス}
//    成功  THUMBNAIL_DONE:{要求ID},{書き出した PNG の絶対パス}
//    失敗  THUMBNAIL_FAILED:{要求ID},{理由}
//
//  なぜ要求 ID を付けるのか:
//    プロジェクトパネルは複数のタイルを並べて表示するため、要求と応答が
//    1 対 1 で往復するとは限らない（先に投げたものが後から返る）。
//    ID を添えれば、エディタは届いた PNG をどのタイルへ貼るか迷わずに済む。
//
//  既定サイズの所有者:
//    一辺のピクセル数を**選ぶ**のはエディタ（プロジェクトパネル）で、
//    ランタイムは受け取った値を検証するだけ。既定値をここにも置くと
//    「どちらが本物か」が曖昧になり、片方だけ変えたときに静かにずれる。
//    ランタイムが持つのは上下限（MIN_SIZE_PX / MAX_SIZE_PX）だけにする。
//
//  なぜ待ち行列なのか:
//    サムネイル 1 枚の生成は複数フレームにまたがる（読み込み → 構図決め → 撮影）。
//    フォルダを開いた瞬間に数十個の要求が一度に届くので、そのまま並行実行すると
//    ランタイムが GPU 資源を使い切る。1 件ずつ順番に処理し、残りはここで待たせる。
// ============================================================

use std::collections::VecDeque;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// 要求コマンドの接頭辞（`ipc.rs` の定数と対になる）。
pub const REQUEST_PREFIX: &str = "THUMBNAIL:";

/// 成功応答の接頭辞。エディタ側 `RuntimeManager` のパーサと対になる。
pub const REPLY_DONE_PREFIX: &str = "THUMBNAIL_DONE:";

/// 失敗応答の接頭辞。
pub const REPLY_FAILED_PREFIX: &str = "THUMBNAIL_FAILED:";

/// 要求・応答の引数区切り文字。
pub const ARG_SEPARATOR: char = ',';

/// 要求の引数の個数（要求ID / 一辺px / アセットパス）。
const REQUEST_ARG_COUNT: usize = 3;

/// サムネイル一辺のピクセル数の下限。これより小さいと何が写っているか判別できない。
pub const MIN_SIZE_PX: u32 = 16;

/// サムネイル一辺のピクセル数の上限。
///
/// プロジェクトパネルのタイル用途にはこれ以上要らず、
/// 大きくするほどキャッシュ（PNG）がディスクを食う。
/// 高解像度の絵が要る場合は図鑑用の `RENDER_ACTOR_THUMBNAIL` を使う。
pub const MAX_SIZE_PX: u32 = 512;

/// 待ち行列に溜められる要求の最大数。
///
/// これを超えたぶんは**古いほうから捨てる**。
/// 大量のモデルが入ったフォルダを素早く通り過ぎたとき、
/// もう画面に無いタイルのために延々と描き続けないための上限。
pub const MAX_QUEUE_LEN: usize = 64;

/// サムネイルを作れるモデル拡張子の一覧（小文字・ドット無し）。
///
/// `loader::load_model` が実際に読める形式と一致させること。
/// ここに無い拡張子は、GPU を回す前に理由付きで断る。
pub const SUPPORTED_MODEL_EXTENSIONS: &[&str] = &["glb", "gltf", "obj"];

// ============================================================
//  ModelThumbnailRequest — 要求 1 件
// ============================================================

/// モデルサムネイル生成の要求 1 件。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelThumbnailRequest {
    /// エディタが付けた要求 ID。応答にそのまま添えて返す。
    pub request_id: String,
    /// 出力する一辺のピクセル数。
    pub size_px: u32,
    /// 対象モデルの `assets://` 仮想パス（または絶対パス）。
    pub asset_path: String,
}

/// 要求を解釈できなかったときの理由。
///
/// # なぜ要求 ID を一緒に運ぶのか
/// エディタは「この要求 ID の応答が来るまで」タイル 1 枚ぶんの送信枠を握っている。
/// ID の付かない失敗を返すと、どの枠を解放してよいか分からず、
/// 枠を握ったままになって以降の要求が詰まる。
/// **ID さえ読めていれば、その先の検証で落ちた場合も必ず ID を添えて返す。**
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RequestError {
    /// 読み取れた要求 ID。行の形が壊れていて読めなかったときだけ空。
    pub request_id: String,
    /// 人間向けの理由。
    pub message: String,
}

impl RequestError {
    /// 要求 ID が判明している失敗。
    fn with_id(request_id: &str, message: String) -> Self {
        Self { request_id: request_id.to_string(), message }
    }

    /// 要求 ID すら読み取れなかった失敗。
    fn without_id(message: String) -> Self {
        Self { request_id: String::new(), message }
    }
}

/// `{要求ID},{一辺px},{assets:// パス}` を解釈する。
///
/// # パスとカンマ
/// 引数はカンマ区切りなので、パスや ID にカンマが入ると復元できない。
/// 黙って壊れるより、はっきり失敗させる（図鑑側 `RENDER_ACTOR_THUMBNAIL` と同じ方針）。
pub fn parse_request_args(args: &str) -> Result<ModelThumbnailRequest, RequestError> {
    let parts: Vec<&str> = args.split(ARG_SEPARATOR).map(str::trim).collect();
    if parts.len() != REQUEST_ARG_COUNT {
        return Err(RequestError::without_id(format!(
            "引数は「要求ID,一辺px,アセットパス」の {REQUEST_ARG_COUNT} つです（{} 個でした。パスにカンマは使えません）",
            parts.len()
        )));
    }

    // 以降の検証で落ちても、この ID を添えて返す（送信枠を解放させるため）
    let request_id = parts[0].to_string();
    if request_id.is_empty() {
        return Err(RequestError::without_id("要求 ID が空です".to_string()));
    }

    let size_px: u32 = parts[1].parse().map_err(|_| {
        RequestError::with_id(
            &request_id,
            format!("一辺のピクセル数を数値として読めません: '{}'", parts[1]),
        )
    })?;
    if !(MIN_SIZE_PX..=MAX_SIZE_PX).contains(&size_px) {
        return Err(RequestError::with_id(
            &request_id,
            format!(
                "一辺のピクセル数は {MIN_SIZE_PX}〜{MAX_SIZE_PX} の範囲です（{size_px} が指定されました）"
            ),
        ));
    }

    let asset_path = parts[2].to_string();
    if asset_path.is_empty() {
        return Err(RequestError::with_id(&request_id, "アセットパスが空です".to_string()));
    }
    if !is_supported_model_path(&asset_path) {
        return Err(RequestError::with_id(
            &request_id,
            format!(
                "サムネイルを作れない形式です（対応: {}）: '{asset_path}'",
                SUPPORTED_MODEL_EXTENSIONS.join(" / ")
            ),
        ));
    }

    Ok(ModelThumbnailRequest { request_id, size_px, asset_path })
}

/// パスの拡張子が [`SUPPORTED_MODEL_EXTENSIONS`] のいずれかか。
pub fn is_supported_model_path(path: &str) -> bool {
    let Some(dot) = path.rfind('.') else { return false };
    let extension = path[dot + 1..].to_ascii_lowercase();
    SUPPORTED_MODEL_EXTENSIONS.contains(&extension.as_str())
}

// ============================================================
//  応答の書式
// ============================================================

/// 成功応答を組み立てる。
pub fn format_done(request_id: &str, png_path: &str) -> String {
    format!("{REPLY_DONE_PREFIX}{request_id}{ARG_SEPARATOR}{png_path}")
}

/// 失敗応答を組み立てる。
///
/// 理由には人間向けの文が入り、カンマを含みうる。
/// エディタ側は**最初のカンマだけ**で分割すること（ID と理由の 2 つに割る）。
pub fn format_failed(request_id: &str, reason: &str) -> String {
    // 改行が入ると IPC の「1 行 1 メッセージ」が壊れるので潰す
    let single_line = reason.replace(['\r', '\n'], " ");
    format!("{REPLY_FAILED_PREFIX}{request_id}{ARG_SEPARATOR}{single_line}")
}

// ============================================================
//  ThumbnailQueue — 待ち行列
// ============================================================

/// 未処理のサムネイル要求を順番に溜めておく待ち行列。
///
/// 先入れ先出し。[`MAX_QUEUE_LEN`] を超えたら**最も古い要求から捨てる**
/// （捨てた要求には失敗応答を返し、エディタがタイルを待ち続けないようにする）。
///
/// # なぜ要素の型を決め打ちにしないのか
/// 駆動側（`app/thumbnail_ops.rs`）は、要求を受け取った時点で出力先の解決まで
/// 済ませた「撮影指示」を溜めたい（キャッシュに既にある絵をその場で返すため）。
/// 一方この待ち行列が持っている知識は「先入れ先出しで、上限を超えたら最古を捨てる」
/// だけで、中身が何かには一切依存しない。型引数にしておけば、
/// GPU に触れないままここで単体テストでき、駆動側は好きな型を積める。
#[derive(Debug)]
pub struct ThumbnailQueue<T> {
    pending: VecDeque<T>,
}

/// `Default` を手で書く理由: derive すると `T: Default` が要求されてしまい、
/// 撮影指示のような「既定値を持たない型」を積めなくなる。
impl<T> Default for ThumbnailQueue<T> {
    fn default() -> Self {
        Self::new()
    }
}

impl<T> ThumbnailQueue<T> {
    /// 空の待ち行列を作る。
    pub fn new() -> Self {
        Self { pending: VecDeque::new() }
    }

    /// 要求を末尾へ積む。
    ///
    /// 上限を超えた場合、あふれて捨てた要求を返す（呼び出し側が失敗応答を返す）。
    #[must_use = "あふれた要求には失敗応答を返すこと"]
    pub fn push(&mut self, request: T) -> Option<T> {
        self.pending.push_back(request);
        if self.pending.len() > MAX_QUEUE_LEN {
            return self.pending.pop_front();
        }
        None
    }

    /// 先頭の要求を 1 件取り出す。空なら `None`。
    pub fn pop(&mut self) -> Option<T> {
        self.pending.pop_front()
    }

    /// 待っている件数。
    ///
    /// 本番の駆動側は「空かどうか」しか見ないので、これは
    /// 上限（[`MAX_QUEUE_LEN`]）を超えて溜まらないことを検査するテスト専用。
    #[cfg(test)]
    pub fn len(&self) -> usize {
        self.pending.len()
    }

    /// 待ちが 1 件も無いか。
    pub fn is_empty(&self) -> bool {
        self.pending.is_empty()
    }
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の要求を作る。
    fn request(id: &str) -> ModelThumbnailRequest {
        ModelThumbnailRequest {
            request_id: id.to_string(),
            size_px: MIN_SIZE_PX,
            asset_path: "assets://mainGame/models/yasi.glb".to_string(),
        }
    }

    // ─── 応答書式（エディタとの契約）─────────────────────────

    #[test]
    fn reply_formats_match_protocol() {
        assert_eq!(
            format_done("7", r"C:\proj\cache\thumbnails\abc.png"),
            r"THUMBNAIL_DONE:7,C:\proj\cache\thumbnails\abc.png"
        );
        assert_eq!(
            format_failed("7", "読み込めません"),
            "THUMBNAIL_FAILED:7,読み込めません"
        );
    }

    /// 理由の改行が潰され、1 行 1 メッセージの約束が守られること。
    #[test]
    fn failure_reason_never_breaks_the_line_protocol() {
        let reply = format_failed("7", "1 行目\n2 行目\r\n3 行目");
        assert!(!reply.contains('\n'), "応答に改行が残っている: {reply}");
        assert!(!reply.contains('\r'), "応答に復帰が残っている: {reply}");
    }

    // ─── 引数パース ───────────────────────────────────────────

    #[test]
    fn parse_accepts_a_well_formed_request() {
        let parsed = parse_request_args("42,128,assets://mainGame/models/yasi.glb")
            .expect("解釈できるはず");
        assert_eq!(parsed.request_id, "42");
        assert_eq!(parsed.size_px, 128);
        assert_eq!(parsed.asset_path, "assets://mainGame/models/yasi.glb");
    }

    /// 空白は落とされること（エディタの整形ゆれを吸収する）。
    #[test]
    fn parse_trims_surrounding_whitespace() {
        let parsed = parse_request_args(" 42 , 128 , assets://a.glb ").expect("解釈できるはず");
        assert_eq!(parsed.request_id, "42");
        assert_eq!(parsed.asset_path, "assets://a.glb");
    }

    #[test]
    fn parse_rejects_malformed_requests() {
        // 引数の数が違う（＝パスにカンマが入った場合もここで落ちる）
        assert!(parse_request_args("42,128").is_err());
        assert!(parse_request_args("42,128,a.glb,extra").is_err());
        // 要求 ID が空
        assert!(parse_request_args(",128,a.glb").is_err());
        // パスが空
        assert!(parse_request_args("42,128,").is_err());
        // サイズが数値でない
        assert!(parse_request_args("42,big,a.glb").is_err());
    }

    /// 要求 ID が読めた失敗には、必ずその ID が添えられること。
    ///
    /// エディタは「この要求 ID の応答が来るまで」送信枠を 1 つ握っている。
    /// ID 無しで失敗を返すとどの枠を解放してよいか分からず、
    /// 枠が埋まったまま以降のサムネイル要求が全部詰まる。
    #[test]
    fn failures_after_the_id_is_known_carry_that_id() {
        for args in [
            "42,999999,a.glb",   // サイズが範囲外
            "42,big,a.glb",      // サイズが数値でない
            "42,128,",           // パスが空
            "42,128,a.blend",    // 対応外の拡張子
        ] {
            let error = parse_request_args(args).expect_err("失敗するはず");
            assert_eq!(error.request_id, "42", "'{args}' の失敗に要求 ID が付いていない");
            assert!(!error.message.is_empty(), "'{args}' の理由が空");
        }
    }

    /// 行の形自体が壊れていて ID を読めない場合だけ、ID 無しで返ること。
    #[test]
    fn failures_before_the_id_is_known_have_no_id() {
        for args in ["42,128", "42,128,a.glb,extra", ",128,a.glb"] {
            let error = parse_request_args(args).expect_err("失敗するはず");
            assert_eq!(error.request_id, "", "'{args}' は ID を読めないはず");
        }
    }

    /// サイズが範囲外なら断ること（巨大要求で GPU とディスクを食い潰さない）。
    #[test]
    fn parse_rejects_sizes_outside_the_allowed_range() {
        let too_small = MIN_SIZE_PX - 1;
        let too_large = MAX_SIZE_PX + 1;
        assert!(parse_request_args(&format!("1,{too_small},a.glb")).is_err());
        assert!(parse_request_args(&format!("1,{too_large},a.glb")).is_err());
        // 境界そのものは通る
        assert!(parse_request_args(&format!("1,{MIN_SIZE_PX},a.glb")).is_ok());
        assert!(parse_request_args(&format!("1,{MAX_SIZE_PX},a.glb")).is_ok());
    }

    /// 対応していない拡張子は、GPU を回す前に断ること。
    #[test]
    fn parse_rejects_unsupported_extensions() {
        assert!(parse_request_args("1,128,assets://a.blend").is_err());
        assert!(parse_request_args("1,128,assets://a.png").is_err());
        assert!(parse_request_args("1,128,assets://a.fbx").is_err());
        // 拡張子が無い
        assert!(parse_request_args("1,128,assets://noext").is_err());
    }

    /// 対応拡張子の判定が大文字小文字を問わないこと。
    #[test]
    fn supported_extensions_are_case_insensitive() {
        for path in ["a.GLB", "b.Gltf", "c.OBJ", "dir/d.glb"] {
            assert!(is_supported_model_path(path), "{path} を弾いてしまった");
        }
        // 拡張子の一覧はローダの対応形式と一致していること
        assert_eq!(SUPPORTED_MODEL_EXTENSIONS, &["glb", "gltf", "obj"]);
    }

    // ─── 待ち行列 ─────────────────────────────────────────────

    #[test]
    fn queue_is_first_in_first_out() {
        let mut queue: ThumbnailQueue<ModelThumbnailRequest> = ThumbnailQueue::new();
        assert!(queue.is_empty());
        assert!(queue.push(request("1")).is_none());
        assert!(queue.push(request("2")).is_none());
        assert_eq!(queue.len(), 2);
        assert_eq!(queue.pop().expect("1 件目").request_id, "1");
        assert_eq!(queue.pop().expect("2 件目").request_id, "2");
        assert!(queue.pop().is_none());
    }

    /// 上限を超えたら最も古い要求が押し出され、呼び出し側へ返ること。
    #[test]
    fn queue_drops_the_oldest_request_when_full() {
        let mut queue: ThumbnailQueue<ModelThumbnailRequest> = ThumbnailQueue::new();
        for index in 0..MAX_QUEUE_LEN {
            assert!(
                queue.push(request(&index.to_string())).is_none(),
                "上限までは押し出されないこと"
            );
        }
        assert_eq!(queue.len(), MAX_QUEUE_LEN);

        let evicted = queue.push(request("overflow")).expect("最古の要求が押し出されること");
        assert_eq!(evicted.request_id, "0");
        assert_eq!(queue.len(), MAX_QUEUE_LEN, "上限を超えて溜まらないこと");
        // 直前に積んだものは残っている
        assert_eq!(queue.pop().expect("次の先頭").request_id, "1");
    }
}
