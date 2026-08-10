// ============================================================
//  scope.rs — RAII スコープ計測（1 フレーム分の階層ツリー記録）
//
//  `profile_scope!("名前")` を書いた位置から、そのスコープを抜けるまでの経過時間を
//  計測する。計測中のスコープはスレッドローカルのスタックで管理され、
//  「今開いているスコープの子」として登録されるので、呼び出しの入れ子が
//  そのままツリー構造になる。
//
//  同一フレーム内で同じ親の下に同じ名前のスコープが複数回現れた場合は
//  1 ノードにマージし、合計時間と呼び出し回数を加算する
//  （ループ内で計測しても行が増殖しないようにするため）。
// ============================================================

use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

/// フレーム全体を表すルートスコープの名前。
///
/// ツリーの根であり、フレーム比（%）の分母にもなる。
pub const FRAME_ROOT_NAME: &str = "Frame";

/// 1 フレームあたりに記録を許可するノード数の上限。
///
/// 計装ミス（動的に名前が増えるような使い方）でノードが爆発した場合の保険。
/// 上限に達したあとの `profile_scope!` は計測されない（no-op 扱い）。
const MAX_NODES_PER_FRAME: usize = 4096;

/// スコープのネスト深さの上限。
///
/// 再帰関数へ計装した場合にツリーが無限に深くなるのを防ぐ。
/// 上限を超えたスコープは計測されない。
const MAX_SCOPE_DEPTH: usize = 64;

/// プロファイラ計測の有効フラグ。
///
/// エディタのプロファイラパネルが購読しているときだけ true になる。
/// `profile_scope!` は最初にこれを Relaxed で読むだけなので、無効時のコストは
/// 「非アトミックなロード + 分岐」とほぼ同等（追加の同期は行われない）。
static ENABLED: AtomicBool = AtomicBool::new(false);

/// 1 フレーム分の計測ツリーの 1 ノード。
///
/// 親子関係はアリーナ（`Vec<FrameNode>`）の添字で表現する。
#[derive(Clone, Debug)]
pub struct FrameNode {
    /// スコープ名（`&'static str` なので記録時のヒープ確保は発生しない）。
    pub name: &'static str,
    /// 親ノードの添字（ルートは `None`）。
    pub parent: Option<usize>,
    /// 子ノードの添字一覧（登場順）。
    pub children: Vec<usize>,
    /// このフレームでこのスコープに費やした合計時間（ナノ秒）。
    pub total_ns: u64,
    /// このフレームでこのスコープが実行された回数。
    pub calls: u32,
}

/// 1 フレーム分の計測ツリー。
///
/// `nodes[0]` が必ずルート（`FRAME_ROOT_NAME`）になる。
#[derive(Clone, Debug)]
pub struct FrameTree {
    pub nodes: Vec<FrameNode>,
}

impl FrameTree {
    /// ルートノード（フレーム全体）を返す。
    pub fn root(&self) -> &FrameNode {
        &self.nodes[0]
    }

    /// フレーム全体の所要時間（ミリ秒）。
    pub fn frame_ms(&self) -> f64 {
        self.root().total_ns as f64 / NANOS_PER_MILLI
    }
}

/// ナノ秒 → ミリ秒の換算係数（マジックナンバー化を避けるための定数）。
pub const NANOS_PER_MILLI: f64 = 1_000_000.0;

/// スレッドローカルのフレーム記録バッファ。
struct Recorder {
    /// このフレームのノードアリーナ。
    nodes: Vec<FrameNode>,
    /// 現在開いているスコープ（ノード添字, 開始時刻）のスタック。
    stack: Vec<(usize, Instant)>,
    /// このスレッドが計測対象か（`frame_begin` を呼んだスレッドだけ true）。
    ///
    /// rayon ワーカー等の別スレッドでは false のままなので、そこでの
    /// `profile_scope!` は完全な no-op になる。
    active: bool,
}

impl Recorder {
    const fn new() -> Self {
        Self { nodes: Vec::new(), stack: Vec::new(), active: false }
    }
}

thread_local! {
    /// スレッドごとのフレーム記録バッファ。
    /// フレーム間で `Vec` の容量を使い回すため、定常状態ではヒープ確保が起きない。
    static RECORDER: RefCell<Recorder> = const { RefCell::new(Recorder::new()) };
}

/// プロファイラ計測の有効／無効を設定する。
pub fn set_enabled(enabled: bool) {
    ENABLED.store(enabled, Ordering::Relaxed);
}

/// プロファイラ計測が有効か。
#[inline(always)]
pub fn is_enabled() -> bool {
    ENABLED.load(Ordering::Relaxed)
}

