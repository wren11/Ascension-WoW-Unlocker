--[[
  GmCmds — macroable slash commands for targeting, loot TP, whispers.
    /tpface [gap]              owned by GmTeleport when loaded; fallback here if absent
    /target_lowest_currenthp   lowest-HP enemy PLAYER
    /target_lowest_monster     lowest-HP attackable NPC (not pet/critter)
    /cleartarget
    /lootnearest [radius]
    /lootall [radius]          TP+loot every lootable in OM range
    /msg "Name" "text..."      whisper once-ever per name (SavedVariables)
]]

GmCmdsDB = GmCmdsDB or {}
GmCmds = GmCmds or {}

local ADDON = "GmCmds"
local VERSION = "1.1.3"
GmCmds.VERSION = VERSION

local TYPE_UNIT = 0x08
local TYPE_PLAYER = 0x10
local TYPE_CORPSE = 0x80
local DYN_LOOTABLE = 0x01
local DYN_DEAD = 0x20

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cffc39bd3[GmCmds]|r " .. tostring(msg))
  end
end

local function clearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmHwEvent) == "function" then pcall(GmHwEvent, 1) end
end

local function bitHas(mask, flag)
  mask = tonumber(mask) or 0
  flag = tonumber(flag) or 0
  if bit and bit.band then return bit.band(mask, flag) ~= 0 end
  return math.floor(mask / flag) % 2 == 1
end

local function isFinite(n)
  return type(n) == "number" and n == n and n > -1e30 and n < 1e30
end

local function normGuid(g)
  if not g then return nil end
  return tostring(g):gsub("^0[xX]", ""):lower()
end

local function pumpOm()
  if type(GmObjectPump) == "function" then pcall(GmObjectPump, 0) end
end

--- Prefer canonical GmShared.OmXyz (PushUnit r[3..5]); no local ByGuid pack duplicate.
local function omXyz(guid)
  if not guid then return nil end
  if type(GmShared) == "table" and type(GmShared.OmXyz) == "function" then
    local x, y, z = GmShared.OmXyz(guid)
    if isFinite(x) and isFinite(y) then return x, y, (isFinite(z) and z) or 0 end
  end
  return nil
end

local function xyzFromOmScan(want)
  if not want or type(GmObjectCount) ~= "function" or type(GmObjectInfo) ~= "function" then
    return nil
  end
  local n = GmObjectCount() or 0
  for i = 1, n do
    local g, x, y, z = GmObjectInfo(i)
    x, y, z = tonumber(x), tonumber(y), tonumber(z)
    if g and normGuid(g) == want and isFinite(x) and isFinite(y) then
      return x, y, (isFinite(z) and z) or 0
    end
  end
  return nil
end

--- Raw-first hop (LoadEx often rejects offset / sky-ish pins).
local function teleportRawFirst(x, y, z, o, flags, lockMs)
  if not (isFinite(x) and isFinite(y)) then return false end
  if not isFinite(z) then z = 0 end
  flags = flags or 3
  lockMs = lockMs or 2000
  o = o or 0
  local sent = nil
  if type(GmTeleportRaw) == "function" then
    sent = GmTeleportRaw(x, y, z, o, flags, lockMs)
  end
  if (not sent or sent == 0) and type(GmTeleport) == "function" then
    sent = GmTeleport(x, y, z, o, flags, lockMs)
  end
  return sent and sent ~= 0
end

local function gmTeleportOwnsTpFace()
  if type(GmTeleport_TpFace) == "function" then return true end
  if type(IsAddOnLoaded) == "function" and IsAddOnLoaded("GmTeleport") then return true end
  return false
end

local function defaults()
  local d = GmCmdsDB
  d.whispered = d.whispered or {}
  d.lootRadius = tonumber(d.lootRadius) or 60
  d.targetRadius = tonumber(d.targetRadius) or 80
  d.lootGap = tonumber(d.lootGap) or 0.35
  return d
end

local function nameKey(name)
  if not name or name == "" then return nil end
  return string.lower((tostring(name):gsub("%s+", "")))
