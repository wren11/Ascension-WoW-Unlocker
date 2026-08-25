

static volatile LONG g_queue_as_group = 0;
static volatile LONG g_auto_accept_group = 0;
static volatile LONG g_auto_accept_queue = 0;

static int ActPushOk(void* L, int ok)
{
    LuaPushNum(L, ok ? 1.0 : 0.0);
    return 1;
}

static int ActAsGroup(void)
{
    return InterlockedCompareExchange(&g_queue_as_group, 0, 0) ? 1 : 0;
}

static void ActEscapeName(const char* in, char* out, size_t out_cap)
{
    size_t o = 0;
    if (!out || out_cap < 3) return;
    out[o++] = '"';
    if (in) {
        while (*in && o + 2 < out_cap) {
            char c = *in++;
            if (c == '"' || c == '\\') {
                if (o + 3 >= out_cap) break;
                out[o++] = '\\';
            }
            if ((unsigned char)c < 32) c = ' ';
            out[o++] = c;
        }
    }
    out[o++] = '"';
    out[o] = '\0';
}

static int ActRun(const char* script)
{
    if (!script || !script[0]) return 0;
    ForceClearTaint();
    RunFrameScriptExecute(script);
    return 1;
}

static int ActQueueUi(const char* script)
{
    uint32_t n;
    if (!script || !script[0]) return 0;
    n = (uint32_t)strlen(script);
    if (!LuaQueueEnqueue(script, n))
        return 0;
    WakeUiForInjectAsync();
    return 1;
}

static int ActInteractNpcScript(const char* name)
{
    char esc[96];
    char script[900];
    ActEscapeName(name && name[0] ? name : "", esc, sizeof(esc));
    _snprintf(script, sizeof(script),
        "local want=%s "
        "if type(GmClearTaint)=='function' then pcall(GmClearTaint) end "
        "if type(GmHwEvent)=='function' then pcall(GmHwEvent,1) end "
        "local function isNpc() "
        "  return UnitExists('target') and not UnitIsPlayer('target') "
        "    and (UnitIsFriend and UnitIsFriend('player','target') or true) end "
        "local ok=false "
        "if want~='' then "
        "  if TargetUnit then pcall(TargetUnit,want) end "
        "  if UnitExists('target') and UnitName('target')==want then ok=true end "
        "  if not ok then for i=1,30 do "
        "    if type(GmTargetNearest)=='function' then pcall(GmTargetNearest,3) "
        "    elseif TargetNearestFriend then pcall(TargetNearestFriend) end "
        "    if UnitExists('target') and UnitName('target')==want then ok=true break end "
        "  end end "
        "end "
        "if not ok then "
        "  if type(GmTargetNearest)=='function' then pcall(GmTargetNearest,3) "
        "  elseif TargetNearestFriend then pcall(TargetNearestFriend) end "
        "end "
        "if type(GmInteractUnit)=='function' then pcall(GmInteractUnit,'target') "
        "elseif InteractUnit then pcall(InteractUnit,'target') end "
        "if type(GmInteract)=='function' and UnitGUID then "
        "  local g=UnitGUID('target') if g then pcall(GmInteract,g) end end",
        esc);
    return ActRun(script);
}

static int ActSendBattlemasterJoin(uint32_t bgTypeId, int asGroup)
{
    uint8_t buf[24];
    uint32_t op = kOpBattlemasterJoin;
    uint64_t guid = 0;
    uint32_t instanceId = 0;
    uint8_t ag = asGroup ? 1u : 0u;
    memcpy(buf + 0, &op, 4);
    memcpy(buf + 4, &guid, 8);
    memcpy(buf + 12, &bgTypeId, 4);
    memcpy(buf + 16, &instanceId, 4);
    buf[20] = ag;
    return InjectClientPacket(buf, 21);
}

static int ActSendArenaJoin(uint8_t slot , int asGroup, int rated)
{
    uint8_t buf[16];
    uint32_t op = kOpBattlemasterJoinArena;
    uint64_t guid = 0;
    memcpy(buf + 0, &op, 4);
    memcpy(buf + 4, &guid, 8);
    buf[12] = slot;
    buf[13] = asGroup ? 1u : 0u;
    buf[14] = rated ? 1u : 0u;
    return InjectClientPacket(buf, 15);
}

