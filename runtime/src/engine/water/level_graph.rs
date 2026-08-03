// ============================================================
//  water/level_graph.rs — 水位グラフの数値計算（Phase W2.5）
//
//  正典: docs/water_interaction_roadmap.md §1.2「連通ボリューム方式」。
//
//  ## 役割（単一責任）
//  「水位を持つノード列」と「ノード間の開口（リンク）列」を受け取り、
//  **1 substep ぶんの水の移動を計算して水位を更新する**ことだけを担う。
//  ECS も World も Actor も wgpu も知らない純粋計算モジュールであり、
//  ここに置いたロジックはすべてユニットテストで固定できる。
//  ECS との結線は `water/level_sim.rs` の責務。
//
//  ## 計算モデル（正典どおりの「水位差 × 開口面積 × 係数」）
//
//      流量 Q [m³/s] = 流量係数 × 濡れ断面積 × (水位A − 水位B)
//      移動体積 ΔV   = Q × dt
//      水位変化      = ΔV / 底面積
//
//  グリッドも粒子も持たない。ボリュームが数十個でも実質タダである。
//
//  ## 濡れ断面積の決め方（「階段から地下へ先に落ちる」が出る理由）
//  開口は「下端 Y」「高さ」「幅」「開閉率」で表される矩形である。
//  流れるのは開口のうち **高い方の水位より下** に沈んでいる部分とし、
//
//      濡れ高さ = clamp( min(開口上端, max(水位A, 水位B)) − 開口下端, 0, 開口高さ )
//      濡れ断面積 = 幅 × 濡れ高さ × 開閉率
//
//  とする。**「低い方の水位」ではない**ことが要点である。低い方を採ると
//  「空っぽの部屋へは永遠に水が入らない」（低い側の水位＝床＝開口下端なので
//  断面積が常に 0）という致命的な誤りになる。物理的にも、片側だけが水没した開口は
//  自由流出（滝のように落ちる）として水を通すので、高い方を採るのが正しい。
//  これにより
//    ・両側とも開口下端より水位が低ければ断面積 0 ＝ 流れない
//      （＝低い位置の階段穴が先に水を通し、高い位置の窓は水位が上がるまで通さない）
//    ・空の部屋へも、扉が浸かっている高さぶんだけ流れ込む
//  が自然に出る。**堰越流の非線形性（Q ∝ h^1.5）は扱わない**割り切りである
//  （室内の浸水表現に必要な精度ではないため。既知の制限として docs に記す）。
//
//  ## 有効水位差（開口より下の水は「つながっていない」）
//  水位差はそのまま `水位A − 水位B` にはしない。開口下端より下にある水は
//  互いに行き来できないので、**開口下端で切り上げた水位の差**を使う:
//
//      有効水位差 = max(水位A, 開口下端) − max(水位B, 開口下端)
//
//  これが無いと「高い窓でつながった 2 部屋が窓枠より下まで釣り合ってしまう」
//  （＝水が窓より下を通り抜ける）。有効水位差にしておくと、高い側は
//  **窓枠の高さちょうどで止まる**という直感どおりの挙動になる。
//
//  ## 安定性（明示オイラーを発振させないための唯一の要）
//  素朴な明示オイラーは `dt × 係数 × 面積 × (1/S_A + 1/S_B) > 2` で発振する。
//  開口が大きく底面積が小さい構成（狭い部屋を大扉でつなぐ）は現実的に起こるので、
//  **1 ステップの移動体積を「両者の水位が釣り合う量」でクランプする**。
//
//      釣り合う体積 V_eq = 水位差 / (1/S_A + 1/S_B)
//
//  |ΔV| ≤ V_eq を課すと、どんな dt・係数・面積でも**水位差の符号が反転しない**
//  ＝オーバーシュートしないので、構造的に発振し得ない。物理的にも
//  「1 ステップで釣り合いを通り越して逆側へ行く」ことはないので妥当な制限である。
//
//  ## 体積保存
//  リンクの移動は必ず「一方から引いて他方へ足す」対で行うため、
//  **総体積は厳密に保存される**（クランプが働いても保存される。片側だけ縮めないため）。
//  例外は 2 つだけで、どちらも意図的な物理境界である:
//    ・枯渇: ノードの水が足りないときは、そのノードの全流出リンクを比例縮小する
//            （縮小後に足りなくなることは無いので底を割らない。保存は保たれる）
//    ・溢れ: 天井（AABB 上面）を超えた水は捨てる（＝そのフレームで系外へ出る）
//  溢れだけが保存を破る唯一の経路であり、テストで明示的に固定してある。
// ============================================================

