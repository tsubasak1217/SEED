// W1-0 スパイク: メインプロセス → :seed_platform の同期呼び出し（AIDL 版。ContentResolver.call との比較用）
package com.seedengine.platformspike;

interface IPlatformSpike {
    /** SeedPlatform.invoke(module, method, byte[] json) の形を模した往復。中身はそのまま返す。 */
    byte[] invoke(String method, in byte[] json);
}