end

---------------------------------------------------------------------------
-- Targeting
---------------------------------------------------------------------------

local function isJunkCreature()
  if UnitCreatureType then
    local ct = UnitCreatureType("target")
    if ct then
      local l = string.lower(ct)
      if l == "critter" or l == "non-combat pet" or l == "wild pet" then return true end
    end
  end
  if UnitPlayerControlled and UnitPlayerControlled("target")
      and not (UnitIsPlayer and UnitIsPlayer("target")) then
    return true
  end
  local lvl = UnitLevel and UnitLevel("target")
  if lvl and lvl == 1 then return true end
  return false
end

function GmCmds.ClearTarget()
  clearTaint()
  if type(GmClearTarget) == "function" then
    pcall(GmClearTarget)
  elseif ClearTarget then
    pcall(ClearTarget)
  end
  chat("cleartarget")
  return true
end

--- Lowest HP enemy player in OM radius.
function GmCmds.TargetLowestPlayer(radius)
  radius = tonumber(radius) or defaults().targetRadius
  clearTaint()
  pumpOm()
  if type(GmObjectCount) ~= "function" then
    chat("|cffff5555OM missing|r")
    return false
  end
  local me = UnitGUID and normGuid(UnitGUID("player"))
  local n = GmObjectCount() or 0
  local bestG, bestFrac, bestDist = nil, 2, 1e9
  for i = 1, n do
    local g, _, _, _, dist, hp, maxhp, _, _, typemask, _, _, dyn = GmObjectInfo(i)
    if g and dist and dist <= radius and hp and hp > 0
        and bitHas(typemask, TYPE_PLAYER)
        and not bitHas(dyn, DYN_DEAD) then
      local ng = normGuid(g)
      if ng and ng ~= me then
        local frac = (maxhp and maxhp > 0) and (hp / maxhp) or 1
        if frac < bestFrac - 0.0001 or (math.abs(frac - bestFrac) < 0.0001 and dist < bestDist) then
          bestG, bestFrac, bestDist = ng, frac, dist
        end
      end
    end
  end
  if not bestG or type(GmTargetGuid) ~= "function" then
    chat("|cffff5555no enemy player|r")
    return false
  end
  pcall(GmTargetGuid, bestG)
  if UnitExists("target") and UnitIsPlayer and UnitIsPlayer("target") then
    chat(string.format("lowest HP player → |cffffd200%s|r (%.0f%% · %.0fyd)",
      UnitName("target") or "?", bestFrac * 100, bestDist))
    return true
  end
  chat("|cffff5555target failed|r")
  return false
end

--- Lowest HP attackable monster (not player / pet / critter).
function GmCmds.TargetLowestMonster(radius)
  radius = tonumber(radius) or defaults().targetRadius
  clearTaint()
  pumpOm()
  if type(GmObjectCount) ~= "function" then
    chat("|cffff5555OM missing|r")
    return false
  end
  local me = UnitGUID and normGuid(UnitGUID("player"))
  local n = GmObjectCount() or 0
  local bestG, bestFrac, bestDist = nil, 2, 1e9
  for i = 1, n do
    local g, _, _, _, dist, hp, maxhp, level, _, typemask, _, _, dyn = GmObjectInfo(i)
    if g and dist and dist <= radius and hp and hp > 0
        and bitHas(typemask, TYPE_UNIT)
        and not bitHas(typemask, TYPE_PLAYER)
        and not bitHas(typemask, TYPE_CORPSE)
        and not bitHas(dyn, DYN_DEAD)
        and not bitHas(dyn, DYN_LOOTABLE) then
      if not (level and tonumber(level) == 1) then
        local ng = normGuid(g)
        if ng and ng ~= me then
          local frac = (maxhp and maxhp > 0) and (hp / maxhp) or 1
          if frac < bestFrac - 0.0001 or (math.abs(frac - bestFrac) < 0.0001 and dist < bestDist) then
            bestG, bestFrac, bestDist = ng, frac, dist
          end
        end
      end
    end
  end
  if not bestG or type(GmTargetGuid) ~= "function" then
    chat("|cffff5555no monster|r")
    return false
  end
  pcall(GmTargetGuid, bestG)
  if UnitExists("target") and not (UnitIsPlayer and UnitIsPlayer("target")) then
    if isJunkCreature() then
      GmCmds.ClearTarget()
      chat("|cffff5555rejected pet/critter|r")
      return false
    end
    if UnitCanAttack and not UnitCanAttack("player", "target") then
      GmCmds.ClearTarget()
      chat("|cffff5555not attackable|r")
      return false
    end
    chat(string.format("lowest HP mob → |cffffd200%s|r (%.0f%% · %.0fyd)",
      UnitName("target") or "?", bestFrac * 100, bestDist))
    return true
  end
  chat("|cffff5555target failed|r")
  return false