// ─── 数値の下限・上限（マジックナンバー禁止）──────────────────

/// 底面積の下限（m²）。0 除算と、極小面積による水位の爆発を防ぐ。
///
/// 1cm 角の水柱よりさらに小さい面積は「水域として意味がない」ものとして扱う。
pub const LEVEL_BASE_AREA_MIN: f32 = 1.0e-4;

/// 開閉率（バルブ）の下限。
pub const LEVEL_OPENNESS_MIN: f32 = 0.0;
/// 開閉率（バルブ）の上限。
pub const LEVEL_OPENNESS_MAX: f32 = 1.0;

/// これ未満の水位差は「釣り合っている」とみなして流さない（m）。
///
/// 入れておかないと、釣り合い付近で f32 の丸め誤差ぶんの微小流量が永久に往復し、
/// 演出接続（流量に比例した波源）が止まらなくなる。0.1mm は水位として無意味な量。
pub const LEVEL_EQUALIZED_EPSILON: f32 = 1.0e-4;

// ─── LevelNode ───────────────────────────────────────────────

/// 水位グラフのノード 1 個（＝1 つの水ボリューム）。
///
/// 領域の形は「底面積・底 Y・天井 Y」の 3 つに畳み込まれる
/// （直方体の水塊が前提。詳細な形状は水位計算に必要ない）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LevelNode {
    /// 現在の水位（**ワールド Y の絶対値**）。本モジュールが更新する唯一の状態。
    pub level_y: f32,
    /// 水域の底 Y（ワールド）。水位はここより下がらない。
    pub floor_y: f32,
    /// 水域の天井 Y（ワールド）。ここを超えた水は溢れて捨てられる。
    pub ceiling_y: f32,
    /// 水平断面積（m²）。水位 1m ぶんの体積がこの値になる。
    pub base_area: f32,
}

impl LevelNode {
    /// 有効な底面積（下限クランプ済み）。
    fn effective_area(&self) -> f32 {
        self.base_area.max(LEVEL_BASE_AREA_MIN)
    }

    /// 現在の貯水量（m³。底から水位までの体積）。
    fn stored_volume(&self) -> f32 {
        (self.level_y - self.floor_y).max(0.0) * self.effective_area()
    }
}

// ─── LevelLink ───────────────────────────────────────────────

/// ノード間の開口 1 個（扉・窓・穴・バルブ）。
///
/// **向きは持たない**（水位が高い側から低い側へ流れる）。`a` / `b` は
/// ノード配列の添字であり、流量の符号だけが「どちら向きに流れたか」を表す。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LevelLink {
    /// 接続先ノード A の添字。
    pub a: usize,
    /// 接続先ノード B の添字。
    pub b: usize,
    /// 開口の下端 Y（ワールド）。
    pub opening_bottom_y: f32,
    /// 開口の高さ（m）。負値は 0 として扱う。
    pub opening_height: f32,
    /// 開口の幅（m）。負値は 0 として扱う。
    pub opening_width: f32,
    /// 開閉率（0..1）。**0 でバルブ全閉＝流れない**。
    pub openness: f32,
    /// 流量係数（1/s）。大きいほど速く釣り合う。
    pub coefficient: f32,
}