/// フレームの記録を開始する（フレームループ冒頭で 1 回だけ呼ぶ）。
///
/// 計測が無効なら何もしない。呼んだスレッドがそのフレームの計測対象になる。
pub fn frame_begin() {
    if !is_enabled() {
        return;
    }
    let now = Instant::now();
    RECORDER.with(|r| {
        let mut rec = r.borrow_mut();
        rec.nodes.clear();
        rec.stack.clear();
        rec.nodes.push(FrameNode {
            name:     FRAME_ROOT_NAME,
            parent:   None,
            children: Vec::new(),
            total_ns: 0,
            calls:    1,
        });
        rec.stack.push((0, now));
        rec.active = true;
    });
    // 前フレームでスクリプトが閉じ忘れたスコープの記録を捨てる
    // （スタックは作り直されるため、位置を持ち越すと誤ったノードを閉じる）。
    SCRIPT_OPEN.with(|s| s.borrow_mut().clear());
}

/// フレームの記録を終了し、記録したツリーを取り出す。
///
/// 計測が無効、またはこのスレッドが計測対象でない場合は `None`。
/// 閉じ忘れているスコープがあっても、ここで全て閉じてから返す（計測漏れで
/// パネルが壊れるより、閉じ忘れ分をルートの自己時間へ寄せる方が安全）。
pub fn frame_end_take() -> Option<FrameTree> {
    let now = Instant::now();
    RECORDER.with(|r| {
        let mut rec = r.borrow_mut();
        if !rec.active {
            return None;
        }
        // 開いたままのスコープを外側へ向かって閉じる（ルートも含む）。
        while let Some((idx, start)) = rec.stack.pop() {
            let elapsed = now.saturating_duration_since(start).as_nanos() as u64;
            rec.nodes[idx].total_ns += elapsed;
        }
        rec.active = false;
        Some(FrameTree { nodes: std::mem::take(&mut rec.nodes) })
    })
}

/// スコープの開始を記録する。
///
/// 戻り値は「スタック上でのこのスコープの位置」（＝push 後の `stack.len() - 1`）。
/// 計測されなかった場合は `None`。位置を返すのは、閉じるときに
/// 「自分より内側に開いたままのスコープをまとめて閉じてから自分を閉じる」ためで、
/// これによりスクリプト側の `Profiler.Begin/End` が不均衡でも
/// エンジン側のスコープが誤って閉じられることがない。
fn scope_begin(name: &'static str) -> Option<usize> {
    RECORDER.with(|r| {
        let mut rec = match r.try_borrow_mut() {
            Ok(rec) => rec,
            // 記録中に再入した場合（想定外）は計測を諦める。パニックさせない。
            Err(_) => return None,
        };
        if !rec.active || rec.stack.len() >= MAX_SCOPE_DEPTH {
            return None;
        }
        let parent = match rec.stack.last() {
            Some(&(idx, _)) => idx,
            None => return None,
        };

        // 同じ親の下の同名ノードを探して合流させる（ループ内計測の行増殖を防ぐ）。
        let existing = rec.nodes[parent]
            .children
            .iter()
            .copied()
            .find(|&c| rec.nodes[c].name == name);

        let idx = match existing {
            Some(idx) => idx,
            None => {
                if rec.nodes.len() >= MAX_NODES_PER_FRAME {
                    return None;
                }
                let idx = rec.nodes.len();
                rec.nodes.push(FrameNode {
                    name,
                    parent: Some(parent),
                    children: Vec::new(),
                    total_ns: 0,
                    calls: 0,
                });
                rec.nodes[parent].children.push(idx);
                idx
            }
        };
        rec.nodes[idx].calls += 1;
        rec.stack.push((idx, Instant::now()));
        Some(rec.stack.len() - 1)
    })
}

/// 指定位置のスコープを閉じる。
///
/// `pos` より内側に開いたままのスコープ（＝スクリプトが `Begin` して `End` し忘れたもの）が
/// あれば、それらも一緒に閉じる。ルート（位置 0）は `frame_end_take` が閉じるので触らない。
fn scope_end(pos: usize) {
    let now = Instant::now();
    RECORDER.with(|r| {
        let mut rec = match r.try_borrow_mut() {
            Ok(rec) => rec,
            Err(_) => return,
        };
        // 位置 0（ルート）は対象外。既に閉じられている（frame_begin でクリアされた等）なら何もしない。
        if pos == 0 || rec.stack.len() <= pos {
            return;
        }
        while rec.stack.len() > pos {
            if let Some((idx, start)) = rec.stack.pop() {
                let elapsed = now.saturating_duration_since(start).as_nanos() as u64;
                rec.nodes[idx].total_ns += elapsed;
            }
        }
    });
}