end

---------------------------------------------------------------------------
-- Loot
---------------------------------------------------------------------------

local lootQueue = {}
local lootDriver = nil

local function tpToGuid(guid, x, y, z)
  clearTaint()
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  pumpOm()
  if not (isFinite(x) and isFinite(y)) then
    x, y, z = omXyz(guid)
  end
  if not (isFinite(x) and isFinite(y)) then
    x, y, z = xyzFromOmScan(normGuid(guid))
  end
  if not isFinite(x) then return false end
  if not isFinite(z) then z = 0 end
  if type(GmNavZ) == "function" then
    local map = (type(GmMapId) == "function" and GmMapId()) or 0
    local gz = GmNavZ(x, y, map, z)
    if gz and gz > -99999 then z = gz + 0.4 end
  end
  local ok = teleportRawFirst(x, y, z, 0, 3, 400)
  -- ClientSync STABLE (x,y,z,o,quiet) — pin client before loot interact.
  if ok and type(GmTeleport_ClientSync) == "function" then
    pcall(GmTeleport_ClientSync, x, y, z, 0, true)
  end
  return ok
end

local function lootGuid(guid)
  clearTaint()
  if type(GmLootOne) == "function" then
    pcall(GmLootOne, guid)
  elseif type(GmLootAll) == "function" then
    pcall(GmLootAll, guid)
  elseif type(GmLoot) == "function" then
    pcall(GmLoot, guid)
  elseif type(GmLootOpen) == "function" then
    pcall(GmLootOpen, guid)
    if type(GmLootTake) == "function" then pcall(GmLootTake, guid) end
  end
  if type(GmLootMoney) == "function" then pcall(GmLootMoney) end
end

function GmCmds.LootNearest(radius)
  radius = tonumber(radius) or defaults().lootRadius
  clearTaint()
  pumpOm()
  local g = nil
  if type(GmNearestLootable) == "function" then
    g = GmNearestLootable(radius, 1)
  end
  if (not g or g == "") and type(GmLootNearest) == "function" then
    local n = GmLootNearest(radius)
    chat(string.format("lootnearest native → %s", tostring(n)))
    return true
  end
  if not g or g == "" then
    chat("|cffff5555no loot nearby|r")
    return false
  end
  g = normGuid(g)
  tpToGuid(g)
  if type(GmTargetGuid) == "function" then pcall(GmTargetGuid, g) end
  lootGuid(g)
  chat("lootnearest → " .. tostring(g))
  return true
end

