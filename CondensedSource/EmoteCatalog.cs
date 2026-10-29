#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

// Installed FH6 ObjectModelGame.zip EmoteDataMap and all 37 EmoteData definitions,
// inspected 2026-10-14. Strict BXML round trips, complete map coverage, and
// manifest IDs verified against FNV-1a64 of each lower-case module GUID.
// All 24 saved ownership IDs and all three equipped IDs matched this catalog.
// Includes the game's ordinary starting/fallback emote; no asset-name hashes.
internal static class EmoteCatalog
{
    internal static IReadOnlyList<EmoteUnlockState.Emote> Items { get; } = Array.AsReadOnly(new EmoteUnlockState.Emote[]
    {
        new(0x026B7171148AB524UL, "Emote_Karate"),
        new(0x12474F3812BDF582UL, "Emote_BringIt"),
        new(0x229B3A3918E5DFFBUL, "Emote_Spin"),
        new(0x27017B0CD8148E7CUL, "Emote_Shocked"),
        new(0x28FE8469777C40D5UL, "Emote_TakeOff"),
        new(0x2B79580B0EFA4B30UL, "Emote_Dance_B"),
        new(0x2EB8433878C79600UL, "Emote_SuperSentai"),
        new(0x41444243C38487D4UL, "Emote_Strong"),
        new(0x41E8F9190E195312UL, "Emote_Confused"),
        new(0x4C534AF2CB3CA6E2UL, "Emote_Karaoke"),
        new(0x5AE2C67D41777649UL, "Emote_Dance_A"),
        new(0x5F4044876C0C59DCUL, "Idle_LookAround_B"),
        new(0x6AB0847F7F71CE3EUL, "Emote_Snap"),
        new(0x6BC2F17D8324FCDAUL, "Emote_SoCute"),
        new(0x70F27AE1EA2496D0UL, "Emote_Sleepy"),
        new(0x70F2C1A7784D0FBCUL, "Emote_DJ"),
        new(0x72BC6E64809D10D7UL, "Emote_Humble"),
        new(0x98150F447DCF3801UL, "Emote_Robot"),
        new(0x9C0CF387654ED309UL, "Emote_Dance_H"),
        new(0x9DE7126B77F93BA5UL, "Emote_Relax"),
        new(0xA09BF7100D558D0DUL, "Emote_Classical_Dance"),
        new(0xA15C21636BC3EC67UL, "Emote_BodyPop_B"),
        new(0xA248092D65A7D68CUL, "Emote_ThumbsDown"),
        new(0xB14DE72006A2195FUL, "Emote_Body_Pop"),
        new(0xBE39D5D64638777BUL, "Emote_Number1"),
        new(0xC7032AED8024D6C1UL, "Emote_Macarena"),
        new(0xCE576C2706EF5CFDUL, "Emote_Perfect"),
        new(0xD24901F24AC40D75UL, "Emote_MexicanWave"),
        new(0xD59F197B32AE32D2UL, "Emote_PepTalk"),
        new(0xD92B52C831892278UL, "Emote_BowArrow"),
        new(0xDD88F6F3AB72133CUL, "Emote_RockOut_A"),
        new(0xE5F44D056963ED41UL, "Emote_Regal_Wave"),
        new(0xE9FE906F87C72016UL, "Emote_ThumbsUp"),
        new(0xEBC3F348ECB51F8DUL, "Emote_Dance_D"),
        new(0xEE6B8857E3F0BFB0UL, "Emote_BlowKiss"),
        new(0xF4D0A752367907C0UL, "Emote_KickUps"),
        new(0xF6117C6F6F1F5A68UL, "Emote_Jazz_Hands"),
    });
}
#endif
