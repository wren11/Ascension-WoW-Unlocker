--[[
  GmLootProof — deterministic in-world loot.

  Object-manager XYZ is the only "where we are / where it is".
  Interact returning 1, a timer elapsing, or GmLootOne are NOT success.

  Proven ok requires, in order:
    1. live OM object XYZ
    2. player OM pose within interact of that XYZ (after TP to exact OM loc + face)
    3. opening cast/channel finished, or none started
    4. loot window actually open (or autoloot bag/chat evidence)
    5. take issued
    6. bag fingerprint / chat loot / money / emptied slots prove the take

  Fail is reported with a reason. Callers must not mark looted on fail.
]]

GmLootProof = GmLootProof or {}
local P = GmLootProof
P.VERSION = "1.0.3"

local INTERACT = 5.2
local INTERACT_MAX = 5.5
local NAV_MISS = -99999
local TYPE_GO = 0x20

local function hasBit(v, b)
  v, b = tonumber(v) or 0, tonumber(b) or 0
  if b <= 0 then return false end
  return (v % (b + b)) >= b
end

local S = {
  active = false,
  phase = "idle",
  guid = nil,
  wantItemId = nil,
  map = 0,
  t0 = 0,
  phaseT = 0,
  tpTries = 0,
  interactTries = 0,
  bagFp = "",
  bagFree = 0,
  money = 0,
  wantCount = 0,
  lootGen = 0,
  lootOpened = false,
  lootClosed = false,
  lootChat = false,
  chatIds = {},
  slotIds = {},
  sawCast = false,
  castInterrupted = false,
  ox = 0, oy = 0, oz = 0,
  px = 0, py = 0, pz = 0,
  dist = 99,
  result = nil,
  why = "",
  onObject = false,
}

local frame

local function now()
  return GetTime and GetTime() or 0
end

local function finite(n)
  return type(n) == "number" and n == n and n > -1e30 and n < 1e30
end

local function nativeOk(v)
  return v ~= nil and v ~= false and v ~= 0
end

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[LootProof]|r " .. tostring(msg))
  end
end

local function clearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmHwEvent) == "function" then pcall(GmHwEvent, 1) end
end

local function omPlayer()
  if type(GmShared) == "table" and type(GmShared.OmPlayer) == "function" then
    return GmShared.OmPlayer()
  end
  if type(GmPlayerPose) == "function" then
    local ok, x, y, z, o, map = pcall(GmPlayerPose)
    if ok and finite(x) and finite(y) then return x, y, z, o, map end
  end
  return nil
end

local function omLive(guid)
  if type(GmShared) == "table" and type(GmShared.OmLive) == "function" then
    return GmShared.OmLive(guid)
  end
  if type(GmObjectPump) == "function" then pcall(GmObjectPump, 1) end
  if type(GmObjectByGuid) ~= "function" then return nil end
  local r = { pcall(GmObjectByGuid, guid) }
  if not r[1] then return nil end
  local x, y, z = tonumber(r[3]), tonumber(r[4]), tonumber(r[5])
  if not (finite(x) and finite(y)) then return nil end
  if math.abs(x) < 0.01 and math.abs(y) < 0.01 then return nil end
  return x, y, z or 0, tonumber(r[6]), tonumber(r[11]), tonumber(r[14]), tonumber(r[13])
end

local function dist3(ax, ay, az, bx, by, bz)
  ax, ay, az = tonumber(ax), tonumber(ay), tonumber(az) or 0
  bx, by, bz = tonumber(bx), tonumber(by), tonumber(bz) or 0
  if not (ax and ay and bx and by) then return nil end
  local dx, dy, dz = ax - bx, ay - by, az - bz
  return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function bagFp()
  if type(GmShared) == "table" and type(GmShared.BagFingerprint) == "function" then
    return GmShared.BagFingerprint()
  end
  return ""
end