local function collectLootables(radius)
  local list = {}
  pumpOm()
  -- ExtProxy: GmLootableCount(radius) + GmLootableGuid(i) are 1-based.
  if type(GmLootableCount) == "function" and type(GmLootableGuid) == "function" then
    local n = GmLootableCount(radius) or 0
    for i = 1, n do
      local g = GmLootableGuid(i)
      if g and g ~= "" then
        local ng = normGuid(g)
        local x, y, z = omXyz(ng)
        list[#list + 1] = { guid = ng, x = x, y = y, z = z }
      end
    end
  end
  if #list == 0 and type(GmObjectCount) == "function" then
    local n = GmObjectCount() or 0
    for i = 1, n do
      local g, x, y, z, dist, _, _, _, _, _, _, _, dyn = GmObjectInfo(i)
      x, y, z, dist = tonumber(x), tonumber(y), tonumber(z), tonumber(dist)
      if g and isFinite(dist) and dist <= radius and bitHas(dyn, DYN_LOOTABLE)
          and isFinite(x) and isFinite(y) then
        list[#list + 1] = {
          guid = normGuid(g), x = x, y = y,
          z = (isFinite(z) and z) or 0, dist = dist,
        }
      end
    end
    table.sort(list, function(a, b) return (a.dist or 0) < (b.dist or 0) end)
  end
  return list
end

function GmCmds.LootAll(radius)
  radius = tonumber(radius) or defaults().lootRadius
  local list = collectLootables(radius)
  if #list == 0 then
    chat("|cffff5555lootall: nothing in range|r")
    return false
  end
  lootQueue = list
  chat(string.format("|cff2ecc71lootall|r queue %d (TP+loot)", #list))
  if not lootDriver then
    lootDriver = CreateFrame("Frame")
  end
  local gap = defaults().lootGap
  local acc = 0
  local phase = "tp" -- tp → loot → next
  local idx = 1
  lootDriver:SetScript("OnUpdate", function(self, dt)
    acc = acc + (dt or 0)
    if idx > #lootQueue then
      self:SetScript("OnUpdate", nil)
      chat("|cff2ecc71lootall done|r")
      lootQueue = {}
      return
    end
    local row = lootQueue[idx]
    if phase == "tp" then
      tpToGuid(row.guid, row.x, row.y, row.z)
      if type(GmTargetGuid) == "function" then pcall(GmTargetGuid, row.guid) end
      phase = "loot"
      acc = 0
    elseif phase == "loot" and acc >= gap then
      lootGuid(row.guid)
      phase = "wait"
      acc = 0
    elseif phase == "wait" and acc >= gap then
      idx = idx + 1
      phase = "tp"
      acc = 0
    end
  end)
  return true
end

---------------------------------------------------------------------------
-- Whisper (user-supplied text only — once ever per recipient)
---------------------------------------------------------------------------

function GmCmds.HasWhispered(name)
  local k = nameKey(name)
  if not k then return true end
  defaults()
  return GmCmdsDB.whispered[k] and true or false
end

function GmCmds.MarkWhispered(name)
  local k = nameKey(name)
  if not k then return end
  defaults()
  GmCmdsDB.whispered[k] = {
    at = time and time() or 0,
    name = name,
  }
end

--- /msg "Name" "message text"  — refuses if already whispered once.
function GmCmds.Msg(name, text)
  name = (name or ""):match("^%s*(.-)%s*$")
  text = text or ""
  if name == "" or text == "" then
    chat("|cffff5555usage|r /msg \"Name\" \"your message\"")
    return false
  end
  if GmCmds.HasWhispered(name) then
    chat("|cffff8800skip|r already messaged |cffffd200" .. name .. "|r once (chatdb)")
    return false
  end
  clearTaint()
  if not SendChatMessage then
    chat("|cffff5555SendChatMessage missing|r")
    return false
  end
  -- Cap whisper length (WotLK ~255)
  if #text > 250 then text = string.sub(text, 1, 250) end
  local ok, err = pcall(SendChatMessage, text, "WHISPER", nil, name)
  if not ok then
    chat("|cffff5555whisper failed|r " .. tostring(err))
    return false
  end
  GmCmds.MarkWhispered(name)
  chat("|cff2ecc71msg|r → |cffffd200" .. name .. "|r (recorded once-ever)")
  return true
end

local function parseQuoted(msg)
  -- /msg "Name" "rest of message with spaces"
  local a, b = msg:match('^%s*"([^"]+)"%s+"(.+)"%s*$')
  if a then return a, b end
  a, b = msg:match("^%s*'([^']+)'%s+'(.+)'%s*$")
  if a then return a, b end
  -- /msg Name rest without quotes
  a, b = msg:match("^%s*(%S+)%s+(.+)$")
  return a, b
end

---------------------------------------------------------------------------
-- TP + face — prefer GmTeleport_TpFace; local body only if GmTeleport absent
---------------------------------------------------------------------------

function GmCmds.TpFace(gap)
  -- Prefer GmTeleport's full path (unlock + OM pump + Raw-first + sync + face).
  if type(GmTeleport_TpFace) == "function" then
    return GmTeleport_TpFace(gap)
  end
  gap = tonumber(gap) or 5
  if gap < 2 then gap = 2 end
  if gap > 35 then gap = 35 end
  if not (UnitExists and UnitExists("target")) then
    chat("|cffff5555tpface: no target|r")
    return false
  end
  clearTaint()
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  pumpOm()
  local want = UnitGUID and normGuid(UnitGUID("target"))
  local tx, ty, tz = omXyz(want)
  if not tx then tx, ty, tz = xyzFromOmScan(want) end
  if not (isFinite(tx) and isFinite(ty)) then
    chat("|cffff5555tpface: no target XYZ|r")
    return false
  end
  if not isFinite(tz) then tz = 0 end
  local px, py = nil, nil
  if type(GmPlayerXYZ) == "function" then px, py = GmPlayerXYZ()
  elseif type(GmPlayerPose) == "function" then px, py = GmPlayerPose() end
  px, py = tonumber(px), tonumber(py)
  if not isFinite(px) then px, py = tx - gap, ty end
  local ang = math.atan2(py - ty, px - tx)
  local x = tx + math.cos(ang) * gap
  local y = ty + math.sin(ang) * gap
  local z = tz
  if type(GmNavZ) == "function" then
    local map = (type(GmMapId) == "function" and GmMapId()) or 0
    local gz = GmNavZ(x, y, map, z)
    if gz and gz > -99999 then z = gz + 0.5 end
  end
  local o = math.atan2(ty - y, tx - x)
  local ok = teleportRawFirst(x, y, z, o, 3, 2000)
  if not ok then
    -- Last resort: stand on target (gap 0).
    ok = teleportRawFirst(tx, ty, tz, o, 3, 2000)
    if ok then x, y, z = tx, ty, tz end
  end
  if type(GmFaceTarget) == "function" then pcall(GmFaceTarget) end
  if type(GmFaceUnit) == "function" and want then pcall(GmFaceUnit, want) end
  -- ClientSync contract (stable): GmTeleport_ClientSync(x, y, z, o, quiet)
  if ok and type(GmTeleport_ClientSync) == "function" then
    pcall(GmTeleport_ClientSync, x, y, z, o, true)
  end
  if ok then
    chat(string.format("|cff2ecc71tpface|r %s gap=%.0f", UnitName("target") or "?", gap))
    return true
  end
  chat("|cffff5555tpface failed|r — walk once, retry")
  return false
end

---------------------------------------------------------------------------
-- Slash registration
---------------------------------------------------------------------------

local function pushHash(slash, fn)
  if type(hash_SlashCmdList) == "table" then
    hash_SlashCmdList[slash] = fn
  end
end

local function registerAll()
  SLASH_CLEARTARGET1 = "/cleartarget"
  SLASH_CLEARTARGET2 = "/ctar"
  SlashCmdList["CLEARTARGET"] = function() GmCmds.ClearTarget() end
  pushHash("/cleartarget", SlashCmdList["CLEARTARGET"])
  pushHash("/ctar", SlashCmdList["CLEARTARGET"])

  SLASH_TARGETLOWESTCURRENTHP1 = "/target_lowest_currenthp"
  SLASH_TARGETLOWESTCURRENTHP2 = "/tlhp"
  SLASH_TARGETLOWESTCURRENTHP3 = "/targetlowestplayer"
  SlashCmdList["TARGETLOWESTCURRENTHP"] = function(msg)
    GmCmds.TargetLowestPlayer(tonumber((msg or ""):match("([%d%.]+)")))
  end
  pushHash("/target_lowest_currenthp", SlashCmdList["TARGETLOWESTCURRENTHP"])
  pushHash("/tlhp", SlashCmdList["TARGETLOWESTCURRENTHP"])
  pushHash("/targetlowestplayer", SlashCmdList["TARGETLOWESTCURRENTHP"])

  SLASH_TARGETLOWESTMONSTER1 = "/target_lowest_monster"
  SLASH_TARGETLOWESTMONSTER2 = "/tlmob"
  SLASH_TARGETLOWESTMONSTER3 = "/targetlowestmob"
  SlashCmdList["TARGETLOWESTMONSTER"] = function(msg)
    GmCmds.TargetLowestMonster(tonumber((msg or ""):match("([%d%.]+)")))
  end
  pushHash("/target_lowest_monster", SlashCmdList["TARGETLOWESTMONSTER"])
  pushHash("/tlmob", SlashCmdList["TARGETLOWESTMONSTER"])
  pushHash("/targetlowestmob", SlashCmdList["TARGETLOWESTMONSTER"])

  SLASH_LOOTNEAREST1 = "/lootnearest"
  SLASH_LOOTNEAREST2 = "/lnearest"
  SlashCmdList["LOOTNEAREST"] = function(msg)
    GmCmds.LootNearest(tonumber((msg or ""):match("([%d%.]+)")))
  end
  pushHash("/lootnearest", SlashCmdList["LOOTNEAREST"])
  pushHash("/lnearest", SlashCmdList["LOOTNEAREST"])

  SLASH_LOOTALL1 = "/lootall"
  SLASH_LOOTALL2 = "/lall"
  SlashCmdList["LOOTALL"] = function(msg)
    GmCmds.LootAll(tonumber((msg or ""):match("([%d%.]+)")))
  end
  pushHash("/lootall", SlashCmdList["LOOTALL"])
  pushHash("/lall", SlashCmdList["LOOTALL"])

  -- /tpface owned by GmTeleport when loaded (OptionalDeps). Fallback slash only if absent.
  if not gmTeleportOwnsTpFace() then
    SLASH_TPFACE1 = "/tpface"
    SLASH_TPFACE2 = "/tpf"
    SlashCmdList["TPFACE"] = function(msg)
      local gap = tonumber((msg or ""):match("([%d%.]+)"))
      GmCmds.TpFace(gap)
    end
    pushHash("/tpface", SlashCmdList["TPFACE"])
    pushHash("/tpf", SlashCmdList["TPFACE"])
  end

  SLASH_GMMSG1 = "/gmmsg"
  SLASH_GMMSG2 = "/gmw"
  SlashCmdList["GMMSG"] = function(msg)
    local name, text = parseQuoted(msg or "")
    GmCmds.Msg(name, text)
  end
  pushHash("/gmmsg", SlashCmdList["GMMSG"])
  pushHash("/gmw", SlashCmdList["GMMSG"])

  SLASH_GMCMDS1 = "/gmcmds"
  SlashCmdList["GMCMDS"] = function()
    chat("v" .. VERSION .. " macros:")
    chat("/tpface [gap] · /cleartarget · /target_lowest_currenthp · /target_lowest_monster")
    chat("/lootnearest [r] · /lootall [r] · /gmmsg \"Name\" \"text\" (once-ever)")
    if gmTeleportOwnsTpFace() then
      chat("/tpface owned by GmTeleport — macro: |cffffd200/run GmTeleport_TpFace(5)|r")
    else
      chat("reliable macro: |cffffd200/run GmCmds.TpFace(5)|r")
    end
  end
  pushHash("/gmcmds", SlashCmdList["GMCMDS"])

  if type(ChatFrame_ImportAllListsToHash) == "function" then
    pcall(ChatFrame_ImportAllListsToHash)
  end
end

registerAll()

local boot = CreateFrame("Frame")
boot:RegisterEvent("PLAYER_LOGIN")
boot:SetScript("OnEvent", function()
  defaults()
  registerAll()
  chat("v" .. VERSION .. " ready — /gmcmds · macro: /run GmCmds.TpFace(5)")
end)