static int ActSendLfgJoin(uint32_t dungeonId, uint32_t roles)
{
    uint8_t buf[32];
    uint32_t op = kOpLfgJoin;
    uint8_t n = 1;
    uint8_t cmt = 0;
    size_t o = 0;
    memcpy(buf + o, &op, 4); o += 4;
    memcpy(buf + o, &roles, 4); o += 4;
    buf[o++] = n;
    memcpy(buf + o, &dungeonId, 4); o += 4;
    buf[o++] = cmt;
    return InjectClientPacket(buf, (uint32_t)o);
}

static int ActSendBattlefieldPort(uint32_t bgTypeId, uint8_t action)
{

    uint8_t buf[16];
    uint32_t op = kOpBattlefieldPort;
    uint16_t unk = 0;
    memcpy(buf + 0, &op, 4);
    buf[4] = 0;
    buf[5] = 0;
    memcpy(buf + 6, &bgTypeId, 4);
    memcpy(buf + 10, &unk, 2);
    buf[12] = action;
    return InjectClientPacket(buf, 13);
}

static int ActSendBfMgrAccept(uint32_t opcode)
{

    uint8_t buf[12];
    uint32_t battleId = 0;
    uint8_t accept = 1;
    memcpy(buf + 0, &opcode, 4);
    memcpy(buf + 4, &battleId, 4);
    buf[8] = accept;
    return InjectClientPacket(buf, 9);
}

static int ActSendLfgProposalAccept(void)
{
    uint8_t buf[12];
    uint32_t op = kOpLfgProposalResult;
    uint32_t proposalId = 0;
    uint8_t accept = 1;
    memcpy(buf + 0, &op, 4);
    memcpy(buf + 4, &proposalId, 4);
    buf[8] = accept;
    return InjectClientPacket(buf, 9);
}

static int ActSendRepairAll(void)
{

    uint8_t buf[24];
    uint32_t op = kOpRepairItem;
    uint64_t npc = ProxyReadGuidSlot(kCurrentTargetGuidRva);
    uint64_t item = 0;
    uint8_t guild = 0;
    if (!npc) return 0;
    memcpy(buf + 0, &op, 4);
    memcpy(buf + 4, &npc, 8);
    memcpy(buf + 12, &item, 8);
    buf[20] = guild;
    return InjectClientPacket(buf, 21);
}

static int __cdecl GmSetQueueAsGroup_Lua(void* L)
{
    InterlockedExchange(&g_queue_as_group, (LuaArgNum(L, 1) != 0.0) ? 1 : 0);
    return ActPushOk(L, ActAsGroup());
}

