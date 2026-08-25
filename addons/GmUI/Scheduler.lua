--[[
  GmUI Scheduler — single heartbeat for all periodic work.

  Interface 30300 (WotLK) has no C_Timer; one OnUpdate accumulator owns timing.
  Pipeline: Tick → Collect → Immutable Snapshot → Dispatch → Idle.
  Public timing APIs (MakePump, OnShownUpdate) register here; they never own OnUpdate.
]]

local UI = GmUI
UI.Scheduler = UI.Scheduler or {}
local SCH = UI.Scheduler

SCH.TICK = 0.30

local nextId = 1
local pumps = {}       -- id -> { id, period, acc, fn, enabled, errAt }
local shown = {}       -- id -> { id, frame, period, acc, fn, enabled }
local deferred = {}    -- { due, fn }
local eventHandlers = {} -- event -> { fn, ... }

local heart = CreateFrame("Frame", "GmUISchedulerHeart")
heart:Hide()
local heartAcc = 0
local heartOn = false

local eventFrame = CreateFrame("Frame", "GmUISchedulerEvents")

local lastSnapshot = nil

local function recountActive()
  local n = #deferred
  for _, p in pairs(pumps) do
    if p.enabled then n = n + 1 end
  end
  for _, s in pairs(shown) do
    if s.enabled then n = n + 1 end
  end
  return n
end

local function syncHeartbeat()
  if recountActive() > 0 then
    if not heartOn then
      heartAcc = 0
      heartOn = true
      heart:Show()
    end
  else
    if heartOn then
      heartOn = false
      heartAcc = 0
      heart:Hide()
    end
  end
end

local function freezeList(src)
  local out = {}
  for i = 1, #src do
    out[i] = src[i]
  end
  return out
end

--- Collect due work for this tick into an immutable snapshot.
local function Collect(now, dt)
  local duePumps = {}
  local dueShown = {}
  local dueDeferred = {}

  for _, p in pairs(pumps) do
    if p.enabled and p.fn then
      p.acc = (p.acc or 0) + dt
      if p.acc >= p.period then
        local step = p.acc
        p.acc = 0
        duePumps[#duePumps + 1] = { id = p.id, period = p.period, step = step, fn = p.fn, pump = p }
      end
    end
  end

  for _, s in pairs(shown) do
    if s.enabled and s.fn and s.frame then
      if s.frame.IsShown and not s.frame:IsShown() then
        s.enabled = false
      else
        s.acc = (s.acc or 0) + dt
        if s.acc >= s.period then
          s.acc = 0
          dueShown[#dueShown + 1] = { id = s.id, frame = s.frame, fn = s.fn }
        end
      end
    end
  end

  if #deferred > 0 then
    local remain = {}
    for i = 1, #deferred do
      local d = deferred[i]
      if d.due <= now then
        dueDeferred[#dueDeferred + 1] = { fn = d.fn }
      else
        remain[#remain + 1] = d
      end
    end
    deferred = remain
  end

  return {
    now = now,
    dt = dt,
    pumps = freezeList(duePumps),
    shown = freezeList(dueShown),
    deferred = freezeList(dueDeferred),
  }
end

local function reportPumpError(pump, err)
  local now = GetTime()
  if (pump.errAt or 0) + 1 < now then
    pump.errAt = now
    if DEFAULT_CHAT_FRAME then
      DEFAULT_CHAT_FRAME:AddMessage("|cffff4444[GmUI pump]|r " .. tostring(err))
    end
  end
end

--- Dispatch snapshot to consumers (pcall-isolated). Order: deferred → pumps → shown.
local function Dispatch(snap)
  for i = 1, #snap.deferred do
    local d = snap.deferred[i]
    if d.fn then pcall(d.fn) end
  end
  for i = 1, #snap.pumps do
    local p = snap.pumps[i]
    if p.fn then
      local ok, err = pcall(p.fn, p.step)
      if not ok and p.pump then
        reportPumpError(p.pump, err)
      end
    end
  end
  for i = 1, #snap.shown do
    local s = snap.shown[i]
    if s.fn and s.frame then
      pcall(s.fn, s.frame)
    end
  end
end

function SCH.Tick(dt)
  local now = GetTime()
  local snap = Collect(now, dt or SCH.TICK)
  lastSnapshot = snap
  Dispatch(snap)
  syncHeartbeat()
end

heart:SetScript("OnUpdate", function(_, elapsed)
  if not heartOn then
    heart:Hide()
    return
  end
  heartAcc = heartAcc + (elapsed or 0)
  if heartAcc < SCH.TICK then return end
  local step = heartAcc
  heartAcc = 0
  SCH.Tick(step)
end)

function SCH.GetSnapshot()
  return lastSnapshot
end

function SCH.IsRunning()
  return heartOn
end

--- One-shot delayed callback (replaces ad-hoc delay OnUpdate frames).
function SCH.After(delay, fn)
  if type(fn) ~= "function" then return end
  delay = tonumber(delay) or 0
  if delay < 0 then delay = 0 end
  deferred[#deferred + 1] = { due = GetTime() + delay, fn = fn }
  syncHeartbeat()
end

--- Register a pump consumer. Returns handle with Enable/Disable/SetPeriod (MakePump API).
function SCH.RegisterPump(period, tickFn)
  local id = nextId
  nextId = nextId + 1
  period = UI.ClampInterval(period, UI.ENGINE_INTERVAL)
  local entry = {
    id = id,
    period = period,
    acc = 0,
    fn = tickFn,
    enabled = false,
    errAt = 0,
  }
  pumps[id] = entry

  local handle = {}
  function handle:Enable()
    entry.enabled = true
    entry.acc = 0
    syncHeartbeat()
  end
  function handle:Disable()
    entry.enabled = false
    entry.acc = 0
    syncHeartbeat()
  end
  function handle:SetPeriod(p)
    entry.period = UI.ClampInterval(p, entry.period)
  end
  return handle
end

--- Hook frame show/hide; while shown, fn runs on the central schedule.
function SCH.RegisterShownUpdate(frame, period, fn)
  if not frame or type(fn) ~= "function" then return end
  period = UI.ClampInterval(period, UI.UI_INTERVAL)
  if period < UI.UI_INTERVAL then period = UI.UI_INTERVAL end

  local id = nextId
  nextId = nextId + 1
  local entry = {
    id = id,
    frame = frame,
    period = period,
    acc = 0,
    fn = fn,
    enabled = false,
  }
  shown[id] = entry

  frame:HookScript("OnShow", function()
    entry.enabled = true
    entry.acc = 0
    syncHeartbeat()
  end)
  frame:HookScript("OnHide", function()
    entry.enabled = false
    entry.acc = 0
    syncHeartbeat()
  end)

  if frame.IsShown and frame:IsShown() then
    entry.enabled = true
    syncHeartbeat()
  end
end

--- Addon-level event subscription (single event frame owns RegisterEvent).
function SCH.On(event, fn)
  if type(event) ~= "string" or type(fn) ~= "function" then return end
  local list = eventHandlers[event]
  if not list then
    list = {}
    eventHandlers[event] = list
    eventFrame:RegisterEvent(event)
  end
  list[#list + 1] = fn
end

eventFrame:SetScript("OnEvent", function(_, event, ...)
  local list = eventHandlers[event]
  if not list then return end
  for i = 1, #list do
    pcall(list[i], event, ...)
  end
end)