impl LevelLink {
    /// 与えられた 2 つの水位に対する濡れ断面積（m²）を返す。
    ///
    /// 「開口のうち**高い方**の水位より下に沈んでいる部分」の面積である
    /// （低い方を採ると空の部屋へ永遠に水が入らない。モジュール冒頭の設計判断を参照）。
    pub fn wetted_area(&self, level_a: f32, level_b: f32) -> f32 {
        // 開閉率・幅・高さはいずれも負値に意味が無いのでクランプする。
        let openness = self.openness.clamp(LEVEL_OPENNESS_MIN, LEVEL_OPENNESS_MAX);
        let width    = self.opening_width.max(0.0);
        let height   = self.opening_height.max(0.0);
        if openness <= LEVEL_OPENNESS_MIN || width <= 0.0 || height <= 0.0 {
            return 0.0;
        }
        // 高い方の水位（この高さまでの開口が水に触れている）。
        let upper_level = level_a.max(level_b);
        // 濡れている高さ = 開口下端から「開口上端」と「高い方の水位」の小さい方まで。
        let top   = (self.opening_bottom_y + height).min(upper_level);
        let wet_h = (top - self.opening_bottom_y).clamp(0.0, height);
        width * wet_h * openness
    }

    /// 有効水位差（m。**正 = A の方が高い**）を返す。
    ///
    /// 開口下端より下にある水は互いに行き来できないので、両方の水位を
    /// 開口下端で切り上げてから差を取る（モジュール冒頭の設計判断を参照）。
    pub fn effective_head(&self, level_a: f32, level_b: f32) -> f32 {
        level_a.max(self.opening_bottom_y) - level_b.max(self.opening_bottom_y)
    }
}

// ─── LinkFlow ────────────────────────────────────────────────

/// 1 substep でリンクを通った流量（演出接続＝波源の発生に使う）。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LinkFlow {
    /// リンク配列の添字。
    pub link: usize,
    /// 流量（m³/s）。**正 = A から B へ / 負 = B から A へ**。
    pub volume_per_sec: f32,
}

// ─── ステップ計算 ────────────────────────────────────────────

