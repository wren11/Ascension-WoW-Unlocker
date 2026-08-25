-- GmShared: multi-instance shared world API + instance HUD.

GmShared = GmShared or {}

--- Compact GM HUD scale (independent of UseUIScale). No-op if GmUI is missing.
function GmShared.ScaleHud(frame, mul)
  if not frame then return end
  local ui = _G.GmUI
  if type(ui) == "table" and type(ui.ScaleFrame) == "function" then
    ui.ScaleFrame(frame, mul)
  end
end

local TYPE_PLAYER = 0x10

local function pushRow(guid, entry, x, y, z, facing, hp, maxhp, level, faction, typemask, src)
  return {
    guid = tostring(guid or ""),
    entry = tonumber(entry) or 0,
    x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or 0,
    facing = tonumber(facing) or 0,
    hp = tonumber(hp) or 0, maxhp = tonumber(maxhp) or 0,
    level = tonumber(level) or 0, faction = tonumber(faction) or 0,
    typeMask = tonumber(typemask) or 0,
    srcInstance = tonumber(src) or 0,
  }
end

local function collectIndexed(countFn, objectFn, maxN)
  local n = 0
  if type(countFn) == "function" then
    n = tonumber(countFn()) or 0
  end
  if n < 1 then return {} end
  if maxN and n > maxN then n = maxN end
  local out = {}
  for i = 1, n do
    local guid, entry, x, y, z, facing, hp, maxhp, level, faction, typemask, src =
      objectFn(i)
    if guid then
      out[#out + 1] = pushRow(guid, entry, x, y, z, facing, hp, maxhp, level, faction, typemask, src)
    end
  end
  return out
end

function GmShared.GetLocalObjects()
  if type(GmObjectCount) ~= "function" or type(GmObjectInfo) ~= "function" then
    return {}
  end
  local n = tonumber(GmObjectCount()) or 0
  local out = {}
  for i = 1, n do
    local g, x, y, z, dist, hp, maxhp, level, faction, typemask, tguid, entry =
      GmObjectInfo(i)
    if g then
      out[#out + 1] = {
        guid = tostring(g), entry = tonumber(entry) or 0,
        x = tonumber(x) or 0, y = tonumber(y) or 0, z = tonumber(z) or 0,
        dist = tonumber(dist) or 0,
        hp = tonumber(hp) or 0, maxhp = tonumber(maxhp) or 0,
        level = tonumber(level) or 0, faction = tonumber(faction) or 0,
        typeMask = tonumber(typemask) or 0,
        srcInstance = 0,
      }
    end
  end
  return out
end

function GmShared.GetSharedObjects()
  if type(GmSharedObject) ~= "function" then return {} end
  local countFn = GmSharedCount or GmSharedObjects
  return collectIndexed(countFn, GmSharedObject, 256)
end

function GmShared.GetPlayers()
  if type(GmSharedPlayer) == "function" then
    return collectIndexed(GmSharedPlayers, GmSharedPlayer, 256)
  end
  local all = GmShared.GetSharedObjects()
  local out = {}
  for i = 1, #all do
    local tm = all[i].typeMask or 0
    if math.floor(tm / TYPE_PLAYER) % 2 == 1 then
      out[#out + 1] = all[i]
    end
  end
  return out
end

function GmShared.GetInstance(id)
  id = tonumber(id) or 0
  if type(GmGetInstanceObject) == "function" and type(GmGetInstanceCount) == "function" then
    local n = tonumber(GmGetInstanceCount(id)) or 0
    local out = {}
    for i = 1, n do
      local guid, entry, x, y, z, facing, hp, maxhp, level, faction, typemask, src =
        GmGetInstanceObject(id, i)
      if guid then
        out[#out + 1] = pushRow(guid, entry, x, y, z, facing, hp, maxhp, level, faction, typemask, src)
      end
    end
    return out
  end
  local all = GmShared.GetSharedObjects()
  local out = {}
  for i = 1, #all do
    if (all[i].srcInstance or 0) == id then out[#out + 1] = all[i] end
  end
  return out
end

function GmShared.NearbyPlayers(x, y, r)
  x, y, r = tonumber(x) or 0, tonumber(y) or 0, tonumber(r) or 40
  local r2 = r * r
  local out = {}
  for _, p in ipairs(GmShared.GetPlayers()) do
    local dx, dy = (p.x or 0) - x, (p.y or 0) - y
    if dx * dx + dy * dy <= r2 then out[#out + 1] = p end
  end
  return out
end

function GmShared.ThisInstance()
  if type(GmInstanceInfo) == "function" then
    local id, total, pid = GmInstanceInfo()
    return {
      id = tonumber(id) or 0,
      total = tonumber(total) or 1,
      pid = tonumber(pid) or 0,
    }
  end
  return { id = 0, total = 1, pid = 0 }
end

--- True when GMToolBox has attached 2+ ExtProxy clients (shared header total).
function GmShared.IsMultiInstance()
  local info = GmShared.ThisInstance()
  if (info.total or 1) >= 2 then return true end
  local players = GmShared.GetPlayers()
  local seen = {}
  for i = 1, #players do
    local s = players[i].srcInstance or 0
    if s > 0 then seen[s] = true end
  end
  local n = 0
  for _ in pairs(seen) do n = n + 1 end
  return n >= 2
end

function GmShared.NativesReady()
  return type(GmTeleport) == "function"
    or type(GmTeleportRaw) == "function"
    or type(GmObjectCount) == "function"
end

--[[
  OM faction field = UNIT_FIELD_FACTIONTEMPLATE (race/template id), NOT Alliance/Horde.
  Comparing raw equality treats same-team Night Elf vs Human as "enemies".
  Map template → team; unknown → nil (never treat as enemy).
]]
GmShared.TEAM_ALLIANCE = 1
GmShared.TEAM_HORDE = 2

-- WotLK player race FactionTemplate IDs + team reputation factions
local FACTION_TEAM = {
  [1] = 1, [3] = 1, [4] = 1, [115] = 1, [1629] = 1, -- Human Dwarf NE Gnome Draenei
  [2] = 2, [5] = 2, [6] = 2, [116] = 2, [1610] = 2, -- Orc Undead Tauren Troll BE
  [469] = 1, -- Alliance
  [67] = 2,  -- Horde
  [891] = 1, -- Alliance Forces (BG)
  [892] = 2, -- Horde Forces (BG)
}

function GmShared.FactionTeam(factionId)
  factionId = tonumber(factionId)
  if not factionId or factionId == 0 then return nil end
  return FACTION_TEAM[factionId]
end

function GmShared.MyFactionTeam()
  if UnitFactionGroup then
    local g = UnitFactionGroup("player")
    if g == "Alliance" then return GmShared.TEAM_ALLIANCE end
    if g == "Horde" then return GmShared.TEAM_HORDE end
  end
  return nil
end

--- True only when OM faction template maps to the opposite team.
--- Unknown / same team / nil → false (do not target).
function GmShared.IsOmEnemyPlayerFaction(factionId)
  local mine = GmShared.MyFactionTeam()
  local theirs = GmShared.FactionTeam(factionId)
  if not mine or not theirs then return false end
  return mine ~= theirs
end

function GmShared.IsOmFriendPlayerFaction(factionId)
  local mine = GmShared.MyFactionTeam()
  local theirs = GmShared.FactionTeam(factionId)
  if not mine or not theirs then return false end
  return mine == theirs
end

--- Unit-token checks after GmTargetGuid — reject friendlies / party / same faction.
function GmShared.UnitIsEnemyPlayer(unit)
  unit = unit or "target"
  if not UnitExists or not UnitExists(unit) then return false end
  if UnitIsUnit and UnitIsUnit(unit, "player") then return false end
  if UnitIsDeadOrGhost and UnitIsDeadOrGhost(unit) then return false end
  if UnitIsPlayer and not UnitIsPlayer(unit) then return false end
  if UnitInParty and UnitInParty(unit) then return false end
  if UnitInRaid and UnitInRaid(unit) then return false end
  if UnitIsFriend and UnitIsFriend("player", unit) then return false end
  if UnitFactionGroup then
    local a, b = UnitFactionGroup("player"), UnitFactionGroup(unit)
    if a and b and a ~= "" and b ~= "" and a == b then return false end
  end
  if UnitCanAttack and not UnitCanAttack("player", unit) then return false end
  return true
end

--[[
  GmShared.OmXyz(guid) — CONSUMER CONTRACT (STABLE)

  Prefer this over local `pcall(GmObjectByGuid)` packing. ExtProxy PushUnit
  (ProxyMain.c) returns 14 values: guid, x, y, z, dist, hp, maxhp, level,
  faction, typemask, tguid, entry, dyn, uflags.
  With `{ pcall(...) }`: r[1]=ok, r[2]=guid, **r[3]=x, r[4]=y, r[5]=z**.
  Using r[2..4] as xyz does tonumber(guidHex)→0 → TP to world origin.

  Usage:
    local x, y, z = GmShared.OmXyz(guid)
    if not x then -- miss / no native / junk
    else -- hop / face / loot at x,y,z
    end

  Returns: x, y, z  (three numbers)  OR  nil (single nil — no partial).
  Guards: missing native, pcall fail, non-numeric, NaN, ±1e30, near-origin
  (|x|<0.01 and |y|<0.01 — usually a bad guid→number fold). z defaults to 0
  when absent/NaN/±1e30 but x,y valid.

  Callers MUST keep a local fallback when GmShared / OmXyz may be absent
  (OptionalDeps). Do not change arity without bumping + broadcasting.
]]
function GmShared.OmXyz(guid)
  if guid == nil or guid == "" then return nil end
  if type(GmObjectByGuid) ~= "function" then return nil end
  local r = { pcall(GmObjectByGuid, guid) }
  if not r[1] then return nil end
  local x, y, z = tonumber(r[3]), tonumber(r[4]), tonumber(r[5])
  if type(x) ~= "number" or type(y) ~= "number" then return nil end
  if x ~= x or y ~= y then return nil end -- NaN
  if x <= -1e30 or x >= 1e30 or y <= -1e30 or y >= 1e30 then return nil end
  -- Reject near-origin packs that usually mean a bad guid→number fold.
  if math.abs(x) < 0.01 and math.abs(y) < 0.01 then return nil end
  -- Insane/NaN z → 0 (contract: never fail solely on z when x,y valid).
  if type(z) ~= "number" or z ~= z or z <= -1e30 or z >= 1e30 then z = 0 end
  return x, y, z
end

--- Live player pose from the object manager (GmPlayerPose). Never UnitPosition.
-- Returns x, y, z, o, map  or nil.
function GmShared.OmPlayer()
  if type(GmPlayerPose) == "function" then
    local ok, x, y, z, o, map = pcall(GmPlayerPose)
    if ok and type(x) == "number" and type(y) == "number" then
      if x == x and y == y and not (math.abs(x) < 0.01 and math.abs(y) < 0.01) then
        if type(z) ~= "number" or z ~= z then z = 0 end
        return x, y, z, tonumber(o) or 0, tonumber(map) or 0
      end
    end
  end
  if type(GmPlayerXYZ) == "function" then
    local ok, x, y, z, o = pcall(GmPlayerXYZ)
    if ok and type(x) == "number" and type(y) == "number"
        and not (math.abs(x) < 0.01 and math.abs(y) < 0.01) then
      local map = 0
      if type(GmMapId) == "function" then
        local okm, m = pcall(GmMapId)
        if okm then map = tonumber(m) or 0 end
      end
      return x, y, z or 0, o or 0, map
    end
  end
  return nil
end

--- Force-pump then live ByGuid. Returns x, y, z, dist, typemask, dyn, entry or nil.
function GmShared.OmLive(guid)
  if guid == nil or guid == "" then return nil end
  if type(GmObjectPump) == "function" then pcall(GmObjectPump, 1) end
  if type(GmObjectByGuid) ~= "function" then return nil end
  local r = { pcall(GmObjectByGuid, guid) }
  if not r[1] then return nil end
  local x, y, z = tonumber(r[3]), tonumber(r[4]), tonumber(r[5])
  if type(x) ~= "number" or type(y) ~= "number" then return nil end
  if x ~= x or y ~= y then return nil end
  if math.abs(x) < 0.01 and math.abs(y) < 0.01 then return nil end
  if type(z) ~= "number" or z ~= z then z = 0 end
  return x, y, z, tonumber(r[6]), tonumber(r[11]), tonumber(r[14]), tonumber(r[13])
end

function GmShared.OmDist3(ax, ay, az, bx, by, bz)
  ax, ay, az = tonumber(ax), tonumber(ay), tonumber(az) or 0
  bx, by, bz = tonumber(bx), tonumber(by), tonumber(bz) or 0
  if not (ax and ay and bx and by) then return nil end
  local dx, dy, dz = ax - bx, ay - by, az - bz
  return math.sqrt(dx * dx + dy * dy + dz * dz)
end

function GmShared.BagsFree()
  local free = 0
  for bag = 0, 4 do
    local n = GetContainerNumFreeSlots and GetContainerNumFreeSlots(bag)
    if n then free = free + n end
  end
  return free
end

--- Sorted itemId x count fingerprint. Change is loot/take proof; timers are not.
function GmShared.BagFingerprint()
  local parts = {}
  for bag = 0, 4 do
    local slots = GetContainerNumSlots and GetContainerNumSlots(bag) or 0
    for slot = 1, slots do
      local link = GetContainerItemLink and GetContainerItemLink(bag, slot)
      if type(link) == "string" then
        local id = tonumber(link:match("item:(%d+)")) or 0
        local count = 1
        if GetContainerItemInfo then
          local _, c = GetContainerItemInfo(bag, slot)
          count = tonumber(c) or 1
        end
        parts[#parts + 1] = string.format("%d:%d", id, count)
      end
    end
  end
  table.sort(parts)
  return table.concat(parts, ",")
end

function GmShared.BagHasItem(itemId)
  itemId = tonumber(itemId)
  if not itemId then return false, 0 end
  local n = 0
  for bag = 0, 4 do
    local slots = GetContainerNumSlots and GetContainerNumSlots(bag) or 0
    for slot = 1, slots do
      local link = GetContainerItemLink and GetContainerItemLink(bag, slot)
      if type(link) == "string" and tonumber(link:match("item:(%d+)")) == itemId then
        local count = 1
        if GetContainerItemInfo then
          local _, c = GetContainerItemInfo(bag, slot)
          count = tonumber(c) or 1
        end
        n = n + count
      end
    end
  end
  return n > 0, n
end

local TP_NOFALL = 0x04
local TP_HOVER = 0x02
local TP_WATER = 0x01
local tpGuardPrev = nil
local tpGuardRef = 0

local function borFlags(a, b)
  a, b = tonumber(a) or 0, tonumber(b) or 0
  if bit and bit.bor then return bit.bor(a, b) end
  local r, v = 0, 1
  local i
  for i = 1, 12 do
    if (math.floor(a / v) % 2) + (math.floor(b / v) % 2) > 0 then r = r + v end
    v = v * 2
  end
  return r
end

local function applyTpSurvive()
  local bits = TP_NOFALL + TP_HOVER + TP_WATER
  local cur = 0
  if type(GmGetHacks) == "function" then
    local h = GmGetHacks()
    cur = tonumber(h) or 0
  end
  if type(GmSetHacks) == "function" then
    pcall(GmSetHacks, borFlags(cur, bits))
  end
  if type(GmNoFall) == "function" then pcall(GmNoFall, 1) end
  if type(GmWaterwalk) == "function" then pcall(GmWaterwalk, 1) end
  if type(GmHackBit) == "function" then
    pcall(GmHackBit, 4, 1)
    pcall(GmHackBit, 2, 1)
    pcall(GmHackBit, 1, 1)
  end
end

--- Reassert NoFall+Hover+Waterwalk on this hop (no save/restore).
function GmShared.TpGuardPulse()
  applyTpSurvive()
  return true
end

--- Session guard. true = acquire (refcount), false = release. Restores only at 0.
function GmShared.TpGuard(on)
  if on then
    if tpGuardRef < 1 then
      if type(GmGetHacks) == "function" then
        local h, fly = GmGetHacks()
        tpGuardPrev = { h = tonumber(h) or 0, fly = tonumber(fly) or 0 }
      else
        tpGuardPrev = { h = 0, fly = 0 }
      end
    end
    tpGuardRef = (tpGuardRef or 0) + 1
    applyTpSurvive()
    return true
  end
  tpGuardRef = math.max(0, (tpGuardRef or 0) - 1)
  if tpGuardRef == 0 and tpGuardPrev and type(GmSetHacks) == "function" then
    pcall(GmSetHacks, tpGuardPrev.h or 0)
    if type(GmFlyhack) == "function" then pcall(GmFlyhack, tpGuardPrev.fly or 0) end
    tpGuardPrev = nil
  end
  return true
end

local NAV_MISS = -99999
local NAV_LIFT = 0.45
local TP_SKIP_GROUND_JUMP = 3 -- keep requested Z (already nav-snapped)

--- Travel teleport: stand ON the walkable nav sheet. Never Hunt under-object Z.
--- Clears leftover noclip so Explore/map hops cannot fall through the mesh.
--- Hunt/LootProof keep using TeleportKeepZ / their own Raw flags=3 path.
function GmShared.TeleportNav(x, y, z, o, opts)
  opts = type(opts) == "table" and opts or {}
  x, y, z = tonumber(x), tonumber(y), tonumber(z) or 0
  o = tonumber(o) or 0
  if not x or not y then return false, "no xy" end
  local map = tonumber(opts.map)
  if not map and type(GmMapId) == "function" then
    local ok, m = pcall(GmMapId)
    if ok then map = tonumber(m) or 0 end
  end
  map = map or 0
  local lockMs = tonumber(opts.lockMs) or 2000
  local floatYd = tonumber(opts.floatYd) or 0
  if type(GmNoclip) == "function" then pcall(GmNoclip, 0) end
  GmShared.TpGuardPulse()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  local gz
  if type(GmNavZ) == "function" then
    local ok, v = pcall(GmNavZ, x, y, map, z)
    if ok then gz = tonumber(v) end
  end
  local destZ = z + NAV_LIFT
  if gz and gz > NAV_MISS then
    -- A lower nav layer (cave / dungeon floor under the requested Z) is what
    -- buries the player. Only snap to nav when it is at or above the request.
    if gz >= (z - 4) then
      destZ = gz + NAV_LIFT
      if floatYd > 1 then destZ = gz + floatYd end
    end
  end
  local ok = false
  if type(GmTeleportRaw) == "function" then
    local pcallOk, r = pcall(GmTeleportRaw, x, y, destZ, o, TP_SKIP_GROUND_JUMP, lockMs)
    ok = pcallOk and r ~= nil and r ~= false and r ~= 0
  end
  if not ok and type(GmTeleport) == "function" then
    local pcallOk, r = pcall(GmTeleport, x, y, destZ, o, TP_SKIP_GROUND_JUMP, lockMs)
    ok = pcallOk and r ~= nil and r ~= false and r ~= 0
  end
  if not ok then return false, destZ end
  -- ClientSync is opt-in. Default off: the 1s Raw reassert DESTROY_OBJECTs
  -- nearby veins/herbs/chests. Map UI arms ClientSync itself after Nav.
  if opts.clientSync == true and type(GmTeleport_ClientSync) == "function" then
    pcall(GmTeleport_ClientSync, x, y, destZ, o, true)
  elseif type(GmSetFacing) == "function" then
    pcall(GmSetFacing, o)
  end
  return true, destZ
end

--- Exact XYZ (Hunt loot-under / LootProof). Does not snap up to nav, does not clear noclip.
function GmShared.TeleportKeepZ(x, y, z, o, opts)
  opts = type(opts) == "table" and opts or {}
  x, y, z = tonumber(x), tonumber(y), tonumber(z) or 0
  o = tonumber(o) or 0
  if not x or not y then return false end
  local lockMs = tonumber(opts.lockMs) or 2500
  GmShared.TpGuardPulse()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  if type(GmTeleportRaw) == "function" then
    local pcallOk, r = pcall(GmTeleportRaw, x, y, z, o, TP_SKIP_GROUND_JUMP, lockMs)
    if pcallOk and r ~= nil and r ~= false and r ~= 0 then return true end
  end
  if type(GmTeleport) == "function" then
    local pcallOk, r = pcall(GmTeleport, x, y, z, o, TP_SKIP_GROUND_JUMP, lockMs)
    if pcallOk and r ~= nil and r ~= false and r ~= 0 then return true end
  end
  return false
end

--[[ HasDeserter — shared BG-queue gate (A2 / BgAfk / CtfCap).
  Classic spellId 26013 + multi-locale name tokens + GetSpellInfo(26013).
  UnitDebuff layout 3.3.5: index 11 = spellId.
  cache (optional table): { poll=0, untilT=0 } — throttle across callers.
  Returns true while debuff present.
]]
function GmShared.HasDeserter(cache, pollSec)
  pollSec = tonumber(pollSec) or 5
  local now = GetTime and GetTime() or 0
  if type(cache) == "table" then
    if now - (cache.poll or 0) < pollSec then
      return (cache.untilT or 0) > now
    end
    cache.poll = now
    cache.untilT = 0
  end
  if type(UnitDebuff) ~= "function" then return false end
  local function match(name, spellId)
    spellId = tonumber(spellId)
    if spellId == 26013 then return true end
    local l = string.lower(tostring(name or ""))
    if l == "" then return false end
    local tokens = {
      "deserter", "дезертир", "déserteur", "fahnenflucht", "desertor", "deserteur",
    }
    for i = 1, #tokens do
      if string.find(l, tokens[i], 1, true) then return true end
    end
    if type(GetSpellInfo) == "function" then
      local sn = GetSpellInfo(26013)
      if sn and sn ~= "" and string.find(l, string.lower(sn), 1, true) then
        return true
      end
    end
    return false
  end
  for i = 1, 40 do
    local name, _, _, _, _, duration, expirationTime, _, _, _, spellId =
      UnitDebuff("player", i)
    if not name then break end
    if match(name, spellId) then
      local untilT = tonumber(expirationTime) or 0
      if untilT <= now then
        untilT = now + (tonumber(duration) or 900)
      end
      if type(cache) == "table" then cache.untilT = untilT end
      return true
    end
  end
  return false
end

function GmShared.IsPeerPlayer(guid)
  guid = tostring(guid or ""):gsub("^0x", ""):upper()
  if #guid < 12 then return false end
  local my = GmShared.ThisInstance()
  for _, p in ipairs(GmShared.GetPlayers()) do
    local g = tostring(p.guid or ""):gsub("^0x", ""):upper()
    local src = tonumber(p.srcInstance) or 0
    if g == guid and src > 0 and src ~= (my.id or 0) then
      return true, src
    end
  end
  return false
end

--- Players in shared OM without a known srcInstance (or src==0) are open-world strangers.
function GmShared.IsStranger(guid)
  guid = tostring(guid or ""):gsub("^0x", ""):upper()
  if #guid < 12 then return true end
  for _, p in ipairs(GmShared.GetPlayers()) do
    local g = tostring(p.guid or ""):gsub("^0x", ""):upper()
    if g == guid then
      local src = tonumber(p.srcInstance) or 0
      return src < 1, src
    end
  end
  -- Not in shared view → treat as stranger (local OM only).
  return true, 0
end

------------------------------------------------------------------------
-- Instance HUD (top-right): "Instance: N / T"
------------------------------------------------------------------------
local hud
local function ensureHud()
  if hud then return hud end
  hud = CreateFrame("Frame", "GmSharedInstanceHud", UIParent)
  hud:SetWidth(110)
  hud:SetHeight(22)
  hud:SetPoint("TOPRIGHT", UIParent, "TOPRIGHT", -180, -12)
  hud:SetFrameStrata("HIGH")
  hud.bg = hud:CreateTexture(nil, "BACKGROUND")
  hud.bg:SetAllPoints()
  hud.bg:SetTexture(0, 0, 0, 0.55)
  hud.fs = hud:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  hud.fs:SetPoint("CENTER")
  hud.fs:SetText("Instance: ?")
  hud:Hide()
  GmShared.ScaleHud(hud)
  return hud
end

local function refreshHud()
  local h = ensureHud()
  local info = GmShared.ThisInstance()
  local id = info.id or 0
  local total = info.total or 1
  if id < 1 and total < 2 and not GmShared.NativesReady() then
    h:Hide()
    return
  end
  if id < 1 then id = 1 end
  h.fs:SetText(string.format("Instance: %d / %d", id, math.max(total, 1)))
  h:Show()
end

local pump = CreateFrame("Frame")
local acc = 0
pump:SetScript("OnUpdate", function(_, dt)
  acc = acc + (dt or 0)
  if acc < 0.30 then return end
  acc = 0
  refreshHud()
end)

SLASH_GMSHARED1 = "/gmshared"
SlashCmdList.GMSHARED = function(msg)
  msg = string.lower(tostring(msg or ""):gsub("^%s+", ""):gsub("%s+$", ""))
  local info = GmShared.ThisInstance()
  if msg == "players" then
    local p = GmShared.GetPlayers()
    DEFAULT_CHAT_FRAME:AddMessage(string.format(
      "|cff00b4d8[GmShared]|r players=%d (inst %d/%d pid=%d) multi=%s natives=%s",
      #p, info.id, info.total, info.pid,
      tostring(GmShared.IsMultiInstance()), tostring(GmShared.NativesReady())))
    return
  end
  if msg == "omxyz" or msg:find("^omxyz") then
    local guid = msg:match("^omxyz%s+(%S+)") or (UnitGUID and UnitGUID("target"))
    if not guid or guid == "" then
      DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmShared]|r OmXyz — target a unit or /gmshared omxyz <guid>")
      return
    end
    local x, y, z = GmShared.OmXyz(guid)
    if not x then
      DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmShared]|r OmXyz miss for " .. tostring(guid))
    else
      DEFAULT_CHAT_FRAME:AddMessage(string.format(
        "|cff00b4d8[GmShared]|r OmXyz %.2f %.2f %.2f", x, y, z or 0))
    end
    return
  end
  if msg == "deserter" or msg:find("^deserter") then
    local has = GmShared.HasDeserter(nil, 0)
    DEFAULT_CHAT_FRAME:AddMessage(string.format(
      "|cff00b4d8[GmShared]|r HasDeserter=%s (spell 26013 + locale tokens)",
      tostring(has)))
    return
  end
  local all = GmShared.GetSharedObjects()
  DEFAULT_CHAT_FRAME:AddMessage(string.format(
    "|cff00b4d8[GmShared]|r objects=%d inst=%d/%d pid=%d — /gmshared players|omxyz|deserter",
    #all, info.id, info.total, info.pid))
end

local boot = CreateFrame("Frame")
boot:RegisterEvent("PLAYER_LOGIN")
boot:RegisterEvent("PLAYER_ENTERING_WORLD")
boot:SetScript("OnEvent", function()
  refreshHud()
  if GmShared.NativesReady() then
    local info = GmShared.ThisInstance()
    if DEFAULT_CHAT_FRAME and (not boot._said) then
      boot._said = true
      DEFAULT_CHAT_FRAME:AddMessage(string.format(
        "|cff00b4d8[GmShared]|r ready — instance %d/%d · /gmshared · /gminstance · getInstance(\"Name\")",
        info.id > 0 and info.id or 1, math.max(info.total or 1, 1)))
    end
  end
end)
