// =============================================================================
// SEED アカウント発行窓口 : ログインチャレンジの管理
// =============================================================================
// ログインは「サーバがチャレンジ（1 回限りの乱数）を出す →
// 利用者が秘密鍵で署名する → サーバが登録済み公開鍵で検証する」の 2 往復。
// このファイルは前半のチャレンジだけを持つ。
//
// 設計上の決め事:
//   - **メモリ内にしか置かない。** サーバを再起動したら全部消えてよい
//     （60 秒しか生きないので、永続化する価値より複雑さのほうが高い）。
//   - **1 回限り。** `consume()` は成否にかかわらずエントリを取り除く。
//     取り除かないと、署名を盗聴した相手が同じチャレンジを使い回せる。
//   - **件数上限つき。** 誰でも無認証で発行できるエンドポイントなので、
//     上限が無いと `/v1/login/challenge` を叩き続けるだけでメモリを食い潰せる。
//     上限に達したら、まず期限切れを掃除し、それでも空かなければ拒否する。
//
// このファイルはアカウントの存在を知らない。「未登録の名前でもチャレンジを返す」
// （存在を漏らさない）という契約は、名前を検証せずに受け取ることで自然に満たされる。
// =============================================================================

use std::collections::HashMap;

use parking_lot::Mutex;

use super::crypto::random_token;

// -----------------------------------------------------------------------------
// 定数
// -----------------------------------------------------------------------------

/// チャレンジの有効期間（秒）。契約 3 章の `expires_in: 60` と一致させる。
pub const CHALLENGE_TTL_SECONDS: u64 = 60;

/// 1 秒あたりのミリ秒数（時刻計算はすべて UNIX epoch ミリ秒で行う）。
const MILLIS_PER_SECOND: u64 = 1_000;

/// チャレンジ ID の乱数バイト数（base64url で 22 文字になる）。
const CHALLENGE_ID_BYTES: usize = 16;

/// nonce の乱数バイト数（base64url で 43 文字になる）。
/// 署名対象に入る値なので、ID より長めに取る。
const NONCE_BYTES: usize = 32;

/// 同時に保持できるチャレンジの最大件数。
/// 1 件あたり数百バイトなので、上限に達しても数百 KB で収まる。
/// 60 秒の TTL を考えると、正常な運用でこの数に届くことはない。
const MAX_PENDING_CHALLENGES: usize = 1_024;

/// 受け付けるチャレンジ ID の最大文字数（入力長の上限）。
/// 実際の ID は 22 文字なので十分な余裕がある。
pub const CHALLENGE_ID_MAX_CHARS: usize = 64;

// -----------------------------------------------------------------------------
// データ
// -----------------------------------------------------------------------------

/// 発行済みチャレンジ 1 件。
#[derive(Clone, Debug)]
struct PendingChallenge {
    /// このチャレンジを要求した名前（未登録の名前でも受け付ける）
    name: String,
    /// 署名対象に入る乱数
    nonce: String,
    /// 失効時刻（UNIX epoch ミリ秒）
    expires_at_ms: u64,
}

/// `/v1/login/challenge` の応答に必要な値。
#[derive(Clone, Debug)]
pub struct IssuedChallenge {
    /// チャレンジ ID（`/v1/login/complete` で送り返してもらう）
    pub challenge_id: String,
    /// 署名対象に入る乱数
    pub nonce: String,
    /// 有効期間（秒）
    pub expires_in_seconds: u64,
}

/// `consume()` が返す「使い切ったチャレンジ」の中身。
#[derive(Clone, Debug)]
pub struct ConsumedChallenge {
    /// チャレンジを要求した名前
    pub name: String,
    /// 署名対象に入っていた乱数
    pub nonce: String,
}

/// 発行中のチャレンジを保持する表。
///
/// `Mutex` の中は素の `HashMap` だけ。await をまたいで保持しないので、
/// async 文脈から呼んでも安全（すべての操作が同期で完結する）。
pub struct ChallengeTable {
    pending: Mutex<HashMap<String, PendingChallenge>>,
}

impl Default for ChallengeTable {
    fn default() -> Self {
        Self::new()
    }
}

impl ChallengeTable {
    /// 空の表を作る。
    pub fn new() -> Self {
        Self {
            pending: Mutex::new(HashMap::new()),
        }
    }

    /// チャレンジを 1 件発行する。
    ///
    /// # 引数
    /// - `name`: 要求された名前（存在確認はしない。契約どおり存在を漏らさないため）
    /// - `now_ms`: 現在時刻（UNIX epoch ミリ秒）
    ///
    /// # Errors
    /// 保持件数が上限に達していて期限切れの掃除でも空きが作れない場合、
    /// および乱数生成に失敗した場合。
    pub fn issue(&self, name: &str, now_ms: u64) -> Result<IssuedChallenge, ChallengeIssueError> {
        let challenge_id =
            random_token(CHALLENGE_ID_BYTES).map_err(|_| ChallengeIssueError::RandomFailed)?;
        let nonce = random_token(NONCE_BYTES).map_err(|_| ChallengeIssueError::RandomFailed)?;

        let mut pending = self.pending.lock();

        // 上限に達していたら、まず期限切れを掃除する。
        // 掃除は上限に達したときだけ走らせる（毎回全走査しない）。
        if pending.len() >= MAX_PENDING_CHALLENGES {
            pending.retain(|_, entry| entry.expires_at_ms > now_ms);
        }
        if pending.len() >= MAX_PENDING_CHALLENGES {
            return Err(ChallengeIssueError::TooManyPending);
        }

        pending.insert(
            challenge_id.clone(),
            PendingChallenge {
                name: name.to_string(),
                nonce: nonce.clone(),
                expires_at_ms: now_ms + CHALLENGE_TTL_SECONDS * MILLIS_PER_SECOND,
            },
        );

        Ok(IssuedChallenge {
            challenge_id,
            nonce,
            expires_in_seconds: CHALLENGE_TTL_SECONDS,
        })
    }

