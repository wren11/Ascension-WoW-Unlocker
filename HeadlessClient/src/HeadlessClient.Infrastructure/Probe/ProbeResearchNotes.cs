namespace HeadlessClient.Infrastructure.Probe;

/// <summary>
/// Research notes for WotLK 3.3.5a (build 12340/12344) Ascension probing —
/// sourced from TrinityCore Opcodes.h, gtker/wow_messages, and live lab captures.
/// </summary>
public static class ProbeResearchNotes
{
    public static readonly string[] HtmlBullets =
    [
        "Catalog: 924 CMSG/MSG opcodes from GmToolbox/Opcodes.cs (Trinity 3.3.5a aligned).",
        "Layouts: QUERY family from gtker wow_messages (CREATURE=entry+guid, ITEM=u32, QUEST=u32, NAME=guid).",
        "Interactions: GOSSIP_HELLO / TRAINER_LIST / LIST_INVENTORY / BANKER / AUCTION_HELLO = guid.",
        "Blacklisted: char delete/create, logout, guild demote/disband, destroy item, chat spam, GM nuke, teleports.",
        "Correlation window: 350ms after each CMSG; noise SMSG (UPDATE_OBJECT, MONSTER_MOVE, PONG, TIME_SYNC) filtered from hits.",
        "Fill atoms: Object Manager GUIDs/entries/xyz + scraped inbound packet GUIDs.",
        "Ascension quirk: custom channel joins need General/LocalDefense first; crypt opcodes 0x050D–0x0510 are Ascension-specific.",
        "Refs: github.com/TrinityCore/TrinityCore (3.3.5 Opcodes.h), gtker.com/wow_messages, WowPacketParser.",
    ];
}
