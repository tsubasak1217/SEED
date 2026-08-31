// ============================================================
//  EnemyStateMachine.cs — ステートパターン（State Pattern）実演スクリプト
//
//  【ステートパターンとは】
//  「状態ごとの振る舞い」をそれぞれ独立したクラスに切り出し、
//  本体（コンテキスト）は "現在の状態オブジェクト" に処理を委譲するデザインパターン。
//  if/switch の分岐が状態数に比例して肥大化するのを防ぎ、
//  状態の追加・変更を「クラスを1つ足す/直す」だけで済ませられる。
//
//  【このスクリプトの構成】
//  - IEnemyState        : 状態インターフェース（Enter / Tick / Exit）
//  - IdleState          : 待機。一定時間経過で巡回へ
//  - PatrolState        : 2点間を巡回。プレイヤーが近づくと追跡へ
//  - ChaseState         : プレイヤーを追跡。離れられると帰還へ
//  - ReturnState        : 初期位置へ帰還。到着で待機へ
//  - EnemyStateMachine  : コンテキスト（SEEDScript）。現在状態へ委譲し、遷移を仲介する
//
//  状態遷移図:
//      Idle --(待機時間経過)--> Patrol --(プレイヤー接近)--> Chase
//        ^                                                     |
//        +--(到着)-- Return <--------(プレイヤー逃走)----------+
//
//  【使い方】
//  任意のアクターにアタッチするだけで動く。シーンに "Player" という名前の
//  アクターがあれば追跡対象になる（無ければ待機と巡回だけを繰り返す）。
// ============================================================

using SEEDEditor.Scripting;

public class EnemyStateMachine : SEEDScript
{
    // ── 調整パラメータ（マジックナンバー禁止のため定数化）─────────────
    /// <summary>待機状態の継続時間（秒）</summary>
    private const float IdleDuration = 2.0f;
    /// <summary>巡回時の移動速度（m/秒）</summary>
    private const float PatrolSpeed = 2.0f;
    /// <summary>追跡時の移動速度（m/秒）。巡回より速い</summary>
    private const float ChaseSpeed = 4.0f;
    /// <summary>初期位置からの巡回往復距離（m）</summary>
    private const float PatrolRange = 5.0f;
    /// <summary>プレイヤーがこの距離まで近づいたら追跡開始（m）</summary>
    private const float DetectRadius = 6.0f;
    /// <summary>追跡中、プレイヤーがこの距離まで離れたら諦めて帰還（m）</summary>
    private const float GiveUpRadius = 10.0f;
    /// <summary>目的地に「到着した」とみなす距離（m）</summary>
    private const float ArriveThreshold = 0.1f;

    // ============================================================
    //  ステートパターンの核 ①: 状態インターフェース
    //  すべての状態はこの3つの操作を実装する。
    //  コンテキスト（EnemyStateMachine）はこの型しか知らないため、
    //  状態を増やしてもコンテキスト側のコードは変わらない。
    // ============================================================
    private interface IEnemyState
    {
        /// <summary>デバッグ表示用の状態名</summary>
        string Name { get; }
        /// <summary>この状態に入った瞬間に1回呼ばれる（初期化）</summary>
        void Enter(EnemyStateMachine owner);
        /// <summary>この状態である間、毎フレーム呼ばれる（振る舞い本体＋遷移判定）</summary>
        void Tick(EnemyStateMachine owner, float dt);
        /// <summary>次の状態へ移る直前に1回呼ばれる（後片付け）</summary>
        void Exit(EnemyStateMachine owner);
    }

    // ============================================================
    //  ステートパターンの核 ②: 具象状態クラス群
    //  状態ごとの振る舞いと「次にどの状態へ遷移するか」を
    //  それぞれのクラス内に閉じ込める。
    // ============================================================

    /// <summary>待機状態: その場で一定時間待ち、経過したら巡回へ移る。</summary>
    private sealed class IdleState : IEnemyState
    {
        public string Name => "Idle";

        /// <summary>待機を始めてからの経過時間（秒）</summary>
        private float _elapsed;

        public void Enter(EnemyStateMachine owner)
        {
            _elapsed = 0f;
        }

        public void Tick(EnemyStateMachine owner, float dt)
        {
            _elapsed += dt;
            // 待機時間が満了したら巡回状態へ遷移する
            if (_elapsed >= IdleDuration)
                owner.ChangeState(new PatrolState());
        }

        public void Exit(EnemyStateMachine owner) { }
    }

    /// <summary>
    /// 巡回状態: 初期位置を中心に左右の折り返し点を往復する。
    /// プレイヤーが検知半径内に入ったら追跡状態へ遷移する。
    /// </summary>
    private sealed class PatrolState : IEnemyState
    {
        public string Name => "Patrol";

        /// <summary>現在向かっている折り返し点（ワールド座標）</summary>
        private SEED.Vector3 _target;

        public void Enter(EnemyStateMachine owner)
        {
            // 初期位置から +X 方向の折り返し点をまず目指す
            _target = owner._homePosition + SEED.Vector3.Right * PatrolRange;
        }