/// 水位グラフを 1 substep（`dt` 秒）進める。
///
/// - `nodes`: 水位を持つノード列。**この関数が `level_y` を更新する**。
/// - `links`: 開口列。ノード添字が範囲外のリンクは黙って無視する
///   （名前参照が解決できなかったリンクを呼び出し側で除外しなくてよいように）。
/// - `dt`: substep 幅（秒）。0 以下なら何もしない。
/// - 戻り値: 実際に流れた流量（リンクごと。流れなかったリンクは含まない）。
///
/// ## 計算の順序（順序依存を作らないための 3 相）
/// ① 全リンクの移動体積を**現在の水位から一斉に**求める
/// ② ノードごとに流出総量を集計し、枯渇するノードの流出を比例縮小する
/// ③ 移動を適用して水位を更新し、天井でクランプする
///
/// ①で「1 本ずつ計算しては即適用」にすると、リンクの並び順で結果が変わる
/// （＝シーン上のスロット順という無関係な要因が水の挙動を変える）。
pub fn step_level_graph(
    nodes: &mut [LevelNode],
    links: &[LevelLink],
    dt:    f32,
) -> Vec<LinkFlow> {
    if dt <= 0.0 || nodes.is_empty() || links.is_empty() {
        return Vec::new();
    }

    // ── ① 各リンクの移動体積（A → B が正）を一斉に求める ──────────
    // 添字が範囲外・自己ループのリンクは 0 として残す（後段の添字が揃うため）。
    let mut deltas: Vec<f32> = Vec::with_capacity(links.len());
    for link in links {
        deltas.push(link_transfer_volume(nodes, link, dt));
    }

    // ── ② 枯渇するノードの流出を比例縮小する ──────────────────
    // 「そのノードから出る量の合計」が「そのノードが持っている水」を超えるなら、
    // 出る側のリンクだけを同じ比率で縮める。入る側は増えるだけなので枯渇に寄与しない。
    // 1 パスで十分（縮小によって他ノードの流出が増えることはない）。
    let mut outflow_sum = vec![0.0f32; nodes.len()];
    for (i, link) in links.iter().enumerate() {
        let d = deltas[i];
        if d > 0.0 {
            outflow_sum[link.a] += d;     // A から出て行く
        } else if d < 0.0 {
            outflow_sum[link.b] += -d;    // B から出て行く
        }
    }
    let scales: Vec<f32> = nodes.iter().enumerate().map(|(n, node)| {
        let out = outflow_sum[n];
        let available = node.stored_volume();
        // 出る量が持っている水以下ならそのまま（1.0）。超えるぶんだけ縮める。
        if out > available && out > 0.0 { available / out } else { 1.0 }
    }).collect();
    for (i, link) in links.iter().enumerate() {
        let d = deltas[i];
        if d > 0.0 {
            deltas[i] = d * scales[link.a];
        } else if d < 0.0 {
            deltas[i] = d * scales[link.b];
        }
    }

    // ── ③ 移動を適用して水位を更新する ────────────────────────
    // 体積を水位へ戻すのは「体積 / 底面積」。対で足し引きするので総体積は保存される。
    let mut flows = Vec::new();
    for (i, link) in links.iter().enumerate() {
        let d = deltas[i];
        if d == 0.0 { continue; }
        let area_a = nodes[link.a].effective_area();
        let area_b = nodes[link.b].effective_area();
        nodes[link.a].level_y -= d / area_a;
        nodes[link.b].level_y += d / area_b;
        flows.push(LinkFlow { link: i, volume_per_sec: d / dt });
    }

    // 天井・床でクランプする。天井を超えたぶんは溢れて系外へ出る（保存を破る唯一の経路）。
    // 床側は②の比例縮小で守られているが、f32 の丸めで僅かに割ることがあるため保険として掛ける。
    for node in nodes.iter_mut() {
        // 反転した範囲（天井 < 床）を与えられても水位が跳ねないよう、床を優先させる。
        node.level_y = node.level_y.min(node.ceiling_y.max(node.floor_y)).max(node.floor_y);
    }

    flows
}

/// リンク 1 本が `dt` 秒で運ぶ体積（m³。**正 = A → B**）を求める。
///
/// 添字が範囲外・自己ループ・断面積 0・水位が釣り合っている場合は 0 を返す。
fn link_transfer_volume(nodes: &[LevelNode], link: &LevelLink, dt: f32) -> f32 {
    // 添字の健全性チェック（名前解決に失敗したリンクを呼び出し側で除外させないため）。
    if link.a >= nodes.len() || link.b >= nodes.len() || link.a == link.b {
        return 0.0;
    }
    let node_a = nodes[link.a];
    let node_b = nodes[link.b];

    // 有効水位差（正 = A の方が高い ＝ A から B へ流れる）。
    // 開口下端より下の水は行き来できないので、そこで切り上げた差を使う。
    let head = link.effective_head(node_a.level_y, node_b.level_y);
    if head.abs() < LEVEL_EQUALIZED_EPSILON { return 0.0; }

    // 濡れ断面積（バルブ全閉・開口が水面より上なら 0）。
    let area = link.wetted_area(node_a.level_y, node_b.level_y);
    if area <= 0.0 { return 0.0; }

    // 正典どおりの線形流量則: Q = 係数 × 面積 × 水位差。
    // 係数は負値に意味が無い（逆流ポンプではない）ので 0 で下限を切る。
    let coefficient = link.coefficient.max(0.0);
    let raw = coefficient * area * head * dt;

    // 釣り合いを通り越さないようにクランプする（発振防止。冒頭の設計判断を参照）。
    // V_eq = 水位差 / (1/S_A + 1/S_B) ＝ この体積を動かすと両者の水位が一致する。
    let inv_area_sum = 1.0 / node_a.effective_area() + 1.0 / node_b.effective_area();
    let v_eq = head / inv_area_sum;   // head と同符号
    // raw と v_eq は同符号（どちらも head の符号）なので、絶対値だけ比較すればよい。
    if raw.abs() > v_eq.abs() { v_eq } else { raw }
}

