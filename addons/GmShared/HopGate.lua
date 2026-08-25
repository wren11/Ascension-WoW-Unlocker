-- GmShared.Hop — one travel hop, OM pump, loot/leave gates.
-- Travel: TeleportNav, never ClientSync (reassert hops DESTROY_OBJECT veins/herbs/chests).
-- On-object: TeleportKeepZ at live OM XYZ. Hunt combat under-object stays on Hunt.

GmShared = GmShared or {}
local H = {}
GmShared.Hop = H

local function lootCount()
  if type(GetNumLootItems) == "function" then
    return tonumber(GetNumLootItems()) or 0
  end
  return 0
end

local function lootFrameOpen()
  local f = _G.LootFrame
  if f and f.IsShown and f:IsShown() then return true end
  return false
end

local function playerCasting()
  if type(UnitCastingInfo) == "function" and UnitCastingInfo("player") then return true end
  if type(UnitChannelInfo) == "function" and UnitChannelInfo("player") then return true end
  return false
end

local function nativePump(force)
  if type(GmObjectPump) == "function" then
    pcall(GmObjectPump, force and 1 or 0)
  end
end

function H.Busy()
  if playerCasting() then return true, "cast" end
  if lootCount() > 0 then return true, "loot" end
  if lootFrameOpen() then return true, "lootframe" end
  local LP = _G.GmLootProof
  if LP then
    if LP.Busy and LP.Busy() then return true, "lootproof" end
    if LP.Blocking and LP.Blocking() then return true, "lootproof" end
  end
  return false
end

function H.Unlock()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  if type(GmTeleport_Unlock) == "function" then pcall(GmTeleport_Unlock) end
end

-- force=true: native pump + Lua OM rebuild (after a hop / before loot).
-- force=false: native pump only (scan ticks; no full table rebuild hitch).
function H.PumpOM(force)
  if force then
    if type(GmShared.OmSync) == "function" then
      pcall(GmShared.OmSync, true)
    else
      nativePump(true)
    end
    return
  end
  nativePump(false)
end

function H.AfterTravel(x, y, z)
  H.Unlock()
  H.PumpOM(true)
  if type(x) == "number" and type(y) == "number" and type(GmShared.OmPlayer) == "function" then
    local px, py = GmShared.OmPlayer()
    if px then
      local dx, dy = px - x, py - y
      return (dx * dx + dy * dy) <= 144
    end
  end
  return true
end

function H.GoLive(guid)
  if not guid or guid == "" then return nil end
  if type(GmShared.OmLive) == "function" then
    return GmShared.OmLive(guid)
  end
  H.PumpOM(true)
  if type(GmShared.OmXyz) == "function" then
    return GmShared.OmXyz(guid)
  end
  return nil
end

function H.CanLoot(guid)
  local busy, why = H.Busy()
  if busy then return false, why end
  H.Unlock()
  if guid and guid ~= "" then
    local x = H.GoLive(guid)
    if not x then return false, "gone" end
  else
    H.PumpOM(true)
  end
  return true
end

function H.CanLeave()
  local busy, why = H.Busy()
  if busy then return false, why end
  H.Unlock()
  return true
end

function H.Travel(x, y, z, o, opts)
  opts = type(opts) == "table" and opts or {}
  local busy, why = H.Busy()
  if busy and not opts.force then return false, why end
  H.Unlock()
  local destZ = z
  local ok = false
  if type(GmShared.TeleportNav) == "function" then
    ok, destZ = GmShared.TeleportNav(x, y, z, o, {
      lockMs = tonumber(opts.lockMs) or 400,
      map = opts.map,
      floatYd = opts.floatYd,
      clientSync = false,
    })
  end
  if ok then
    H.AfterTravel(x, y, destZ or z)
    return true, destZ
  end
  return false, why or "tp"
end

function H.OnObject(x, y, z, o, opts)
  opts = type(opts) == "table" and opts or {}
  H.Unlock()
  local ok = false
  if type(GmShared.TeleportKeepZ) == "function" then
    ok = GmShared.TeleportKeepZ(x, y, z, o, {
      lockMs = tonumber(opts.lockMs) or 400,
    }) and true or false
  end
  if not ok and type(GmTeleportRaw) == "function" then
    local pcallOk, r = pcall(GmTeleportRaw, x, y, z, o, 3, tonumber(opts.lockMs) or 400)
    ok = pcallOk and r ~= nil and r ~= false and r ~= 0
  end
  if not ok then return false end
  H.AfterTravel(x, y, z)
  return true
end

H.IsBusy = H.Busy
