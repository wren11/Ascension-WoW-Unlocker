--[[ GmMapTeleport Scheduler
     Single heartbeat, immutable snapshot, ordered pure consumers.

     Pipeline: Tick → BuildSnapshot → Dispatch → Consume → Idle
     Heartbeat: one OnUpdate accumulator at TICK_SEC (never C_Timer, never a second loop).
     Events: registered only here; handlers arm the scheduler, they do not scan OM.
]]

local SCH = {}
_G.GmMapTeleportScheduler = SCH

SCH.TICK_SEC = 0.30

local accum = 0
local running = false
local collector = nil
local consumers = {}
local eventHandler = nil
local lastSnapshot = nil

local frame = CreateFrame("Frame", "GmMapTeleportSchedulerFrame")
frame:Hide()

local function freeze(value)
  if type(value) ~= "table" then return value end
  local out = {}
  for k, v in pairs(value) do
    out[k] = freeze(v)
  end
  return setmetatable(out, {
    __newindex = function()
      error("GmMapTeleport snapshot is immutable", 2)
    end,
    __metatable = false,
  })
end

function SCH.IsRunning()
  return running
end

function SCH.LastSnapshot()
  return lastSnapshot
end

function SCH.RegisterCollector(fn)
  collector = fn
end

function SCH.RegisterConsumer(name, fn)
  consumers[#consumers + 1] = { name = name, fn = fn }
end

function SCH.BuildSnapshot()
  if not collector then
    lastSnapshot = nil
    return nil
  end
  local raw = collector()
  if not raw then
    lastSnapshot = nil
    return nil
  end
  lastSnapshot = freeze(raw)
  return lastSnapshot
end

function SCH.Dispatch(snapshot)
  for i = 1, #consumers do
    local c = consumers[i]
    c.fn(snapshot)
  end
end

function SCH.Consume(snapshot)
  SCH.Dispatch(snapshot)
end

function SCH.Stop()
  running = false
  accum = 0
  frame:Hide()
end

function SCH.Start()
  if running then return end
  running = true
  frame:Show()
end

--- Arm the heartbeat. immediate=true forces Tick on the next OnUpdate frame.
function SCH.RequestTick(immediate)
  if immediate then
    accum = SCH.TICK_SEC
  end
  SCH.Start()
end

function SCH.Tick()
  local snapshot = SCH.BuildSnapshot()
  if not snapshot then
    SCH.Stop()
    return
  end
  SCH.Consume(snapshot)
  if not snapshot.keepAlive then
    SCH.Stop()
  end
end

frame:SetScript("OnUpdate", function(_, dt)
  accum = accum + (dt or 0)
  if accum < SCH.TICK_SEC then return end
  accum = 0
  SCH.Tick()
end)

function SCH.RegisterEvents(eventList, handler)
  eventHandler = handler
  for i = 1, #eventList do
    frame:RegisterEvent(eventList[i])
  end
  frame:SetScript("OnEvent", function(_, event, ...)
    if eventHandler then
      eventHandler(event, ...)
    end
  end)
end

function SCH.GetFrame()
  return frame
end
