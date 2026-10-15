#if PROFILE_UNLOCK_TESTING
namespace FH6LocalCryptoTool;

// Installed FH6 ObjectModelGame.zip, inspected 2026-10-14. All 554 QuickChatData
// definitions passed strict BXML round trips and exact QuickChatDataMap coverage.
// Only 65 visible RequiresUnlock=true / SpecialId=None reward phrases belong here;
// ordinary free phrases and hidden/context/invite messages must not be granted.
// The 33 protected entries are the exact phrase rewards referenced by the 38
// CollectionCategoryProgress threads' objectives, independently cross-checked
// against their localized "Unlock from the Collection Journal." lock messages.
internal static class ForzaLinkPhraseCatalog
{
    internal static IReadOnlyList<ForzaLinkPhraseUnlockState.Phrase> Items { get; } = Array.AsReadOnly(new ForzaLinkPhraseUnlockState.Phrase[]
    {
        new(0x085BFCB293A53FD2UL, "Evasive maneuvres", false),
        new(0x08BC632CD2EABC71UL, "Gotta Smash Em All!", true),
        new(0x0F24DD35D1C45223UL, "Welcome to Japan!", false),
        new(0x10EA7C24CA5BA75CUL, "Better… stronger… faster!", true),
        new(0x116DFDB7834E1763UL, "HO HO HO!", false),
        new(0x12F4727212F77CC3UL, "You're a MONSTER!", false),
        new(0x14F3272DC45D38E8UL, "Dude where’s my car?", false),
        new(0x180547FCA7C58858UL, "I choose my own adventures", false),
        new(0x2505FE232BDFD647UL, "Yup… that's dirt all right", true),
        new(0x257C48536A89D186UL, "Smile! You're on camera!", false),
        new(0x258BD90A0F77F192UL, "Never taken a shortcut before?", true),
        new(0x2636FB58734A188EUL, "Bonus board!", true),
        new(0x2B1B9B11BD991D79UL, "It's working! It's WORKING!", false),
        new(0x2F439F7F9C88C8A0UL, "Happy New Year!", false),
        new(0x302930BB096A6427UL, "Driftacular!", true),
        new(0x38E0E69F5C4EA115UL, "This is fine", false),
        new(0x43291C34C3DA9999UL, "If in doubt, flat out", false),
        new(0x4849F49E18819CC0UL, "Meow", false),
        new(0x4A814A805209703DUL, "Group Photo?", true),
        new(0x4CF9B25D3D2D0A56UL, "Nailed it!", true),
        new(0x4E02725829F3CE72UL, "Where we're going we won't need roads", true),
        new(0x53BA7F27E2AE289AUL, "I also hunt monsters", true),
        new(0x54E9B8460E9DDDDEUL, "Gotta go fast!", true),
        new(0x602DB45844B23C54UL, "Nyooom!", false),
        new(0x60490A6694A29B31UL, "I ain't afraid of no Rivals!", true),
        new(0x639FC7287DDDEE18UL, "Happy Holidays!", false),
        new(0x668CAA1EA9AFD1B7UL, "I am invincible!", false),
        new(0x6BD8166EAAD5DB24UL, "Merry Christmas!", false),
        new(0x6D0E48CD3978504AUL, "Yoink!", true),
        new(0x6E66A271F2A13D4EUL, "I zoned out there for a second", true),
        new(0x700A9F8289AB638CUL, "EUROBEAT INTENSIFIES", false),
        new(0x745822E74F926B6DUL, "My car is prepared", true),
        new(0x7BF2CFCCCC5FC319UL, "Beam me up!", false),
        new(0x7CFE4AB94629F445UL, "Just a happy little accident", false),
        new(0x80B6AB6112D62768UL, "GOOOOOOAAAAALLLLL!!!", true),
        new(0x87CD12B8C67F0599UL, "Brakes? What're those?", false),
        new(0x8CD60651AAEEE027UL, "Are you not entertained?", false),
        new(0x8E28E9ADCC4EA602UL, "Relax, you're doing fine!", false),
        new(0x9005D2CA76DB4FD9UL, "I’m a sneaky snaaaaaaaaaake", false),
        new(0x93D275AA3C99C29AUL, "Well I refuse to lose.", false),
        new(0x94CC640EB5CC1923UL, "Boop!", true),
        new(0x9BC9F273BD90C370UL, "Look out!", true),
        new(0xA4A26AE5F05ED6C1UL, "Watch this!", true),
        new(0xAC1FF7B1C36A7DD6UL, "I've got the skills to pay the bills!", true),
        new(0xAE0C4C0E4F18D04AUL, "Action!!!", false),
        new(0xAFB471B1AC7E4D9BUL, "YOU HAVE NO AUTHORITY HERE", false),
        new(0xB479FD7E82D9F320UL, "Nothin but net!", false),
        new(0xBA67661170DBA169UL, "Special delivery!", true),
        new(0xBD1FB8F5826112FAUL, "I eat danger for breakfast!", true),
        new(0xBE4A7A0BAA1AECB8UL, "I have the high ground", true),
        new(0xC64027B048DF8F22UL, "I'm flying!", true),
        new(0xCB999E5F8FDF945EUL, "World's Fastest!", true),
        new(0xCDCBFB24FDD947EAUL, "Just keep driving, just keep driving", true),
        new(0xCF6D213B28CF6604UL, "Are we there yet?", false),
        new(0xD32BD8D60E43A256UL, "IT'S A TRAP", true),
        new(0xD43A75380E7F2284UL, "I'm famous around here", true),
        new(0xD4C1EC6CC671524EUL, "Too fast for me", true),
        new(0xDE7708CA1E433DDCUL, "Their vision is based on movement", false),
        new(0xE43F893340FFC8CBUL, "Choo Choo!", false),
        new(0xE500E2303248F3B9UL, "I cannot be outraced", true),
        new(0xE7B217B7A6CF81E4UL, "Oooh… shiny!", false),
        new(0xEC92F09A7B76328CUL, "G.O.A.T.", true),
        new(0xF0B3AABC8191104FUL, "C-C-C-C-Combo Breaker!", true),
        new(0xF3ACD08B937B221AUL, "Trick or Treat?", false),
        new(0xF7EEF8D5691CAD23UL, "I'm really feeling it!", true),
    });
}
#endif