    /// チャレンジを 1 回限りで取り出す。
    ///
    /// **見つかった時点で必ず表から取り除く。** 期限切れだった場合も同じ
    /// （取り除かないと、期限切れのエントリが上限を埋めたままになる）。
    ///
    /// 期限内のものが見つかったときだけ `Some` を返す。
    pub fn consume(&self, challenge_id: &str, now_ms: u64) -> Option<ConsumedChallenge> {
        let mut pending = self.pending.lock();
        let entry = pending.remove(challenge_id)?;

        if entry.expires_at_ms <= now_ms {
            return None;
        }

        Some(ConsumedChallenge {
            name: entry.name,
            nonce: entry.nonce,
        })
    }

    /// 現在保持している件数。診断とテスト用。
    ///
    /// バイナリ crate なので通常ビルドでは呼ばれず dead_code 警告になるが、
    /// テストでは使っているので消さずに許可する。
    #[allow(dead_code)]
    pub fn pending_count(&self) -> usize {
        self.pending.lock().len()
    }
}

/// チャレンジ発行に失敗した理由。
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChallengeIssueError {
    /// 保持件数が上限に達している（総当たり／DoS の疑い）
    TooManyPending,
    /// OS の乱数源が使えない
    RandomFailed,
}

// =============================================================================
// 単体テスト
// =============================================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// テスト内の基準時刻（値そのものに意味は無い）。
    const T0: u64 = 1_800_000_000_000;

    /// TTL のミリ秒表現（テストの可読性のため）。
    const TTL_MS: u64 = CHALLENGE_TTL_SECONDS * MILLIS_PER_SECOND;

    /// 発行したチャレンジを 1 回だけ取り出せること。
    #[test]
    fn challenge_can_be_consumed_once() {
        let table = ChallengeTable::new();
        let issued = table.issue("tsubasa", T0).unwrap();
        assert_eq!(issued.expires_in_seconds, CHALLENGE_TTL_SECONDS);
        assert_eq!(table.pending_count(), 1);

        let first = table.consume(&issued.challenge_id, T0).unwrap();
        assert_eq!(first.name, "tsubasa");
        assert_eq!(first.nonce, issued.nonce);
        assert_eq!(table.pending_count(), 0, "使ったら表から消えること");

        // 2 回目は取れない（署名の使い回しができない）
        assert!(table.consume(&issued.challenge_id, T0).is_none());
    }

    /// 期限を過ぎたチャレンジは使えず、かつ表からも消えること。
    #[test]
    fn expired_challenge_is_rejected_and_removed() {
        let table = ChallengeTable::new();
        let issued = table.issue("tsubasa", T0).unwrap();

        // ちょうど期限の瞬間は「切れている」扱い（境界を明示しておく）
        assert!(table.consume(&issued.challenge_id, T0 + TTL_MS).is_none());
        assert_eq!(table.pending_count(), 0, "期限切れでも表から取り除くこと");
    }

    /// 期限直前なら通ること（境界の反対側）。
    #[test]
    fn challenge_just_before_expiry_is_accepted() {
        let table = ChallengeTable::new();
        let issued = table.issue("tsubasa", T0).unwrap();
        assert!(table.consume(&issued.challenge_id, T0 + TTL_MS - 1).is_some());
    }

    /// 存在しない ID では何も起きないこと（panic しないこと）。
    #[test]
    fn unknown_challenge_id_is_none() {
        let table = ChallengeTable::new();
        assert!(table.consume("does-not-exist", T0).is_none());
        assert!(table.consume("", T0).is_none());
    }

    /// 発行のたびに ID と nonce が変わること。
    #[test]
    fn issued_values_are_unique() {
        let table = ChallengeTable::new();
        let a = table.issue("tsubasa", T0).unwrap();
        let b = table.issue("tsubasa", T0).unwrap();
        assert_ne!(a.challenge_id, b.challenge_id);
        assert_ne!(a.nonce, b.nonce);
    }

    /// 上限に達したら、まず期限切れが掃除されて発行が続けられること。
    #[test]
    fn expired_entries_are_reclaimed_at_the_limit() {
        let table = ChallengeTable::new();

        // 上限いっぱいまで古いチャレンジで埋める
        for _ in 0..MAX_PENDING_CHALLENGES {
            table.issue("flood", T0).unwrap();
        }
        assert_eq!(table.pending_count(), MAX_PENDING_CHALLENGES);

        // すべて期限切れになった時点なら、掃除されて新しいものが発行できる
        let later = T0 + TTL_MS + 1;
        assert!(table.issue("tsubasa", later).is_ok());
        assert_eq!(table.pending_count(), 1, "古いものが掃除されていない");
    }

    /// 期限内のもので上限が埋まっていたら発行を拒否すること
    /// （無認証エンドポイントなので、無制限に確保させない）。
    #[test]
    fn issuing_is_refused_when_full_of_live_entries() {
        let table = ChallengeTable::new();
        for _ in 0..MAX_PENDING_CHALLENGES {
            table.issue("flood", T0).unwrap();
        }
        assert_eq!(
            table.issue("tsubasa", T0).unwrap_err(),
            ChallengeIssueError::TooManyPending
        );
    }

    /// 未登録の名前でもチャレンジは普通に発行されること
    /// （アカウントの存在を漏らさないという契約の土台）。
    #[test]
    fn unknown_names_still_get_a_challenge() {
        let table = ChallengeTable::new();
        assert!(table.issue("この名前は登録されていない", T0).is_ok());
    }
}
