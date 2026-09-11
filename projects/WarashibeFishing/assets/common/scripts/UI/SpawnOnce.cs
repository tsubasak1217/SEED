// ============================================================================
//  SpawnOnce.cs
//  「同じ補助アクタを二度生成しない」ための共通ヘルパー。
// ============================================================================

/// <summary>
/// プレハブから補助アクタを生成するとき、<b>既に同名のものがあればそれを使い回す</b>
/// 静的ヘルパー【OnStart での二重生成を防ぐ唯一の入口】。
///
/// 【なぜ必要か】
/// エディタでスクリプトを保存すると、ランタイムはユーザースクリプトを再コンパイルして
/// <b>全スクリプトインスタンスを作り直す</b>（ホットリロード）。作り直しでは
/// <c>OnStart</c> が改めて呼ばれるため、<c>OnStart</c> の中で
/// <c>GameObject.Instantiate</c> しているものは<b>呼ばれた回数だけ増えていく</b>。
/// 実際に、Play しながら編集していると
///   - ポーズメニュー・リザルト・評価バナー・巻き取り音・打点アイコン・火花
/// が二重三重に生成され、フレーム時間が伸びる／古い方が前面に残る、という症状が出た。
///
/// 本来の対策はエディタ側で「Play 中はホットリロードを保留する」ことだが、
/// それだけだと「Play 中も即時反映する」設定を使う人・シーン再読込・
/// 将来の別経路で同じ事故が起きうる。そこで<b>多重防御</b>として、
/// スクリプト側も「既にあるなら作らない」ように書けるようにする。
///
/// 【使い方】
/// <code>
/// // 親を指定しない（シーン全体から探す）
/// reelSoundActor = SpawnOnce.GetOrInstantiate("ReelSound", reelSoundActorPath);
///
/// // 親の配下から探して、無ければその親の下に作る
/// var icon = SpawnOnce.GetOrInstantiate($"BeatIcon{i:00}", beatIconActorPath, parent);
/// </code>
///
/// 【重要な制約】
/// <list type="bullet">
///   <item>
///     生成も改名もフレーム末尾に反映される。したがって<b>同じフレーム内で
///     同じ名前を 2 回要求すると 2 つ作られる</b>。呼び出し側は名前を一意にすること
///     （プールなら連番を振る）。
///   </item>
///   <item>
///     名前で照合するため、<c>actorName</c> は<b>シーン内で意味が一意</b>である必要がある。
///     親を渡せる場合は必ず渡すこと（探索範囲が親の配下に限定され、
///     同じプレハブを複数並べても他人のものを掴まない）。
///   </item>
///   <item>
///     見つけた既存アクタは<b>そのままの状態</b>（表示・位置・アニメの進行）で返る。
///     初期化が必要なら呼び出し側が明示的に行うこと。
///   </item>
/// </list>
/// </summary>
public static class SpawnOnce
{
    /// <summary>
    /// 名前で既存アクタを探し、無ければプレハブから生成して<b>その名前を付けて</b>返す。
    /// </summary>
    /// <param name="actorName">
    /// 目印にするアクタ名。生成した場合はこの名前が設定される（<c>GameObject.Name</c>）。
    /// 空文字のときは照合できないため、常に新規生成して改名も行わない。
    /// </param>
    /// <param name="prefabPath">
    /// 生成元の .actor パス（<c>assets://...</c>）。空のときは何も生成せず、
    /// 既存アクタが見つかればそれを返す（見つからなければ無効な GameObject）。
    /// </param>
    /// <param name="parent">
    /// 探索と生成の親。指定すると<b>その配下だけ</b>を探し、生成もその子として行う。
    /// null（既定）ならシーン全体から探し、シーンのルートへ生成する。
    /// </param>
    /// <returns>
    /// 既存または新規生成したアクタ。生成に失敗した場合は <c>IsValid == false</c> の GameObject。
    /// </returns>
    public static SEED.GameObject GetOrInstantiate(
        string actorName, string prefabPath, SEED.GameObject? parent = null)
    {
        // 1. 既存を探す。親が渡されていれば、その配下だけを見る（他インスタンスの
        //    同名アクタを掴まないため）。親が無効なハンドルなら「親指定なし」と同じ扱い。
        bool hasParent = parent is { IsValid: true };

        if (!string.IsNullOrEmpty(actorName))
        {
            SEED.GameObject found = hasParent
                ? parent!.Value.FindChild(actorName)
                : SEED.GameObject.Find(actorName);
            if (found.IsValid) { return found; }
        }

        // 2. 無いので作る。パスが未設定なら作りようがないので無効ハンドルを返す
        //    （呼び出し側が「設定漏れ」として警告を出せるよう、ここでは黙って返す）。
        if (string.IsNullOrEmpty(prefabPath)) { return default; }

        SEED.GameObject spawned = hasParent
            ? SEED.GameObject.Instantiate(prefabPath, parent!.Value)
            : SEED.GameObject.Instantiate(prefabPath);

        // 3. 次回以降ここで見つけられるよう、目印の名前を付ける。
        //    反映はフレーム末尾だが、同フレーム中に Name を読めば設定値が返る。
        if (spawned.IsValid && !string.IsNullOrEmpty(actorName)) { spawned.Name = actorName; }

        return spawned;
    }
}
