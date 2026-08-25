namespace HeadlessClient.Domain.Protocol;

public static class Opcodes
{
    public const uint CmsgAuthSrp6Begin = 0x0033;
    public const uint CmsgAuthSrp6Proof = 0x0034;
    public const uint SmsgAuthSrp6Response = 0x0039;
    public const uint CmsgCharEnum = 0x0037;
    public const uint SmsgCharEnum = 0x003B;
    public const uint CmsgPlayerLogin = 0x003D;
    public const uint SmsgNewWorld = 0x003E;
    public const uint SmsgLoginVerifyWorld = 0x0236;
    /// <summary>Observed as first SMSG after CMSG_PLAYER_LOGIN on Ascension (enter-world ack).</summary>
    public const uint SmsgAscensionEnterWorldAck = 0x020E;
    /// <summary>Ascension crypt seed / enable handshake (NetClient switch at RVA 0x2333C2).</summary>
    public const uint SmsgAscensionCryptEnable = 0x050F;
    public const uint CmsgAscensionCryptAck = 0x0510;
    public const uint SmsgAscensionCryptSetup = 0x050D;
    public const uint SmsgAuthChallenge = 0x01EC;
    public const uint CmsgAuthSession = 0x01ED;
    public const uint SmsgAuthResponse = 0x01EE;
    public const uint CmsgNameQuery = 0x0050;
    public const uint SmsgNameQueryResponse = 0x0051;
    public const uint CmsgItemQuerySingle = 0x0056;
    public const uint SmsgItemQuerySingleResponse = 0x0058;
    public const uint SmsgPatchItem = 0x0932;
    public const uint CmsgQuestQuery = 0x005C;
    public const uint SmsgQuestQueryResponse = 0x005D;
    public const uint CmsgGameobjectQuery = 0x005E;
    public const uint SmsgGameobjectQueryResponse = 0x005F;
    public const uint CmsgCreatureQuery = 0x0060;
    public const uint SmsgCreatureQueryResponse = 0x0061;
    public const uint CmsgNpcTextQuery = 0x017F;
    public const uint SmsgNpcTextUpdate = 0x0180;
    public const uint MsgQueryNextMailTime = 0x0284;
    public const uint CmsgQueryTime = 0x01CE;
    public const uint SmsgQueryTimeResponse = 0x01CF;
    public const uint SmsgMessageChat = 0x0096;
    public const uint SmsgGmMessageChat = 0x03B3;
    public const uint CmsgMessageChat = 0x0095;
    public const uint CmsgJoinChannel = 0x0097;
    public const uint SmsgChannelNotify = 0x0099;
    public const uint CmsgChannelList = 0x009A;
    public const uint SmsgChannelList = 0x009B;
    public const uint CmsgWho = 0x0062;
    public const uint SmsgWho = 0x0063;
    public const uint SmsgNotification = 0x01CB;
    public const uint SmsgUpdateObject = 0x00A9;
    public const uint SmsgCompressedUpdateObject = 0x01F6;
    public const uint CmsgSetSelection = 0x013D;
    public const uint CmsgAttackSwing = 0x0141;
    public const uint CmsgLoot = 0x015D;
    public const uint CmsgLootMoney = 0x015E;
    public const uint CmsgLootRelease = 0x015F;
    public const uint CmsgGameobjUse = 0x00B1;
    public const uint MsgMoveFallLand = 0x00C9;
    public const uint MsgMoveHeartbeat = 0x00EE;
    public const uint CmsgPing = 0x01DC;
    public const uint SmsgPong = 0x01DD;
    public const uint SmsgTimeSyncReq = 0x0390;
    public const uint CmsgTimeSyncResp = 0x0391;
    public const uint CmsgKeepAlive = 0x0407;
    public const uint CmsgAuthSrp6Recode = 0x0035;
}