static int __cdecl GmQueueAsGroup_Lua(void* L)
{
    LuaPushNum(L, ActAsGroup() ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmQueueRandomBg_Lua(void* L)
{
    int asGroup = ActAsGroup();
    int ok;
    if (LuaArgNum(L, 1) != 0.0) asGroup = 1;
    ForceClearTaint();
    /* Ascension requires PvP / LFG roles before Random BG will accept the join. */
    ActRun(
        "if SetPVPRoles then pcall(SetPVPRoles,true,true,true) end "
        "if SetLFGRoles then pcall(SetLFGRoles,true,true,true,true) end "
        "if CompleteLFGRoleCheck then pcall(CompleteLFGRoleCheck,true) end "
        "for _,n in ipairs({'PVPFrameRoleButtonTank','PVPFrameRoleButtonHealer',"
        "'PVPFrameRoleButtonDPS','PVPRoleTank','PVPRoleHealer','PVPRoleDPS',"
        "'PVPReadyDialogRoleButtonTank','PVPReadyDialogRoleButtonHealer',"
        "'PVPReadyDialogRoleButtonDPS','RolePollTank','RolePollHealer','RolePollDPS'}) do "
        "  local f=_G[n] if f then "
        "    if f.SetChecked then pcall(f.SetChecked,f,true) end "
        "    if f.Click then pcall(f.Click,f) end "
        "  end "
        "end");
    ok = ActSendBattlemasterJoin(kBgTypeRandom, asGroup);
    ActRun(
        "if SetPVPRoles then pcall(SetPVPRoles,true,true,true) end "
        "if JoinBattlefield then pcall(JoinBattlefield,0) end "
        "if RequestBattlegroundInstanceInfo then pcall(RequestBattlegroundInstanceInfo,1) end "
        "if PVPFrame_Join then pcall(PVPFrame_Join) end "
        "if BattlefieldFrameJoinButton_OnClick then pcall(BattlefieldFrameJoinButton_OnClick) end "
        "if CompleteLFGRoleCheck then pcall(CompleteLFGRoleCheck,true) end");
    return ActPushOk(L, ok || 1);
}

static int __cdecl GmLeaveBattleground_Lua(void* L)
{
    int ok;
    ForceClearTaint();
    ok = SendBareOpcode(kOpLeaveBattlefield);
    ActRun("if LeaveBattlefield then pcall(LeaveBattlefield) end");
    return ActPushOk(L, ok || 1);
}

static int __cdecl GmLeaveBgQueue_Lua(void* L)
{
    int i;
    ForceClearTaint();

    ActSendBattlefieldPort(kBgTypeRandom, 0);
    for (i = 1; i <= 3; i++)
        ActSendBattlefieldPort((uint32_t)i, 0);
    ActSendBfMgrAccept(kOpBfMgrQueueInviteRsp);
    ActRun(
        "if AcceptBattlefieldPort then "
        "  for i=1,3 do pcall(AcceptBattlefieldPort,i,0) end end "
        "if LeaveLFG then pcall(LeaveLFG) end");
    return ActPushOk(L, 1);
}

static int __cdecl GmQueueArena2v2_Lua(void* L)
{
    int asGroup = ActAsGroup();
    int ok;
    ForceClearTaint();
    ok = ActSendArenaJoin(0 , asGroup, 0 );
    ActRun(
        "if JoinArena then pcall(JoinArena,1,false,false) end "
        "if ArenaFrameJoinButton_OnClick then pcall(ArenaFrameJoinButton_OnClick) end");
    return ActPushOk(L, ok || 1);
}

static int __cdecl GmEnterArena2v2_Lua(void* L)
{
    ForceClearTaint();
    ActSendBattlefieldPort(0, 1);
    ActSendBfMgrAccept(kOpBfMgrEntryInviteRsp);
    ActSendBfMgrAccept(kOpBfMgrQueueInviteRsp);
    ActRun(
        "if AcceptBattlefieldPort then "
        "  for i=1,3 do pcall(AcceptBattlefieldPort,i,1) end end "
        "if StaticPopup_Hide then "
        "  for i=1,4 do local f=_G['StaticPopup'..i] "
        "    if f and f:IsShown() and f.button1 then pcall(function() f.button1:Click() end) end "
        "  end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmQueueRandomDungeon_Lua(void* L)
{
    int ok;
    uint32_t roles = 7u;
    ForceClearTaint();
    ok = ActSendLfgJoin(kLfgDungeonRandomWotlk, roles);
    ActSendLfgJoin(kLfgDungeonRandomBc, roles);
    ActSendLfgJoin(kLfgDungeonRandomClassic, roles);
    ActRun(
        "if SetLFGRoles then pcall(SetLFGRoles,true,true,true,true) end "
        "if GetLFGRoles then local l,t,h,d=GetLFGRoles() end "
        "if SetLFGDungeon then pcall(SetLFGDungeon,'LookingForDungeon',260) end "
        "if JoinLFG then pcall(JoinLFG,'LookingForDungeon') end "
        "if LFDQueueFrameFindGroupButton_OnClick then "
        "  pcall(LFDQueueFrameFindGroupButton_OnClick) end "
        "if CompleteLFGRoleCheck then pcall(CompleteLFGRoleCheck,true) end");
    return ActPushOk(L, ok || 1);
}

static int __cdecl GmLeaveRandomDungeon_Lua(void* L)
{
    ForceClearTaint();
    SendBareOpcode(kOpLfgLeave);
    ActRun(
        "if LeaveLFG then pcall(LeaveLFG) end "
        "if LFGTeleport then pcall(LFGTeleport,true) end");
    return ActPushOk(L, 1);
}

static int __cdecl GmAcceptQueue_Lua(void* L)
{
    ForceClearTaint();
    ActSendBattlefieldPort(kBgTypeRandom, 1);
    ActSendBattlefieldPort(0, 1);
    ActSendBfMgrAccept(kOpBfMgrQueueInviteRsp);
    ActSendBfMgrAccept(kOpBfMgrEntryInviteRsp);
    ActSendLfgProposalAccept();
    ActRun(
        "if AcceptBattlefieldPort then "
        "  for i=1,3 do pcall(AcceptBattlefieldPort,i,1) end end "
        "if AcceptProposal then pcall(AcceptProposal) end "
        "if CompleteLFGRoleCheck then pcall(CompleteLFGRoleCheck,true) end "
        "for i=1,4 do local f=_G['StaticPopup'..i] "
        "  if f and f:IsShown() and f.which and f.button1 then "
        "    local w=tostring(f.which) "
        "    if w:find('BF') or w:find('BATTLE') or w:find('LFG') or w:find('CONFIRM') "
        "      or w:find('DUNGEON') or w:find('ARENA') then "
        "      pcall(function() f.button1:Click() end) end end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmAcceptGroup_Lua(void* L)
{
    ForceClearTaint();
    SendBareOpcode(kOpGroupAccept);
    ActRun(
        "if AcceptGroup then pcall(AcceptGroup) end "
        "StaticPopup_Hide('PARTY_INVITE') "
        "for i=1,4 do local f=_G['StaticPopup'..i] "
        "  if f and f:IsShown() and f.which=='PARTY_INVITE' and f.button1 then "
        "    pcall(function() f.button1:Click() end) end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmSetAutoAcceptGroup_Lua(void* L)
{
    InterlockedExchange(&g_auto_accept_group, (LuaArgNum(L, 1) != 0.0) ? 1 : 0);
    LuaPushNum(L, InterlockedCompareExchange(&g_auto_accept_group, 0, 0) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmSetAutoAcceptQueue_Lua(void* L)
{
    InterlockedExchange(&g_auto_accept_queue, (LuaArgNum(L, 1) != 0.0) ? 1 : 0);
    LuaPushNum(L, InterlockedCompareExchange(&g_auto_accept_queue, 0, 0) ? 1.0 : 0.0);
    return 1;
}

static int __cdecl GmGetAutoFlags_Lua(void* L)
{
    LuaPushNum(L, InterlockedCompareExchange(&g_auto_accept_group, 0, 0) ? 1.0 : 0.0);
    LuaPushNum(L, InterlockedCompareExchange(&g_auto_accept_queue, 0, 0) ? 1.0 : 0.0);
    LuaPushNum(L, ActAsGroup() ? 1.0 : 0.0);
    return 3;
}

static int __cdecl GmLeaveGroup_Lua(void* L)
{
    ForceClearTaint();
    SendBareOpcode(kOpGroupDisband);
    ActRun("if LeaveParty then pcall(LeaveParty) end");
    return ActPushOk(L, 1);
}

static int __cdecl GmResetAllInstances_Lua(void* L)
{
    ForceClearTaint();
    SendBareOpcode(kOpResetInstances);
    ActRun("if ResetInstances then pcall(ResetInstances) end");
    return ActPushOk(L, 1);
}

static int __cdecl GmResetLastInstance_Lua(void* L)
{
    return GmResetAllInstances_Lua(L);
}

static int __cdecl GmInteractNpc_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    ForceClearTaint();
    ProxyGrantHwEvent();
    if (!name || !name[0]) {
        ProxyTargetNearestNative(3, 0);
        ProxyInteractUnit("target");
        return ActPushOk(L, 1);
    }
    return ActPushOk(L, ActInteractNpcScript(name));
}

static int __cdecl GmRepairAll_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    ForceClearTaint();
    ActInteractNpcScript(name);
    ActSendRepairAll();
    ActRun(
        "if CanMerchantRepair and CanMerchantRepair() and RepairAllItems then "
        "  pcall(RepairAllItems) end "
        "if MerchantRepairAllButton and MerchantRepairAllButton:IsEnabled() then "
        "  pcall(function() MerchantRepairAllButton:Click() end) end");
    return ActPushOk(L, 1);
}

static int __cdecl GmTrainAll_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    ForceClearTaint();
    ActInteractNpcScript(name);
    ActRun(
        "if SetTrainerServiceTypeFilter then "
        "  pcall(SetTrainerServiceTypeFilter,'available',1) "
        "  pcall(SetTrainerServiceTypeFilter,'unavailable',0) "
        "  pcall(SetTrainerServiceTypeFilter,'used',0) end "
        "if GetNumTrainerServices and BuyTrainerService then "
        "  for _=1,4 do local n=GetNumTrainerServices() or 0 "
        "    if n<=0 then break end "
        "    for i=n,1,-1 do "
        "      local _,_,cat=GetTrainerServiceInfo(i) "
        "      if cat=='available' then "
        "        local cost=GetTrainerServiceCost and GetTrainerServiceCost(i) or 0 "
        "        if (GetMoney() or 0)>=(cost or 0) then pcall(BuyTrainerService,i) end "
        "      end end end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmSellJunk_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    ForceClearTaint();
    ActInteractNpcScript(name);
    ActRun(
        "local function sell() "
        "  if not MerchantFrame or not MerchantFrame:IsShown() then return 0 end "
        "  local n=0 "
        "  for bag=0,4 do "
        "    local slots=GetContainerNumSlots and GetContainerNumSlots(bag) or 0 "
        "    for slot=1,slots do "
        "      local link=GetContainerItemLink and GetContainerItemLink(bag,slot) "
        "      if link then "
        "        local _,_,q,_,_,_,_,_,_,_,price=GetItemInfo(link) "
        "        if (price or 0)>0 and (q or 0)<=1 then "
        "          pcall(UseContainerItem,bag,slot) n=n+1 "
        "        end end end end return n end "
        "if not MerchantFrame or not MerchantFrame:IsShown() then "
        "  if type(GmInteractNpc)=='function' then end "
        "end "
        "sell()");
    return ActPushOk(L, 1);
}

static int __cdecl GmSellAllValuable_Lua(void* L)
{
    const char* name = LuaArgStr(L, 1);
    ForceClearTaint();
    ActInteractNpcScript(name);
    ActRun(
        "if MerchantFrame and MerchantFrame:IsShown() then "
        "  for bag=0,4 do "
        "    local slots=GetContainerNumSlots and GetContainerNumSlots(bag) or 0 "
        "    for slot=1,slots do "
        "      local link=GetContainerItemLink and GetContainerItemLink(bag,slot) "
        "      if link then "
        "        local _,_,_,_,_,_,_,_,_,_,price=GetItemInfo(link) "
        "        if (price or 0)>0 then pcall(UseContainerItem,bag,slot) end "
        "      end end end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmAcceptQuest_Lua(void* L)
{
    ForceClearTaint();
    ActRun(
        "if AcceptQuest then pcall(AcceptQuest) end "
        "if QuestFrameAcceptButton and QuestFrameAcceptButton:IsVisible() then "
        "  pcall(function() QuestFrameAcceptButton:Click() end) end "
        "if GossipFrame and GossipFrame:IsShown() and SelectGossipAvailableQuest then "
        "  local n=GetNumGossipAvailableQuests and GetNumGossipAvailableQuests() or 0 "
        "  if n>0 then pcall(SelectGossipAvailableQuest,1) end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmCompleteQuest_Lua(void* L)
{
    ForceClearTaint();
    ActRun(
        "if CompleteQuest then pcall(CompleteQuest) end "
        "if GetNumQuestChoices and GetNumQuestChoices()>0 and GetQuestReward then "
        "  pcall(GetQuestReward,1) "
        "elseif GetQuestReward then pcall(GetQuestReward) end "
        "if QuestFrameCompleteQuestButton and QuestFrameCompleteQuestButton:IsVisible() then "
        "  pcall(function() QuestFrameCompleteQuestButton:Click() end) end "
        "if GossipFrame and GossipFrame:IsShown() and SelectGossipActiveQuest then "
        "  local n=GetNumGossipActiveQuests and GetNumGossipActiveQuests() or 0 "
        "  if n>0 then pcall(SelectGossipActiveQuest,1) end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmAutoQuestPulse_Lua(void* L)
{
    ForceClearTaint();
    ActRun(
        "if GossipFrame and GossipFrame:IsShown() then "
        "  local a=GetNumGossipAvailableQuests and GetNumGossipAvailableQuests() or 0 "
        "  local c=GetNumGossipActiveQuests and GetNumGossipActiveQuests() or 0 "
        "  if a>0 and SelectGossipAvailableQuest then pcall(SelectGossipAvailableQuest,1) "
        "  elseif c>0 and SelectGossipActiveQuest then pcall(SelectGossipActiveQuest,1) "
        "  elseif GetNumGossipOptions and SelectGossipOption then "
        "    local o=GetNumGossipOptions() or 0 if o>0 then pcall(SelectGossipOption,1) end "
        "  end end "
        "if QuestFrame and QuestFrame:IsShown() then "
        "  if QuestFrameAcceptButton and QuestFrameAcceptButton:IsVisible() then "
        "    pcall(AcceptQuest) "
        "  elseif QuestFrameCompleteQuestButton and QuestFrameCompleteQuestButton:IsVisible() then "
        "    if GetNumQuestChoices and GetNumQuestChoices()>0 then pcall(GetQuestReward,1) "
        "    else pcall(GetQuestReward) end "
        "  elseif QuestFrameCompleteButton and QuestFrameCompleteButton:IsVisible() then "
        "    pcall(CompleteQuest) end end");
    return ActPushOk(L, 1);
}

static int __cdecl GmLootNearestPulse_Lua(void* L)
{
    ForceClearTaint();
    ObjMgrPump();
    /* Prefer Lua GmLootProof (cast bar + loot window + bag proof). Pulse
     * returning 1 is NOT loot success — the session reports ok/fail. */
    ActRun(
        "if type(GmLootProof)=='table' then "
        "  if GmLootProof.Busy and GmLootProof.Busy() then "
        "    if GmLootProof.Tick then GmLootProof.Tick() end "
        "  elseif GmLootProof.StartNearest then "
        "    GmLootProof.StartNearest({radius=40,quiet=true}) "
        "  end "
        "else "
        "  local r=40 "
        "  if type(GmNearestLootable)=='function' then "
        "    local g=GmNearestLootable(r,1) "
        "    if g and type(GmApproachGuid)=='function' then pcall(GmApproachGuid,g) end "
        "    if g and type(GmLootOpen)=='function' then pcall(GmLootOpen,g) "
        "    elseif g and type(GmLootAll)=='function' then pcall(GmLootAll,g) "
        "    elseif g and type(GmRightClick)=='function' then pcall(GmRightClick,g) end "
        "  elseif type(GmLootNearest)=='function' then pcall(GmLootNearest) end "
        "  if GetNumLootItems and (GetNumLootItems() or 0)>0 then "
        "    if type(GmLootMoney)=='function' then pcall(GmLootMoney) end "
        "    for i=1,(GetNumLootItems() or 0) do "
        "      if type(GmLootSlot)=='function' then pcall(GmLootSlot,i) "
        "      elseif LootSlot then pcall(LootSlot,i) end end "
        "    if CloseLoot then pcall(CloseLoot) end "
        "  end "
        "end");
    return ActPushOk(L, 1);
}

static void ProxyAutoAcceptPulse(void)
{
    static DWORD s_last = 0;
    DWORD now = GetTickCount();
    int want_group;
    int want_queue;
    if (now - s_last < 750u)
        return;
    want_group = InterlockedCompareExchange(&g_auto_accept_group, 0, 0) ? 1 : 0;
    want_queue = InterlockedCompareExchange(&g_auto_accept_queue, 0, 0) ? 1 : 0;
    if (!want_group && !want_queue)
        return;
    s_last = now;

    if (want_group) {
        static const char kAcceptGroup[] =
            "if type(GmClearTaint)=='function' then pcall(GmClearTaint) end "
            "if AcceptGroup then pcall(AcceptGroup) end "
            "if StaticPopup_Hide then pcall(StaticPopup_Hide,'PARTY_INVITE') end "
            "for i=1,4 do local f=_G['StaticPopup'..i] "
            "  if f and f:IsShown() and f.which=='PARTY_INVITE' and f.button1 then "
            "    pcall(function() f.button1:Click() end) end end";
        ActQueueUi(kAcceptGroup);
    }
    if (want_queue) {
        static const char kAcceptQueue[] =
            "if type(GmClearTaint)=='function' then pcall(GmClearTaint) end "
            "if AcceptBattlefieldPort then "
            "  for i=1,3 do pcall(AcceptBattlefieldPort,i,1) end end "
            "if AcceptProposal then pcall(AcceptProposal) end "
            "if CompleteLFGRoleCheck then pcall(CompleteLFGRoleCheck,true) end "
            "for i=1,4 do local f=_G['StaticPopup'..i] "
            "  if f and f:IsShown() and f.button1 and f.which then "
            "    local w=tostring(f.which) "
            "    if w:find('BF') or w:find('BATTLE') or w:find('LFG') "
            "      or w:find('CONFIRM') or w:find('DUNGEON') or w:find('ARENA') then "
            "      pcall(function() f.button1:Click() end) end end end";
        ActQueueUi(kAcceptQueue);
    }
}

static void RegisterGmActionApis(RegisterFunctionFn reg)
{
    reg("GmSetQueueAsGroup", (void*)GmSetQueueAsGroup_Lua);
    reg("GmQueueAsGroup", (void*)GmQueueAsGroup_Lua);
    reg("GmQueueRandomBg", (void*)GmQueueRandomBg_Lua);
    reg("GmLeaveBattleground", (void*)GmLeaveBattleground_Lua);
    reg("GmLeaveBgQueue", (void*)GmLeaveBgQueue_Lua);
    reg("GmQueueArena2v2", (void*)GmQueueArena2v2_Lua);
    reg("GmEnterArena2v2", (void*)GmEnterArena2v2_Lua);
    reg("GmQueueRandomDungeon", (void*)GmQueueRandomDungeon_Lua);
    reg("GmLeaveRandomDungeon", (void*)GmLeaveRandomDungeon_Lua);
    reg("GmAcceptQueue", (void*)GmAcceptQueue_Lua);
    reg("GmAcceptGroup", (void*)GmAcceptGroup_Lua);
    reg("GmSetAutoAcceptGroup", (void*)GmSetAutoAcceptGroup_Lua);
    reg("GmSetAutoAcceptQueue", (void*)GmSetAutoAcceptQueue_Lua);
    reg("GmGetAutoFlags", (void*)GmGetAutoFlags_Lua);
    reg("GmLeaveGroup", (void*)GmLeaveGroup_Lua);
    reg("GmResetLastInstance", (void*)GmResetLastInstance_Lua);
    reg("GmResetAllInstances", (void*)GmResetAllInstances_Lua);
    reg("GmInteractNpc", (void*)GmInteractNpc_Lua);
    reg("GmRepairAll", (void*)GmRepairAll_Lua);
    reg("GmTrainAll", (void*)GmTrainAll_Lua);
    reg("GmSellJunk", (void*)GmSellJunk_Lua);
    reg("GmSellAllValuable", (void*)GmSellAllValuable_Lua);
    reg("GmAcceptQuest", (void*)GmAcceptQuest_Lua);
    reg("GmCompleteQuest", (void*)GmCompleteQuest_Lua);
    reg("GmAutoQuestPulse", (void*)GmAutoQuestPulse_Lua);
    reg("GmLootNearestPulse", (void*)GmLootNearestPulse_Lua);
}
