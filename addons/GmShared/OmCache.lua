--[[
  GmShared OM table — syncs ExtProxy native cache ↔ Lua.

  Native side (ObjMgrPump) publishes a generation-stamped snapshot.
  This module:
    • pumps / syncs at a controlled interval
    • rehydrates rows in-place by GUID (stable table refs for addons)
    • marks missing rows stale briefly, then evicts
    • exposes list / byGuid / players / units without re-walking natives

  Contract with ExtProxy PushUnit (14 values):
    guid, x, y, z, dist, hp, maxhp, level, faction, typemask, tguid, entry, dyn, uflags
]]

GmShared = GmShared or {}

local TYPE_UNIT   = 0x08
local TYPE_PLAYER = 0x10
local TYPE_GO     = 0x20
local TYPE_CORPSE = 0x80

local function bitHas(mask, flag)
  mask = tonumber(mask) or 0
  flag = tonumber(flag) or 0
  if flag == 0 then return false end
  if bit and bit.band then return bit.band(mask, flag) ~= 0 end
  return math.floor(mask / flag) % 2 == 1
end

local function normGuid(g)
  if not g then return nil end
  g = tostring(g)
  g = string.gsub(g, "^0[xX]", "")
  return string.upper(g)
end

local OM = {
  byGuid = {},       -- [guid] = row (stable identity across syncs)
  list = {},         -- dense array of live (non-stale) rows this sync
  players = {},
  units = {},
  gameobjects = {},
  gen = 0,           -- Lua sync counter
  nativeGen = 0,     -- ObjMgrCacheGen
  count = 0,
  ageMs = -1,
  lastSyncAt = 0,
  -- Tunables
  minInterval = 0.12,  -- seconds between Lua syncs (native already ≥200ms)
  staleSec = 1.25,     -- keep disappeared GUID this long, then drop
  hardStaleSec = 4.0,  -- absolute max retention if never re-seen
}

GmShared.OM = OM

local function applyRow(row, guid, x, y, z, dist, hp, maxhp, level, faction, typemask, tguid, entry, dyn, uflags, now, nativeGen)
  row.guid = guid
  row.x = tonumber(x) or row.x or 0
  row.y = tonumber(y) or row.y or 0
  row.z = tonumber(z) or row.z or 0
  row.dist = tonumber(dist) or row.dist or 999
  row.hp = tonumber(hp) or 0
  row.maxhp = tonumber(maxhp) or 0
  row.pct = (row.maxhp > 0) and (row.hp / row.maxhp) or 0
  row.level = tonumber(level) or 0
  row.faction = tonumber(faction) or 0
  row.typeMask = tonumber(typemask) or 0
  row.tguid = tguid and tostring(tguid) or row.tguid
  row.entry = tonumber(entry) or 0
  row.dyn = tonumber(dyn) or 0
  row.uflags = tonumber(uflags) or 0
  row.seenAt = now
  row.gen = nativeGen
  row.stale = false
  row.isPlayer = bitHas(row.typeMask, TYPE_PLAYER)
  row.isUnit = bitHas(row.typeMask, TYPE_UNIT) or row.isPlayer
  row.isGo = bitHas(row.typeMask, TYPE_GO)
  row.isCorpse = bitHas(row.typeMask, TYPE_CORPSE)
  return row
end