local function bagsFree()
  if type(GmShared) == "table" and type(GmShared.BagsFree) == "function" then
    return GmShared.BagsFree()
  end
  local free = 0
  for bag = 0, 4 do
    local n = GetContainerNumFreeSlots and GetContainerNumFreeSlots(bag)
    if n then free = free + n end
  end
  return free
end

local function bagHas(id)
  if type(GmShared) == "table" and type(GmShared.BagHasItem) == "function" then
    return GmShared.BagHasItem(id)
  end
  return false, 0
end

local function lootGen()
  if type(GmLastLootPkt) ~= "function" then return 0 end
  local ok, op, dir, guid, tick, len, gen = pcall(GmLastLootPkt)
  if not ok then return 0 end
  return tonumber(gen) or 0
end

local function lootSlotCount()
  if GetNumLootItems then return GetNumLootItems() or 0 end
  if GetNumLootItems then return GetNumLootItems() or 0 end
  return 0
end

local function lootWindowOpen()
  if lootSlotCount() > 0 then return true end
  if LootFrame and LootFrame.IsShown and LootFrame:IsShown() then return true end
  return false
end

local function slotIds()
  local ids = {}
  local n = GetNumLootItems and (GetNumLootItems() or 0) or 0
  for i = 1, n do
    local link = GetLootSlotLink and GetLootSlotLink(i)
    if type(link) == "string" then
      local id = tonumber(link:match("item:(%d+)"))
      if id then ids[#ids + 1] = id end
    end
  end
  return ids
end

local function playerCasting()
  if UnitCastingInfo and UnitCastingInfo("player") then return true, "cast" end
  if UnitChannelInfo and UnitChannelInfo("player") then return true, "channel" end
  return false
end

local function lootFullyClear()
  if playerCasting() then return false end
  if lootSlotCount() > 0 then return false end
  if LootFrame and LootFrame.IsShown and LootFrame:IsShown() then return false end
  if lootWindowOpen() and not S.lootClosed then return false end
  return true
end

local function maybeCloseLoot()
  if lootSlotCount() > 0 then return false end
  if type(CloseLoot) == "function" then pcall(CloseLoot) end
  return true
end

local function lootDestZ(ox, oy, oz, map)
  oz = tonumber(oz) or 0
  map = tonumber(map) or 0
  local nav
  if type(GmNavZ) == "function" then
    local hint = (oz > -200 and oz < 400) and oz or 500
    local ok, z = pcall(GmNavZ, ox, oy, map, hint)
    if ok and type(z) == "number" and z > NAV_MISS then nav = z end
  end
  if oz > 400 or oz < -200 then
    return nav or oz, nav
  end
  -- Stand ON the object. Never sink under the mesh (Hunt does that only
  -- after an on-top landing, and only while in combat — then we are
  -- already in interact range and this TP phase is skipped).
  local dest = oz + 0.45
  if nav and nav <= oz + 1.5 and nav >= oz - 2.5 then
    dest = nav + 0.12
  end
  if dest < oz then dest = oz + 0.45 end
  return dest, nav
end

local function facePoint(tx, ty)
  clearTaint()
  if type(GmFaceUnit) == "function" and S.guid then
    local ok, r = pcall(GmFaceUnit, S.guid)
    if ok and nativeOk(r) then return true end
  end
  if type(GmFace) == "function" then
    local ok, r = pcall(GmFace, tx, ty)
    if ok and nativeOk(r) then return true end
  end
  local px, py = S.px, S.py
  if finite(px) and finite(py) and finite(tx) and finite(ty) then
    local ang = math.atan2(ty - py, tx - px)
    if ang < 0 then ang = ang + (2 * math.pi) end
    if type(GmSetFacing) == "function" then
      local ok, r = pcall(GmSetFacing, ang)
      if ok and nativeOk(r) then return true end
    end
  end
  return false
end

local function teleportExact(x, y, z, o, map)
  if type(GmShared) == "table" and type(GmShared.TpGuardPulse) == "function" then
    pcall(GmShared.TpGuardPulse)
  end
  clearTaint()
  o = o or 0
  map = map or 0
  local flags, lockMs = 3, 400
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  if type(GmTeleportRaw) == "function" then
    local ok, r = pcall(GmTeleportRaw, x, y, z, o, flags, lockMs)
    if ok and nativeOk(r) then return true end
  end
  if type(GmTeleport) == "function" then
    local ok, r = pcall(GmTeleport, x, y, z, o, flags, lockMs)
    if ok and nativeOk(r) then return true end
  end
  return false
end

local function interact(guid)
  clearTaint()
  if type(GmTargetGuid) == "function" then pcall(GmTargetGuid, guid) end
  if type(GmSetMouseover) == "function" then pcall(GmSetMouseover, guid) end
  -- Open only. Never GmLootOne / GmLootAll here — those take+release without a window.
  if type(GmLootOpen) == "function" then pcall(GmLootOpen, guid) end
  if type(ObjectInteract) == "function" then pcall(ObjectInteract, guid) end
  if type(GmInteract) == "function" then pcall(GmInteract, guid) end
  if type(GmInteractGuid) == "function" then pcall(GmInteractGuid, guid) end
  if type(GmUseObject) == "function" then pcall(GmUseObject, guid) end
  if type(GmRightClick) == "function" then pcall(GmRightClick, guid) end
  if type(GmInteractUnit) == "function" then
    pcall(GmInteractUnit, "mouseover")
    pcall(GmInteractUnit, "target")
  elseif InteractUnit then
    pcall(InteractUnit, "mouseover")
    pcall(InteractUnit, "target")
  end
  return true
end

local function takeSlots(guid)
  clearTaint()
  local n = GetNumLootItems and (GetNumLootItems() or 0) or 0
  if n > 0 then
    for i = 1, n do
      local has = true
      if LootSlotHasItem then has = LootSlotHasItem(i) end
      if has and LootSlot then pcall(LootSlot, i) end
      if type(GmLootSlot) == "function" then pcall(GmLootSlot, i) end
    end
  end
  if type(GmLootTake) == "function" then pcall(GmLootTake, guid or "") end
  if type(GmLootMoney) == "function" then pcall(GmLootMoney) end
  if n < 1 and type(GmLootSlot) == "function" then
    for i = 1, 16 do pcall(GmLootSlot, i) end
  end
end

local function finish(ok, why, extra)
  extra = extra or {}
  S.why = why or (ok and "ok" or "fail")
  S.result = {
    ok = ok and true or false,
    why = S.why,
    guid = S.guid,
    ids = extra.ids or S.slotIds,
    chatIds = S.chatIds,
    dist = S.dist,
    px = S.px, py = S.py, pz = S.pz,
    ox = S.ox, oy = S.oy, oz = S.oz,
    gotWant = extra.gotWant,
    empty = extra.empty,
    bagChanged = extra.bagChanged,
    lootOpened = S.lootOpened,
  }
  S.active = false
  S.phase = ok and "ok" or "fail"
  if (not ok) or lootSlotCount() <= 0 then
    if type(CloseLoot) == "function" then pcall(CloseLoot) end
  end
  if type(GmLootRelease) == "function" and S.guid and ((not ok) or lootSlotCount() <= 0) then
    pcall(GmLootRelease, S.guid)
  end
  if frame then frame:Hide() end
  return S.phase, S.why, S.result
end

local function succeed(why, extra)
  S.pendingWhy = why
  S.pendingExtra = extra
  if playerCasting() or lootSlotCount() > 0 or (lootWindowOpen() and not S.lootClosed) then
    setPhase("settle")
    return "busy", "loot-settle", nil
  end
  maybeCloseLoot()
  return finish(true, why, extra)
end

local function refreshOm()
  local px, py, pz, o, map = omPlayer()
  if not px then return false, "no player OM pose" end
  S.px, S.py, S.pz = px, py, pz
  S.map = S.map ~= 0 and S.map or (map or 0)
  local ox, oy, oz, dist, typemask = omLive(S.guid)
  if not ox then return false, "object not in object manager" end
  S.ox, S.oy, S.oz = ox, oy, oz
  S.dist = dist or dist3(px, py, pz, ox, oy, oz) or 99
  S.typemask = typemask
  S.facing = o
  return true
end

local function inRange()
  if S.dist and S.dist <= INTERACT_MAX then return true end
  local d = dist3(S.px, S.py, S.pz, S.ox, S.oy, S.oz)
  return d and d <= INTERACT_MAX
end

local function snapshotBags()
  S.bagFp = bagFp()
  S.bagFree = bagsFree()
  S.money = GetMoney and GetMoney() or 0
  S.lootGen = lootGen()
  if S.wantItemId then
    local _, n = bagHas(S.wantItemId)
    S.wantCount = n or 0
  end
end

local function takeProven()
  local fp = bagFp()
  local free = bagsFree()
  local money = GetMoney and GetMoney() or 0
  local bagChanged = (fp ~= "" and S.bagFp ~= "" and fp ~= S.bagFp)
      or (free < S.bagFree)
      or (money > S.money)
  local gotWant = false
  if S.wantItemId then
    local has, n = bagHas(S.wantItemId)
    if has and (n or 0) > (S.wantCount or 0) then gotWant = true end
    for i = 1, #(S.chatIds or {}) do
      if S.chatIds[i] == S.wantItemId then gotWant = true end
    end
    for i = 1, #(S.slotIds or {}) do
      if S.slotIds[i] == S.wantItemId then
        -- in window is not take-proof; bag/chat is
      end
    end
  end
  local chatHit = S.lootChat and #(S.chatIds) > 0
  return bagChanged or chatHit or gotWant, bagChanged, gotWant
end

local function setPhase(p)
  S.phase = p
  S.phaseT = now()
end

local function step()
  if not S.active then return "fail", S.why or "idle", S.result end
  if _G.GmDeath and GmDeath.ShouldHold and GmDeath.ShouldHold("LootProof") then
    return "busy", "death-hold", nil
  end
  if UnitIsDeadOrGhost and UnitIsDeadOrGhost("player") then
    return "busy", "dead", nil
  end

  local age = now() - S.phaseT

  if S.phase == "om" then
    local ok, why = refreshOm()
    if not ok then return finish(false, why) end
    snapshotBags()
    if inRange() then
      setPhase("face")
    else
      setPhase("tp")
    end
    return "busy", S.phase, nil
  end

  if S.phase == "tp" then
    local ok, why = refreshOm()
    if not ok then return finish(false, why) end
    if inRange() then
      setPhase("face")
      return "busy", "in-range", nil
    end
    if age < 0.05 and S.tpTries == 0 then
      -- fall through to first TP immediately
    elseif age < 0.18 and S.tpTries > 0 then
      return "busy", "tp-settle", nil
    end
    S.tpTries = S.tpTries + 1
    if S.tpTries > 3 then
      return finish(false, string.format("not in interact range (om dist=%.2f)", S.dist or -1))
    end
    local destZ = lootDestZ(S.ox, S.oy, S.oz, S.map)
    local ang = math.atan2(S.oy - S.py, S.ox - S.px)
    if ang < 0 then ang = ang + (2 * math.pi) end
    -- Gameobjects (veins/herbs): always snap to live OM XYZ on top.
    -- GmApproachGuid walks beside the object and mining/herb interact misses.
    local usedC = false
    local isGo = hasBit(S.typemask, TYPE_GO) or S.onObject
    if not isGo and type(GmApproachGuid) == "function" then
      local cok, r = pcall(GmApproachGuid, S.guid)
      usedC = cok and nativeOk(r)
    end
    if not usedC then
      if type(GmShared) == "table" and type(GmShared.TeleportKeepZ) == "function" then
        usedC = GmShared.TeleportKeepZ(S.ox, S.oy, destZ, ang, { lockMs = 400 }) and true or false
      end
      if not usedC then
        if not teleportExact(S.ox, S.oy, destZ, ang, S.map) then
          return finish(false, "teleport to object OM xyz failed")
        end
      end
    end
    if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
    if type(GmTeleport_Unlock) == "function" then pcall(GmTeleport_Unlock) end
    if type(GmShared) == "table" and GmShared.Hop and GmShared.Hop.PumpOM then
      GmShared.Hop.PumpOM(true)
    end
    S.phaseT = now()
    local ok2 = refreshOm()
    if ok2 and inRange() then
      setPhase("face")
      return "busy", string.format("tp-landed dist=%.2f z=%.2f", S.dist or -1, destZ), nil
    end
    return "busy", string.format("tp#%d destZ=%.2f omZ=%.2f dist=%.2f", S.tpTries, destZ, S.oz, S.dist or -1), nil
  end

  if S.phase == "face" then
    local ok, why = refreshOm()
    if not ok then return finish(false, why) end
    if not inRange() then
      if S.tpTries < 3 then
        setPhase("tp")
        return "busy", "re-tp", nil
      end
      return finish(false, string.format("face but om dist=%.2f", S.dist or -1))
    end
    facePoint(S.ox, S.oy)
    snapshotBags()
    setPhase("interact")
    return "busy", "faced", nil
  end

  if S.phase == "interact" then
    if age < 0.05 then return "busy", "interact-gap", nil end
    S.interactTries = S.interactTries + 1
    S.lootOpened = false
    S.castInterrupted = false
    S.sawCast = false
    interact(S.guid)
    setPhase("wait_cast")
    return "busy", "interact-sent", nil
  end

  if S.phase == "wait_cast" then
    local casting = playerCasting()
    if casting then S.sawCast = true end
    if S.castInterrupted then
      if S.interactTries < 3 then
        setPhase("interact")
        return "busy", "cast-interrupted-retry", nil
      end
      return finish(false, "opening cast interrupted")
    end
    if S.sawCast then
      if casting then
        if age > 10 then return finish(false, "opening cast timeout") end
        return "busy", "opening", nil
      end
      setPhase("wait_window")
      return "busy", "cast-done", nil
    end
    -- Never treat a bag change as done while still casting or while slots remain.
    if lootWindowOpen() then
      setPhase("window")
      return "busy", "window", nil
    end
    local proven, bagChanged, gotWant = takeProven()
    if proven and (not playerCasting()) and lootFullyClear() then
      return succeed("autoloot", { bagChanged = bagChanged, ids = S.chatIds, gotWant = gotWant })
    end
    if age > 0.45 then
      setPhase("wait_window")
    end
    if age > 8 then return finish(false, "no opening cast and no loot window") end
    return "busy", "wait-cast-or-window", nil
  end

  if S.phase == "wait_window" then
    if lootWindowOpen() then
      setPhase("window")
      return "busy", "window", nil
    end
    local proven, bagChanged, gotWant = takeProven()
    if proven and (not playerCasting()) and lootFullyClear() then
      return succeed("autoloot", { bagChanged = bagChanged, ids = S.chatIds, gotWant = gotWant })
    end
    if age > 1.2 and S.interactTries < 3 then
      setPhase("interact")
      return "busy", "re-interact", nil
    end
    if age > 6 then
      return finish(false, "loot window never opened")
    end
    return "busy", "wait-window", nil
  end

  if S.phase == "window" then
    S.slotIds = slotIds()
    local n = GetNumLootItems and (GetNumLootItems() or 0) or 0
    if n <= 0 and S.lootOpened then
      return succeed("empty-window", { empty = true, ids = {} })
    end
    setPhase("take")
    return "busy", string.format("slots=%d", n), nil
  end

  if S.phase == "take" then
    takeSlots(S.guid)
    setPhase("wait_taken")
    return "busy", "take-sent", nil
  end

  if S.phase == "wait_taken" then
    if age < 0.15 then
      takeSlots(S.guid)
    end
    local proven, bagChanged, gotWant = takeProven()
    local n = GetNumLootItems and (GetNumLootItems() or 0) or 0
    local slotsEmpty = n <= 0
    if proven then
      if lootSlotCount() > 0 then
        takeSlots(S.guid)
        return "busy", "take-remaining", nil
      end
      return succeed("taken", {
        bagChanged = bagChanged, ids = S.slotIds, gotWant = gotWant,
      })
    end
    if slotsEmpty and S.lootOpened and age > 0.35 then
      -- Window closed after take but bag/chat not seen — still not proven.
      -- One more bag poll; if still no evidence, fail.
      if age > 1.2 then
        return finish(false, "loot window closed without bag/chat/money proof")
      end
    end
    if age > 4 then
      return finish(false, "take not proven (bags/chat/money unchanged)")
    end
    if age > 0.5 and n > 0 then
      takeSlots(S.guid)
    end
    return "busy", "wait-taken", nil
  end

  if S.phase == "settle" then
    if lootSlotCount() > 0 then
      takeSlots(S.guid)
      return "busy", "settle-take", nil
    end
    if playerCasting() then
      return "busy", "settle-cast", nil
    end
    if lootWindowOpen() then
      maybeCloseLoot()
      return "busy", "settle-close", nil
    end
    if age < 0.40 then
      return "busy", "settle-wait", nil
    end
    maybeCloseLoot()
    return finish(true, S.pendingWhy or "taken", S.pendingExtra or {})
  end

  return finish(false, "unknown-phase " .. tostring(S.phase))
end

function P.Busy()
  return S.active and true or false
end

function P.Guid()
  return S.guid
end

function P.Blocking(exceptGuid)
  if playerCasting() then return true end
  if lootWindowOpen() then return true end
  if lootSlotCount() > 0 then return true end
  if LootFrame and LootFrame.IsShown and LootFrame:IsShown() then return true end
  if S.active then
    if exceptGuid and S.guid and tostring(exceptGuid) == tostring(S.guid) then
      return false
    end
    return true
  end
  return false
end

if type(GmShared) == "table" then
  GmShared.LootBlocking = P.Blocking
end

function P.Phase()
  return S.phase, S.why, S.dist
end

function P.Last()
  return S.result
end

function P.Abort(why)
  if not S.active then return end
  finish(false, why or "abort")
end

function P.Tick()
  local t = now()
  if S.lastTick == t and S.didStep then
    if not S.active then
      return S.phase == "ok" and "ok" or (S.phase == "fail" and "fail" or "idle"),
        S.why, S.result
    end
    return "busy", S.phase, nil
  end
  S.lastTick = t
  S.didStep = true
  if not S.active then
    return S.phase == "ok" and "ok" or (S.phase == "fail" and "fail" or "idle"),
      S.why, S.result
  end
  return step()
end

local function ensureFrame()
  if frame then return frame end
  frame = CreateFrame("Frame", "GmLootProofWatch")
  frame:RegisterEvent("LOOT_OPENED")
  frame:RegisterEvent("LOOT_CLOSED")
  frame:RegisterEvent("LOOT_SLOT_CLEARED")
  frame:RegisterEvent("CHAT_MSG_LOOT")
  frame:RegisterEvent("CHAT_MSG_MONEY")
  frame:RegisterEvent("BAG_UPDATE")
  frame:RegisterEvent("UNIT_SPELLCAST_INTERRUPTED")
  frame:RegisterEvent("UNIT_SPELLCAST_FAILED")
  frame:RegisterEvent("UNIT_SPELLCAST_STOP")
  frame:RegisterEvent("UNIT_SPELLCAST_CHANNEL_STOP")
  frame:SetScript("OnEvent", function(_, ev, a1, a2)
    if not S.active then return end
    if ev == "LOOT_OPENED" then
      S.lootOpened = true
      S.slotIds = slotIds()
    elseif ev == "LOOT_CLOSED" then
      S.lootClosed = true
    elseif ev == "CHAT_MSG_LOOT" then
      local msg = tostring(a1 or "")
      if msg:find("You receive loot", 1, true) or msg:find("You receive item", 1, true)
          or msg:find("You loot", 1, true) then
        S.lootChat = true
        local id = tonumber(msg:match("item:(%d+)"))
        if id then S.chatIds[#S.chatIds + 1] = id end
      end
    elseif ev == "CHAT_MSG_MONEY" then
      S.lootChat = true
    elseif ev == "UNIT_SPELLCAST_INTERRUPTED" or ev == "UNIT_SPELLCAST_FAILED" then
      if a1 == "player" then S.castInterrupted = true end
    end
  end)
  frame:SetScript("OnUpdate", function(_, dt)
    if not S.active then
      frame:Hide()
      return
    end
    P.Tick()
  end)
  return frame
end

function P.Start(guid, opts)
  opts = opts or {}
  guid = guid and tostring(guid) or ""
  guid = guid:gsub("^0[xX]", "")
  if guid == "" then
    return false, "no guid"
  end
  if S.active then P.Abort("preempted") end
  S.active = true
  S.guid = guid
  S.wantItemId = tonumber(opts.wantItemId or opts.itemId)
  S.onObject = opts.onObject == true
  S.map = tonumber(opts.map) or 0
  S.t0 = now()
  S.phaseT = now()
  S.tpTries = 0
  S.interactTries = 0
  S.lootOpened = false
  S.lootClosed = false
  S.lootChat = false
  S.pendingWhy = nil
  S.pendingExtra = nil
  S.lastTick = 0
  S.didStep = false
  S.chatIds = {}
  S.slotIds = {}
  S.sawCast = false
  S.castInterrupted = false
  S.result = nil
  S.why = "start"
  S.phase = "om"
  ensureFrame():Show()
  if opts.quiet ~= true then
    chat("start " .. guid .. (S.wantItemId and (" item:" .. S.wantItemId) or ""))
  end
  return true
end

function P.StartNearest(opts)
  opts = opts or {}
  local r = tonumber(opts.radius) or 40
  local g
  if type(GmNearestLootable) == "function" then
    local ok, gg = pcall(GmNearestLootable, r, 1)
    if ok then g = gg end
  end
  if not g or g == "" then
    return false, "no lootable in OM"
  end
  return P.Start(g, opts)
end

SLASH_GMLOOTPROOF1 = "/gmloot"
SlashCmdList.GMLOOTPROOF = function(msg)
  msg = string.lower(tostring(msg or ""):match("^%s*(.-)%s*$") or "")
  if msg == "last" or msg == "status" then
    local r = P.Last()
    chat(string.format("phase=%s busy=%s why=%s dist=%.2f",
      tostring(S.phase), tostring(S.active), tostring(S.why), tonumber(S.dist) or -1))
    if r then
      chat(string.format("last ok=%s why=%s guid=%s ids=%d opened=%s",
        tostring(r.ok), tostring(r.why), tostring(r.guid),
        r.ids and #r.ids or 0, tostring(r.lootOpened)))
    end
    return
  end
  if msg == "stop" or msg == "abort" then
    P.Abort("slash")
    chat("aborted")
    return
  end
  local guid = msg
  if guid == "" or guid == "nearest" or guid == "near" then
    local ok, why = P.StartNearest({})
    chat(ok and "nearest started" or ("fail " .. tostring(why)))
    return
  end
  if UnitGUID and (guid == "target" or guid == "") then
    guid = UnitGUID("target") or guid
  end
  local ok, why = P.Start(guid, {})
  chat(ok and ("started " .. guid) or ("fail " .. tostring(why)))
end

ensureFrame():Hide()