// ─── スクリプト（C#）からの手動計測 ────────────────────────────
//
// `SEED.Profiler.Begin("名前") / End()` から呼ばれる。RAII を持てない C# 側のために
// 明示的な開始／終了を提供する。開始した数を数えておき、`End` は「自分が開けたぶん」
// しか閉じない（不均衡な呼び出しでエンジン側のスコープを閉じてしまわないため）。

thread_local! {
    /// スクリプトが開いたまま（未 `End`）のスコープ位置スタック。
    static SCRIPT_OPEN: RefCell<Vec<usize>> = const { RefCell::new(Vec::new()) };
}

/// スクリプトからのスコープ開始。計測されたら true。
pub fn script_scope_begin(name: &'static str) -> bool {
    if !is_enabled() {
        return false;
    }
    match scope_begin(name) {
        Some(pos) => {
            SCRIPT_OPEN.with(|s| s.borrow_mut().push(pos));
            true
        }
        None => false,
    }
}

/// スクリプトからのスコープ終了。対応する `Begin` が無ければ何もしない。
pub fn script_scope_end() -> bool {
    SCRIPT_OPEN.with(|s| match s.borrow_mut().pop() {
        Some(pos) => {
            scope_end(pos);
            true
        }
        None => false,
    })
}

/// スコープ計測の RAII ガード。
///
/// `profile_scope!` が生成する。drop 時に経過時間を記録する。
/// 計測が無効な場合 `armed = false` となり、drop は即 return する（実質ゼロコスト）。
pub struct ScopeGuard {
    /// 開いたスコープのスタック上の位置。計測しなかった場合は `None`。
    pos: Option<usize>,
}

impl ScopeGuard {
    /// 計測を開始する。計測無効時は「何も記録しないガード」を返す。
    #[inline]
    pub fn new(name: &'static str) -> Self {
        if !is_enabled() {
            return Self { pos: None };
        }
        Self { pos: scope_begin(name) }
    }
}

impl Drop for ScopeGuard {
    #[inline]
    fn drop(&mut self) {
        if let Some(pos) = self.pos {
            scope_end(pos);
        }
    }
}

/// フレーム内のセクション時間を計測する RAII マクロ。
///
/// 使い方:
/// ```ignore
/// fn update_physics(&mut self) {
///     profile_scope!("物理/3D ステップ");
///     // ...
/// }
/// ```
/// スコープ（ブロック）を抜けた時点で経過時間が記録される。
/// 名前は `&'static str` のみ（動的な文字列は行の増殖を招くため受け付けない）。
#[macro_export]
macro_rules! profile_scope {
    ($name:expr) => {
        // 変数名を衝突しにくいものにして、同一ブロックで複数使ってもシャドウしないようにする。
        let _seed_profile_guard =
            $crate::engine::core::profiling::ScopeGuard::new($name);
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    /// テストは同一プロセス内で並列実行されるが、`ENABLED` はプロセス共有のため
    /// 直列化する必要がある。計測自体はスレッドローカルなので、
    /// このミューテックスで「有効化している区間」を保護すれば十分。
    static TEST_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    /// 有効化 → ルート＋子ノードが記録され、親子関係が保たれることを確認する。
    #[test]
    fn records_nested_scopes_as_tree() {
        let _g = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_enabled(true);
        frame_begin();
        {
            profile_scope!("親");
            {
                profile_scope!("子");
            }
        }
        let tree = frame_end_take().expect("計測が有効ならツリーが取れる");
        set_enabled(false);

        assert_eq!(tree.nodes[0].name, FRAME_ROOT_NAME);
        assert_eq!(tree.nodes[0].children.len(), 1);
        let parent = tree.nodes[0].children[0];
        assert_eq!(tree.nodes[parent].name, "親");
        assert_eq!(tree.nodes[parent].children.len(), 1);
        let child = tree.nodes[parent].children[0];
        assert_eq!(tree.nodes[child].name, "子");
        assert_eq!(tree.nodes[child].parent, Some(parent));
    }

    /// 同じ親の下の同名スコープは 1 ノードへマージされ、呼び出し回数が加算される。
    #[test]
    fn merges_repeated_sibling_scopes() {
        let _g = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_enabled(true);
        frame_begin();
        for _ in 0..5 {
            profile_scope!("ループ内");
        }
        let tree = frame_end_take().expect("計測が有効ならツリーが取れる");
        set_enabled(false);

        assert_eq!(tree.nodes.len(), 2, "ルート + マージされた 1 ノード");
        assert_eq!(tree.nodes[1].calls, 5);
    }

    /// 無効時は完全な no-op（ツリーが取れない）であることを確認する。
    #[test]
    fn disabled_records_nothing() {
        let _g = TEST_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        set_enabled(false);
        frame_begin();
        {
            profile_scope!("計測されない");
        }
        assert!(frame_end_take().is_none());
    }
}