--- Pump native OM + rehydrate Lua tables. Returns gen, liveCount, ageMs.
-- force=true → ObjMgrInvalidate + immediate pump.
function GmShared.OmSync(force)
  local now = GetTime and GetTime() or 0
  if not force and OM.lastSyncAt > 0 and (now - OM.lastSyncAt) < (OM.minInterval or 0.12) then
    return OM.gen, OM.count, OM.ageMs
  end

  local nativeGen, n, ageMs = 0, 0, -1
  if type(GmObjectSync) == "function" then
    local ok, a, b, c = pcall(GmObjectSync, force and 1 or 0)
    if ok then
      nativeGen = tonumber(a) or 0
      n = tonumber(b) or 0
      ageMs = tonumber(c) or -1
    end
  else
    if type(GmObjectPump) == "function" then
      pcall(GmObjectPump, force and 1 or 0)
    end
    if type(GmObjectGen) == "function" then
      nativeGen = tonumber(GmObjectGen()) or 0
    end
    if type(GmObjectCount) == "function" then
      n = tonumber(GmObjectCount()) or 0
    end
    if type(GmObjectCacheAge) == "function" then
      ageMs = tonumber(GmObjectCacheAge()) or -1
    end
  end

  -- Same native generation and we already built tables → only age-check eviction
  if not force and nativeGen > 0 and nativeGen == OM.nativeGen and OM.count > 0
      and type(GmObjectInfo) == "function" then
    -- Still refresh positions if age is fresh? Skip full rebuild for perf.
    -- Evict anything past hard stale.
    local dropped = false
    for guid, row in pairs(OM.byGuid) do
      if row.stale and (now - (row.seenAt or 0)) > (OM.hardStaleSec or 4) then
        OM.byGuid[guid] = nil
        dropped = true
      end
    end
    if dropped then
      -- rebuild derived lists from byGuid live rows
      local list, players, units, gos = {}, {}, {}, {}
      for _, row in pairs(OM.byGuid) do
        if not row.stale then
          list[#list + 1] = row
          if row.isPlayer then players[#players + 1] = row end
          if row.isUnit then units[#units + 1] = row end
          if row.isGo then gos[#gos + 1] = row end
        end
      end
      OM.list, OM.players, OM.units, OM.gameobjects = list, players, units, gos
      OM.count = #list
      OM.gen = OM.gen + 1
    end
    OM.ageMs = ageMs
    OM.lastSyncAt = now
    return OM.gen, OM.count, OM.ageMs
  end

  if type(GmObjectInfo) ~= "function" then
    OM.lastSyncAt = now
    return OM.gen, 0, ageMs
  end

  local seen = {}
  local list, players, units, gos = {}, {}, {}, {}

  for i = 1, n do
    local g, x, y, z, dist, hp, maxhp, level, faction, typemask, tguid, entry, dyn, uflags =
      GmObjectInfo(i)
    local guid = normGuid(g)
    if guid and guid ~= "" and guid ~= "0000000000000000" then
      seen[guid] = true
      local row = OM.byGuid[guid]
      if not row then
        row = {}
        OM.byGuid[guid] = row
      end
      applyRow(row, guid, x, y, z, dist, hp, maxhp, level, faction, typemask, tguid, entry, dyn, uflags, now, nativeGen)
      list[#list + 1] = row
      if row.isPlayer then players[#players + 1] = row end
      if row.isUnit then units[#units + 1] = row end
      if row.isGo then gos[#gos + 1] = row end
    end
  end

  -- Mark missing as stale; evict after staleSec / hardStaleSec
  local staleSec = OM.staleSec or 1.25
  local hardSec = OM.hardStaleSec or 4.0
  for guid, row in pairs(OM.byGuid) do
    if not seen[guid] then
      local age = now - (row.seenAt or 0)
      if age > hardSec or (row.stale and age > staleSec) then
        OM.byGuid[guid] = nil
      else
        row.stale = true
      end
    end
  end

  OM.list = list
  OM.players = players
  OM.units = units
  OM.gameobjects = gos
  OM.count = #list
  OM.nativeGen = nativeGen
  OM.ageMs = ageMs
  OM.gen = OM.gen + 1
  OM.lastSyncAt = now
  return OM.gen, OM.count, OM.ageMs
end

function GmShared.OmGet(guid)
  guid = normGuid(guid)
  if not guid then return nil end
  local row = OM.byGuid[guid]
  if row and not row.stale then return row end
  return row -- may be stale; caller can check .stale
end

function GmShared.OmList(includeStale)
  if includeStale then
    local out = {}
    for _, row in pairs(OM.byGuid) do out[#out + 1] = row end
    return out
  end
  return OM.list
end

function GmShared.OmPlayers(includeStale)
  if not includeStale then return OM.players end
  local out = {}
  for _, row in pairs(OM.byGuid) do
    if row.isPlayer then out[#out + 1] = row end
  end
  return out
end

function GmShared.OmUnits(includeStale)
  if not includeStale then return OM.units end
  local out = {}
  for _, row in pairs(OM.byGuid) do
    if row.isUnit then out[#out + 1] = row end
  end
  return out
end

function GmShared.OmStats()
  return OM.gen, OM.nativeGen, OM.count, OM.ageMs, OM.lastSyncAt
end

--- Prefer OmSync-backed XYZ; falls back to GmObjectByGuid / prior OmXyz contract.
function GmShared.OmXyzCached(guid)
  guid = normGuid(guid)
  if not guid then return nil end
  local row = OM.byGuid[guid]
  if row and row.x and row.y and not (math.abs(row.x) < 0.01 and math.abs(row.y) < 0.01) then
    return row.x, row.y, row.z or 0
  end
  return GmShared.OmXyz(guid)
end

-- Back-compat: GetLocalObjects rebuilds from synced table when possible.
local _oldGetLocal = GmShared.GetLocalObjects
function GmShared.GetLocalObjects()
  if type(GmObjectCount) == "function" then
    GmShared.OmSync(false)
    local out = {}
    for i = 1, #OM.list do
      local r = OM.list[i]
      out[i] = {
        guid = r.guid, entry = r.entry or 0,
        x = r.x, y = r.y, z = r.z, dist = r.dist,
        hp = r.hp, maxhp = r.maxhp, level = r.level,
        faction = r.faction, typeMask = r.typeMask, srcInstance = 0,
      }
    end
    return out
  end
  if _oldGetLocal then return _oldGetLocal() end
  return {}
end