        public void Tick(EnemyStateMachine owner, float dt)
        {
            // ── 遷移判定: プレイヤーが近ければ追跡へ ──
            if (owner.TryGetPlayerDistance(out float dist) && dist <= DetectRadius)
            {
                owner.ChangeState(new ChaseState());
                return;
            }

            // ── 振る舞い: 折り返し点へ向かって移動 ──
            owner.MoveTowards(_target, PatrolSpeed, dt);

            // 到着したら反対側の折り返し点へ切り替える（往復）
            if (SEED.Vector3.Distance(owner.transform.Position, _target) <= ArriveThreshold)
            {
                bool headingRight =
                    _target.x > owner._homePosition.x;
                _target = owner._homePosition +
                    (headingRight ? SEED.Vector3.Left : SEED.Vector3.Right) * PatrolRange;
            }
        }

        public void Exit(EnemyStateMachine owner) { }
    }

    /// <summary>
    /// 追跡状態: プレイヤーへ向かって移動し続ける。
    /// プレイヤーが諦め半径より遠くへ逃げる（または消える）と帰還状態へ遷移する。
    /// </summary>
    private sealed class ChaseState : IEnemyState
    {
        public string Name => "Chase";

        public void Enter(EnemyStateMachine owner) { }

        public void Tick(EnemyStateMachine owner, float dt)
        {
            // ── 遷移判定: プレイヤーが消えた/遠すぎるなら帰還へ ──
            if (!owner.TryGetPlayerDistance(out float dist) || dist >= GiveUpRadius)
            {
                owner.ChangeState(new ReturnState());
                return;
            }

            // ── 振る舞い: プレイヤー位置へ向かって移動（追跡速度）──
            if (owner._player.GetComponent<SEED.Transform>() is { } pt)
                owner.MoveTowards(pt.Position, ChaseSpeed, dt);
        }

        public void Exit(EnemyStateMachine owner) { }
    }

    /// <summary>帰還状態: 初期位置へ戻り、到着したら待機状態へ遷移する。</summary>
    private sealed class ReturnState : IEnemyState
    {
        public string Name => "Return";

        public void Enter(EnemyStateMachine owner) { }

        public void Tick(EnemyStateMachine owner, float dt)
        {
            owner.MoveTowards(owner._homePosition, PatrolSpeed, dt);

            // 初期位置に到着したら待機へ戻る（1周して振り出しへ）
            if (SEED.Vector3.Distance(owner.transform.Position, owner._homePosition) <= ArriveThreshold)
                owner.ChangeState(new IdleState());
        }

        public void Exit(EnemyStateMachine owner) { }
    }

    // ============================================================
    //  ステートパターンの核 ③: コンテキスト
    //  「現在の状態オブジェクト」を1つだけ保持し、毎フレームの処理を委譲する。
    //  状態が何種類あってもここは変わらない（Open/Closed 原則）。
    // ============================================================

    /// <summary>現在の状態。null の間は未初期化（初回 Update で Idle が入る）</summary>
    private IEnemyState? _state = null;

    /// <summary>スポーン時の初期位置。巡回の中心・帰還の目的地</summary>
    private SEED.Vector3 _homePosition;

    /// <summary>追跡対象のプレイヤー（シーン名 "Player"。見つからなければ IsValid=false）</summary>
    private SEED.GameObject _player;

    /// <summary>
    /// 状態遷移の唯一の入口。旧状態の Exit → 新状態の Enter を必ずこの順で呼ぶことで、
    /// 各状態は「入るとき/出るとき」の初期化・後片付けを自分の中に閉じ込められる。
    /// </summary>
    private void ChangeState(IEnemyState next)
    {
        _state?.Exit(this);
        SEED.Debug.Log($"[EnemyStateMachine] {_state?.Name ?? "(none)"} -> {next.Name}");
        _state = next;
        _state.Enter(this);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        // 初回だけ: 初期位置とプレイヤー参照を確定し、待機状態から開始する
        if (_state is null)
        {
            _homePosition = transform.Position;
            _player       = SEED.GameObject.Find("Player");
            ChangeState(new IdleState());
        }

        // 毎フレーム: 現在の状態オブジェクトへ処理を委譲する（ここが委譲の1行）。
        // 上の初期化ブロックで必ず ChangeState 済みのため _state は非 null（! で明示）。
        _state!.Tick(this, SEED.Time.DeltaTime);
    }

    // ── 状態クラスから使う共通ヘルパ ─────────────────────────────

    /// <summary>
    /// プレイヤーとの距離を返す。プレイヤーが存在しない場合は false。
    /// 検知（Patrol→Chase）と諦め（Chase→Return）の両方の判定で使う共通処理。
    /// </summary>
    private bool TryGetPlayerDistance(out float distance)
    {
        distance = 0f;
        if (_player.GetComponent<SEED.Transform>() is not { } pt) return false;
        distance = SEED.Vector3.Distance(transform.Position, pt.Position);
        return true;
    }

    /// <summary>
    /// 目的地へ向かって一定速度で移動する共通処理（Y は変えず水平移動のみ）。
    /// キャラクターコントローラー ON のアクターなら、Position を書くだけで
    /// 地形への衝突・押し戻しはエンジン側が自動で解決する。
    /// </summary>
    private void MoveTowards(SEED.Vector3 target, float speed, float dt)
    {
        // 高さは現状維持し、水平面内だけで目的地へ近づく
        var flatTarget = new SEED.Vector3(target.x, transform.Position.y, target.z);
        transform.Position = SEED.Vector3.MoveTowards(
            transform.Position, flatTarget, speed * dt);
    }
}
