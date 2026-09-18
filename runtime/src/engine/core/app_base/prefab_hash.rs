// ============================================================
//  prefab_hash.rs — プレハブの「取り込んだ版」を表す内容ハッシュ
//
//  【何のための値か】
//  シーン内のプレハブインスタンスは `Actor::prefab_hash` に
//  「取り込んだ時点の `.actor` の内容ハッシュ」を焼き込む。
//  次にシーンを開いたとき、参照先ファイルの現在のハッシュと比べて
//  「プレハブが更新された（stale）」を検出する（`app/prefab_ops.rs`）。
//
//  【生テキストから取る】
//  ハッシュは**ディスク上の生テキスト**から取る。版のマイグレーションを
//  通した後の値から取ると、ファイルの内容と値が食い違う
//  （読むたびに stale 判定が揺れる）。`actor_file::load_with_raw` が
//  生テキストも返すのはこのため。
//
//  【なぜ独立したファイルなのか】
//  この関数は 2 つの離れた場所から要る:
//    - `app/prefab_ops.rs` … 実行時の stale 判定・再展開
//    - `core/migration/upgrade/prefab_rehash.rs` … 一括アップグレード後の貼り直し
//  実装を 2 か所に持つと、片方だけアルゴリズムが変わったときに
//  全インスタンスが一斉に stale 表示になる。定義はここ 1 か所にする。
//
//  【アルゴリズム: FNV-1a 64bit】
//  暗号学的強度は不要（衝突しても「更新に気付かない」だけで破壊は起きない）。
//  外部クレートを増やさず、C# エディタ側でも数行で同じ値を再現できることを優先する。
// ============================================================

/// FNV-1a 64bit のオフセット基底（規格値）。
const FNV_OFFSET_BASIS_64: u64 = 0xcbf2_9ce4_8422_2325;
/// FNV-1a 64bit の素数（規格値）。
const FNV_PRIME_64: u64 = 0x0000_0100_0000_01b3;

/// ハッシュ値を 16 進数で表すときの桁数（64bit ＝ 16 桁）。
pub const PREFAB_HASH_HEX_DIGITS: usize = 16;

/// 文字列の内容ハッシュを 16 桁の 16 進数文字列で返す（FNV-1a 64bit）。
pub fn content_hash(text: &str) -> String {
    let mut hash = FNV_OFFSET_BASIS_64;
    for byte in text.as_bytes() {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(FNV_PRIME_64);
    }
    format!("{hash:0width$x}", width = PREFAB_HASH_HEX_DIGITS)
}

// ── テスト ───────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// 桁数が固定で、内容が違えば値も違うこと。
    #[test]
    fn hash_is_fixed_width_and_content_sensitive() {
        let a = content_hash("{\"name\":\"A\"}");
        let b = content_hash("{\"name\":\"B\"}");
        assert_eq!(a.len(), PREFAB_HASH_HEX_DIGITS);
        assert_eq!(b.len(), PREFAB_HASH_HEX_DIGITS);
        assert_ne!(a, b);
        // 16 進数の文字だけで構成されること（貼り直しの文字列照合が前提にしている）
        assert!(a.chars().all(|c| c.is_ascii_hexdigit()));
    }

    /// 同じ入力からは必ず同じ値が出ること（決定的）。
    #[test]
    fn hash_is_deterministic() {
        let text = "{\"name\":\"Fish\",\"components\":[]}";
        assert_eq!(content_hash(text), content_hash(text));
    }

    /// FNV-1a 64bit の既知の値（規格の検証ベクタ）と一致すること。
    ///
    /// これが変わると、既存シーンの `prefab_hash` が全部ずれて
    /// 「プレハブが更新された」と一斉に表示される。固定しておく。
    #[test]
    fn matches_the_known_fnv1a_vector() {
        // FNV-1a 64bit("a") = 0xaf63dc4c8601ec8c
        assert_eq!(content_hash("a"), "af63dc4c8601ec8c");
        // 空文字はオフセット基底そのもの
        assert_eq!(content_hash(""), "cbf29ce484222325");
    }
}