// ─── ユニットテスト ──────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のノードを作る（底 0m・天井 100m・底面積 area）。
    fn node(level: f32, area: f32) -> LevelNode {
        LevelNode { level_y: level, floor_y: 0.0, ceiling_y: 100.0, base_area: area }
    }

    /// テスト用のリンクを作る（開口は床から 100m ＝ 常に全面が濡れている）。
    fn link(a: usize, b: usize, width: f32, coefficient: f32) -> LevelLink {
        LevelLink {
            a, b,
            opening_bottom_y: 0.0,
            opening_height:   100.0,
            opening_width:    width,
            openness:         1.0,
            coefficient,
        }
    }

    /// ノード列の総体積（m³）。体積保存の検証に使う。
    fn total_volume(nodes: &[LevelNode]) -> f32 {
        nodes.iter().map(|n| (n.level_y - n.floor_y) * n.base_area).sum()
    }

    /// 水位差があるリンクでは高い方から低い方へ水が動くこと。
    #[test]
    fn water_flows_from_high_to_low() {
        let mut nodes = vec![node(2.0, 10.0), node(0.5, 10.0)];
        let links = vec![link(0, 1, 1.0, 0.5)];
        let flows = step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        assert!(nodes[0].level_y < 2.0, "高い側は下がる");
        assert!(nodes[1].level_y > 0.5, "低い側は上がる");
        assert_eq!(flows.len(), 1);
        assert!(flows[0].volume_per_sec > 0.0, "A→B は正の流量");
    }

    /// **体積保存**（W2.5 の中核契約）。クランプが働かない通常ケースで
    /// 総体積が 1 substep でも多数ステップでも変わらないこと。
    #[test]
    fn total_volume_is_conserved_over_many_steps() {
        let mut nodes = vec![node(3.0, 12.0), node(0.0, 7.0), node(1.0, 25.0)];
        let links = vec![link(0, 1, 1.5, 0.4), link(1, 2, 0.8, 0.3)];
        let before = total_volume(&nodes);
        for _ in 0..600 {
            step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        }
        let after = total_volume(&nodes);
        // f32 の累積誤差ぶんだけ許容する（総体積 ~85m³ に対し 1e-2 は 0.01%）。
        assert!((after - before).abs() < 1.0e-2,
            "総体積が保存されること（before={before} after={after}）");
    }

    /// 十分な時間を与えると水位が釣り合い、その後は動かないこと（連通管）。
    #[test]
    fn levels_equalize_and_then_stop() {
        // 釣り合いへの接近は指数的（＝漸近的）なので、
        // `LEVEL_EQUALIZED_EPSILON`（0.1mm）を下回るまでには十分な時間が要る。
        // 4000 ステップ ＝ 約 67 秒ぶん。
        let mut nodes = vec![node(4.0, 10.0), node(0.0, 30.0)];
        let links = vec![link(0, 1, 2.0, 1.0)];
        for _ in 0..4000 {
            step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        }
        assert!((nodes[0].level_y - nodes[1].level_y).abs() < LEVEL_EQUALIZED_EPSILON,
            "水位が釣り合う（{} vs {}）", nodes[0].level_y, nodes[1].level_y);
        // 釣り合った後は流量が報告されない（＝演出の波源も止まる）。
        let flows = step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        assert!(flows.is_empty(), "釣り合った後は流れないこと");
    }

    /// **発振しないこと**: 極端な構成（狭い部屋・大開口・巨大係数）でも
    /// 水位差の符号が反転せず、単調に釣り合いへ向かうこと。
    #[test]
    fn does_not_oscillate_with_extreme_parameters() {
        // 底面積 0.5m² の狭い水槽同士を、幅 50m・係数 1000 の巨大開口でつなぐ。
        // クランプが無ければ 1 ステップで大きくオーバーシュートして発散する構成。
        let mut nodes = vec![node(5.0, 0.5), node(0.0, 0.5)];
        let links = vec![link(0, 1, 50.0, 1000.0)];
        let mut prev_head = nodes[0].level_y - nodes[1].level_y;
        for _ in 0..100 {
            step_level_graph(&mut nodes, &links, 1.0 / 60.0);
            let head = nodes[0].level_y - nodes[1].level_y;
            assert!(head >= -1.0e-3, "水位差の符号が反転した（オーバーシュート）: {head}");
            assert!(head <= prev_head + 1.0e-4, "水位差が増えた（発振）: {prev_head} → {head}");
            assert!(head.is_finite(), "発散した");
            prev_head = head;
        }
        // 釣り合いは「総体積 / 総面積」= 2.5m のはず。
        assert!((nodes[0].level_y - 2.5).abs() < 1.0e-3);
    }

    /// **バルブ全閉（開閉率 0）では 1 滴も流れないこと**（ゲームプレイ制御の中核）。
    #[test]
    fn closed_valve_blocks_all_flow() {
        let mut nodes = vec![node(5.0, 10.0), node(0.0, 10.0)];
        let mut l = link(0, 1, 2.0, 1.0);
        l.openness = 0.0;
        let links = vec![l];
        let flows = step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        assert!(flows.is_empty(), "全閉では流量イベントが出ない");
        assert_eq!(nodes[0].level_y, 5.0);
        assert_eq!(nodes[1].level_y, 0.0);
    }

    /// 開閉率は流量に比例して効くこと（半開はおよそ半分の勢い）。
    #[test]
    fn openness_scales_flow_proportionally() {
        let step = |openness: f32| {
            let mut nodes = vec![node(5.0, 10.0), node(0.0, 10.0)];
            let mut l = link(0, 1, 0.1, 0.01);   // クランプが働かない弱い開口
            l.openness = openness;
            let flows = step_level_graph(&mut nodes, &[l], 1.0 / 60.0);
            flows.first().map(|f| f.volume_per_sec).unwrap_or(0.0)
        };
        let full = step(1.0);
        let half = step(0.5);
        assert!((half * 2.0 - full).abs() < 1.0e-4, "半開は全開の半分（{half} vs {full}）");
    }

    /// **開口が両方の水位より上にあれば流れないこと**
    /// （＝低い位置の階段穴から先に水が落ちる挙動の根拠）。
    #[test]
    fn opening_above_water_blocks_flow() {
        let mut nodes = vec![node(1.0, 10.0), node(0.0, 10.0)];
        // 開口は 3m〜5m。水位は 1m と 0m なのでどちらも開口に届いていない。
        let mut l = link(0, 1, 2.0, 1.0);
        l.opening_bottom_y = 3.0;
        l.opening_height   = 2.0;
        let flows = step_level_graph(&mut nodes, &[l], 1.0 / 60.0);
        assert!(flows.is_empty(), "水面より上の窓は水を通さない");
        assert_eq!(nodes[0].level_y, 1.0);
    }

    /// 開口が部分的に浸かっているときは、浸かっている高さぶんだけの断面積になること。
    /// 判定に使うのは**高い方の水位**である（低い方だと空の部屋が満たせない）。
    #[test]
    fn partially_submerged_opening_uses_wetted_height_only() {
        // 開口 = 幅 2m・下端 1m・高さ 4m（＝1m〜5m）。
        let l = LevelLink {
            a: 0, b: 1,
            opening_bottom_y: 1.0, opening_height: 4.0, opening_width: 2.0,
            openness: 1.0, coefficient: 1.0,
        };
        // 高い方 2m → 濡れ高さ 2−1=1m → 面積 = 2m × 1m = 2m²
        assert!((l.wetted_area(2.0, 0.0) - 2.0).abs() < 1.0e-6);
        // 高い方が開口上端より上なら全面（幅 2m × 高さ 4m = 8m²）
        assert!((l.wetted_area(9.0, 0.0) - 8.0).abs() < 1.0e-6);
        // **空の部屋側（低い方）が開口下端より下でも流れること**（自由流出）。
        assert!(l.wetted_area(9.0, -100.0) > 0.0, "空の部屋へも水は入る");
        // 両側とも開口下端より下なら 0
        assert_eq!(l.wetted_area(0.5, 0.0), 0.0);
    }

    /// **有効水位差は開口下端で切り上げられること**
    /// （開口より下の水は行き来できない ＝ 高い窓でつないだ部屋は窓枠で止まる）。
    #[test]
    fn effective_head_is_clipped_at_the_opening_sill() {
        let l = LevelLink {
            a: 0, b: 1,
            opening_bottom_y: 3.0, opening_height: 2.0, opening_width: 1.0,
            openness: 1.0, coefficient: 1.0,
        };
        // 両側とも敷居より上 → そのままの差
        assert!((l.effective_head(6.0, 4.0) - 2.0).abs() < 1.0e-6);
        // 低い側が敷居より下 → 敷居で切り上げる（6 − 3 = 3）
        assert!((l.effective_head(6.0, -50.0) - 3.0).abs() < 1.0e-6);
        // 両側とも敷居より下 → 0（つながっていない）
        assert_eq!(l.effective_head(1.0, 0.0), 0.0);
    }

    /// 高い窓でつないだ 2 部屋は、**窓枠の高さで水位が止まる**こと
    /// （有効水位差が無いと窓枠より下まで釣り合ってしまう）。
    #[test]
    fn high_window_stops_draining_at_the_sill() {
        // A は水位 8m（床 0）、B は空（水位 0）。窓は 5m〜7m。
        let mut nodes = vec![node(8.0, 10.0), node(0.0, 10.0)];
        let l = LevelLink {
            a: 0, b: 1,
            opening_bottom_y: 5.0, opening_height: 2.0, opening_width: 2.0,
            openness: 1.0, coefficient: 1.0,
        };
        for _ in 0..3000 { step_level_graph(&mut nodes, &[l], 1.0 / 60.0); }
        // A は窓枠（5m）へ**漸近する**。敷居に近づくほど有効水位差も濡れ断面積も
        // 同時に 0 へ向かうので接近は二次的に遅く、有限時間で厳密には到達しない。
        // 要件は「敷居より下へは決して落ちない」ことなので、下側は厳格に、
        // 上側は緩く見る（50 秒ぶんで残り 10cm 程度）。
        assert!(nodes[0].level_y >= 5.0 - 1.0e-5,
            "A が窓枠 5m より下へ落ちた: {}", nodes[0].level_y);
        assert!(nodes[0].level_y < 5.2,
            "A が窓枠付近まで下がること: {}", nodes[0].level_y);
        // B は A から抜けたぶんを受け取る（同じ底面積なので同じ高さぶん）。
        assert!((nodes[1].level_y - (8.0 - nodes[0].level_y)).abs() < 1.0e-3,
            "抜けたぶんがそのまま B に載ること: {}", nodes[1].level_y);
    }

    /// **空の部屋にも水が流れ込むこと**（低い方の水位で断面積を決める実装の回帰テスト）。
    #[test]
    fn empty_room_gets_filled() {
        // B は完全に空（水位 = 床 = 開口下端と同じ 0m）。
        let mut nodes = vec![node(4.0, 10.0), node(0.0, 10.0)];
        let links = vec![link(0, 1, 1.0, 0.5)];
        let flows = step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        assert_eq!(flows.len(), 1, "空の部屋にも流れること");
        assert!(nodes[1].level_y > 0.0, "B の水位が上がること: {}", nodes[1].level_y);
    }

    /// ノードの水が枯渇しても**底を割らない**こと（比例縮小の検証）。
    /// 1 つのノードから 2 本のリンクで同時に吸い出す構成で確かめる。
    #[test]
    fn draining_node_never_goes_below_floor() {
        // 中央（添字 0）の水を、左右（1・2）へ巨大開口で同時に抜く。
        let mut nodes = vec![node(0.2, 1.0), node(0.0, 1000.0), node(0.0, 1000.0)];
        let links = vec![link(0, 1, 100.0, 1000.0), link(0, 2, 100.0, 1000.0)];
        for _ in 0..100 {
            step_level_graph(&mut nodes, &links, 1.0 / 60.0);
            assert!(nodes[0].level_y >= nodes[0].floor_y - 1.0e-5,
                "底（{}）を割った: {}", nodes[0].floor_y, nodes[0].level_y);
        }
    }

    /// 天井を超えた水は溢れて捨てられること（保存を破る唯一の経路）。
    #[test]
    fn overflow_above_ceiling_is_discarded() {
        // 天井 1m の浅い受け皿へ、深い水槽から大量に流し込む。
        let mut nodes = vec![
            LevelNode { level_y: 20.0, floor_y: 0.0, ceiling_y: 100.0, base_area: 100.0 },
            LevelNode { level_y:  0.0, floor_y: 0.0, ceiling_y:   1.0, base_area:   1.0 },
        ];
        let links = vec![link(0, 1, 10.0, 10.0)];
        for _ in 0..200 {
            step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        }
        assert!((nodes[1].level_y - 1.0).abs() < 1.0e-5, "天井で止まること");
    }

    /// 範囲外の添字・自己ループのリンクは無視され、パニックしないこと
    /// （参照名が解決できなかったリンクをそのまま渡せる契約）。
    #[test]
    fn invalid_links_are_ignored_without_panic() {
        let mut nodes = vec![node(5.0, 10.0), node(0.0, 10.0)];
        let links = vec![
            link(0, 99, 1.0, 1.0),   // 添字が範囲外
            link(1, 1, 1.0, 1.0),    // 自己ループ
        ];
        let flows = step_level_graph(&mut nodes, &links, 1.0 / 60.0);
        assert!(flows.is_empty());
        assert_eq!(nodes[0].level_y, 5.0);
    }

    /// dt が 0 以下・リンクが空なら何も起きないこと。
    #[test]
    fn zero_dt_or_no_links_is_a_no_op() {
        let mut nodes = vec![node(5.0, 10.0), node(0.0, 10.0)];
        assert!(step_level_graph(&mut nodes, &[link(0, 1, 1.0, 1.0)], 0.0).is_empty());
        assert!(step_level_graph(&mut nodes, &[], 1.0 / 60.0).is_empty());
        assert_eq!(nodes[0].level_y, 5.0);
    }

    /// 結果がリンクの並び順に依存しないこと（3 相構成の検証）。
    #[test]
    fn result_is_independent_of_link_order() {
        let make = || vec![node(4.0, 8.0), node(1.0, 3.0), node(0.0, 15.0)];
        let l0 = link(0, 1, 1.0, 0.2);
        let l1 = link(1, 2, 1.0, 0.3);

        let mut forward = make();
        let mut reverse = make();
        for _ in 0..30 {
            step_level_graph(&mut forward, &[l0, l1], 1.0 / 60.0);
            step_level_graph(&mut reverse, &[l1, l0], 1.0 / 60.0);
        }
        for i in 0..forward.len() {
            assert!((forward[i].level_y - reverse[i].level_y).abs() < 1.0e-5,
                "ノード {i} の水位が並び順で変わった");
        }
    }
}
