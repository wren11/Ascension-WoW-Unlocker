--[[
  GmTeleport Scheduler — single heartbeat, single snapshot pipeline.

  Pipeline: Tick → Collect → Immutable Snapshot → Dispatch → Pure Consumers → Idle

  Heartbeat: one OnUpdate accumulator at TICK_SEC (0.30). Never C_Timer.
  Native global `GmTeleport` is an ExtProxy function — do not overwrite it.
]]

GmTeleportAddon = GmTeleportAddon or {}
local Addon = GmTeleportAddon
local SCH = {}
Addon.Scheduler = SCH

local TICK_SEC = 0.30

local consumers = {} -- { { name=, fn=, order= }, ... } sorted by order
local heartbeat = CreateFrame("Frame")
heartbeat:Hide()

local acc = 0
local running = false
local eventsRegistered = false
local eventFrame = CreateFrame("Frame")

-- Live sync timing owned by scheduler collect (not by consumers).
local sync = {
  active = false,
  x = 0, y = 0, z = 0, o = 0,
  elapsed = 0,
  quiet = false,
}

local lastSnapshot = nil
local loginPending = false

local function shallowCopyPin(p)
  if not p or not p.x then return nil end
  return {
    x = p.x, y = p.y, z = p.z, o = p.o,
    map = p.map, facingSrc = p.facingSrc, ground = p.ground,
  }
end

local function freezeSnapshot(s)
  -- Treat as immutable: consumers must not write fields.
  -- (Lua 5.1 has no real freeze; contract is by convention + fresh table each tick.)
  return s
end

function SCH.TickSec()
  return TICK_SEC
end

function SCH.IsRunning()
  return running
end

function SCH.GetSync()
  return sync
end

function SCH.LastSnapshot()
  return lastSnapshot
end

--- Register a pure consumer: fn(snapshot). order lower = earlier.
function SCH.RegisterConsumer(name, fn, order)
  if type(name) ~= "string" or type(fn) ~= "function" then return false end
  for i = #consumers, 1, -1 do
    if consumers[i].name == name then table.remove(consumers, i) end
  end
  consumers[#consumers + 1] = { name = name, fn = fn, order = tonumber(order) or 100 }
  table.sort(consumers, function(a, b)
    if a.order == b.order then return a.name < b.name end
    return a.order < b.order
  end)
  return true
end

function SCH.ArmSync(x, y, z, o, quiet)
  sync.active = true
  sync.quiet = quiet and true or false
  sync.x, sync.y, sync.z, sync.o = x, y, z or 0, o or 0
  sync.elapsed = 0
  SCH.Start()
end

function SCH.ClearSync()
  sync.active = false
  sync.quiet = false
  sync.elapsed = 0
end

function SCH.NeedsWork()
  return sync.active or loginPending or (Addon._uiDirty == true)
end

--- Collect once per tick — all OM/pose reads happen here.
function SCH.BuildSnapshot(dt)
  local pose = nil
  if sync.active and type(Addon.ReadPose) == "function" then
    local x, y, z, o, map, src = Addon.ReadPose()
    if x then
      pose = { x = x, y = y, z = z or 0, o = o or 0, map = map or 0, src = src }
    end
  end

  local ui = _G["GmTeleportUI"]
  local snap = {
    now = (type(GetTime) == "function" and GetTime()) or 0,
    dt = dt or 0,
    tickSec = TICK_SEC,
    pose = pose,
    sync = {
      active = sync.active,
      x = sync.x, y = sync.y, z = sync.z, o = sync.o,
      elapsed = sync.elapsed,
      quiet = sync.quiet,
      hardSec = Addon.SYNC_HARD_SEC or 1.0,
    },
    pin = shallowCopyPin(GmTeleportDB and GmTeleportDB.pin),
    uiShown = (ui and ui:IsShown()) and true or false,
    uiDirty = Addon._uiDirty == true,
    loginPending = loginPending,
    hasTeleport = (type(Addon.HasTeleport) == "function" and Addon.HasTeleport()) or false,
  }
  return freezeSnapshot(snap)
end

function SCH.Dispatch(snap)
  for i = 1, #consumers do
    local c = consumers[i]
    local ok, err = pcall(c.fn, snap)
    if not ok and DEFAULT_CHAT_FRAME then
      DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmTeleport]|r |cffff4444consumer " .. c.name .. "|r " .. tostring(err))
    end
  end
end

--- After consumers: clear one-shot flags; idle-stop when no work remains.
function SCH.Consume()
  if loginPending then loginPending = false end
  if Addon._uiDirty then Addon._uiDirty = false end
  if not SCH.NeedsWork() then
    SCH.Stop()
  end
end

function SCH.Tick(dt)
  if sync.active then
    sync.elapsed = (sync.elapsed or 0) + (dt or 0)
  end
  local snap = SCH.BuildSnapshot(dt)
  lastSnapshot = snap
  SCH.Dispatch(snap)
  SCH.Consume()
end

function SCH.Start()
  if running then return end
  running = true
  acc = 0
  heartbeat:Show()
end

function SCH.Stop()
  running = false
  heartbeat:Hide()
  acc = 0
end

--- Wake the heartbeat. immediate=true runs one Tick(0) now (events/UI), then
--- keeps the accumulator running only if lasting work (sync) remains.
function SCH.RequestWake(immediate)
  if immediate then
    local ok, err = pcall(SCH.Tick, 0)
    if not ok and DEFAULT_CHAT_FRAME then
      DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmTeleport]|r |cffff4444Tick|r " .. tostring(err))
    end
    if SCH.NeedsWork() then
      SCH.Start()
    end
    return
  end
  SCH.Start()
end

function SCH.MarkUiDirty()
  Addon._uiDirty = true
  SCH.RequestWake(true)
end

function SCH.RegisterEvents()
  if eventsRegistered then return end
  eventsRegistered = true
  eventFrame:RegisterEvent("PLAYER_LOGIN")
  eventFrame:SetScript("OnEvent", function(_, event)
    if event == "PLAYER_LOGIN" then
      loginPending = true
      SCH.RequestWake(true)
    end
  end)
end

-- Sole heartbeat: accumulate only, then Tick. No game logic here.
heartbeat:SetScript("OnUpdate", function(_, dt)
  acc = acc + (dt or 0)
  if acc < TICK_SEC then return end
  local step = acc
  acc = 0
  local ok, err = pcall(SCH.Tick, step)
  if not ok and DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmTeleport]|r |cffff4444Tick|r " .. tostring(err))
  end
end)

SCH.RegisterEvents()
