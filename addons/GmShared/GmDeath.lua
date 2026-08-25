--[[
  GmDeath — shared death/ghost recovery for every running GM addon.

  Never sit as an idle ghost. Snapshot where we were, where we were going,
  and what is next. Instant TP to corpse, accept resurrect (or wait the
  corpse timer), retrieve, then resume the job. Two deaths in the same
  cell → evade on the navmesh and skip that hole.
]]

GmDeath = GmDeath or {}
local D = GmDeath
D.VERSION = "1.0.0"

local NAV_MISS = -99999
local HOLD_SEC = 0.12
local ACCEPT_GAP = 0.35
local DEAD_WAIT_SEC = 1.6
local TP_GAP = 0.28
local CELL = 28
local HIT_TTL = 180
local EVADE_RADII = { 36, 48, 64, 80, 96 }
local EVADE_ANGLES = { 0.25, 0.75, 1.25, 1.75, 0.5, 1.0, 1.5, 0.0, 0.125, 0.875 }
local RING = { 0, 4, 8, 14, 22 }

local jobs = {}
local acc = 0
local frame

local function now()
  return GetTime and GetTime() or 0
end

local function finite(n)
  return type(n) == "number" and n == n and n > -1e30 and n < 1e30
end

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cffff7f50[GmDeath]|r " .. tostring(msg))
  end
end

local function copyPin(p)
  if type(p) ~= "table" then return nil end
  if not finite(p.x) or not finite(p.y) then return nil end
  return {
    x = p.x, y = p.y, z = p.z or 0, o = p.o or 0, map = p.map or 0,
    label = p.label,
  }
end

local function dist2(ax, ay, bx, by)
  local dx, dy = (ax or 0) - (bx or 0), (ay or 0) - (by or 0)
  return dx * dx + dy * dy
end

local function cellKey(map, x, y)
  map = tonumber(map) or 0
  x, y = tonumber(x) or 0, tonumber(y) or 0
  return string.format("%d:%d:%d", map, math.floor(x / CELL), math.floor(y / CELL))
end

local function clearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(GmHwEvent) == "function" then pcall(GmHwEvent, 1) end
end

local function pose()
  if type(GmPlayerPose) == "function" then
    local ok, x, y, z, o, map = pcall(GmPlayerPose)
    if ok and finite(x) and finite(y) then
      return x, y, z or 0, o or 0, tonumber(map) or 0
    end
  end
  if type(GmPlayerXYZ) == "function" then
    local ok, x, y, z, o = pcall(GmPlayerXYZ)
    if ok and finite(x) and finite(y) then
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

local function floorZ(x, y, map, zHint)
  if type(GmNavZ) ~= "function" then return zHint end
  local function q(h)
    local ok, z = pcall(GmNavZ, x, y, map or 0, h or 0)
    if ok and type(z) == "number" and z > NAV_MISS then return z end
  end
  local a = q(zHint or 0)
  local b = q(500)
  if a and b then
    if a > b + 8 then return b end
    return a
  end
  return a or b or zHint
end

local function tp(x, y, z, o, map)
  if not finite(x) or not finite(y) then return false end
  o = finite(o) and o or 0
  map = tonumber(map) or 0
  z = finite(z) and z or 0
  if type(GmShared) == "table" and type(GmShared.TpGuardPulse) == "function" then
    pcall(GmShared.TpGuardPulse)
  end
  clearTaint()
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  local flags, lockMs = 3, 5000
  if type(GmTeleportRaw) == "function" then
    local ok, r = pcall(GmTeleportRaw, x, y, z, o, flags, lockMs)
    if ok and r and r ~= 0 then return true end
  end
  if type(GmTeleport) == "function" then
    local ok, r = pcall(GmTeleport, x, y, z, o)
    if ok and r and r ~= 0 then return true end
  end
  return false
end

local function tpFloor(pin)
  if not pin then return false end
  local gz = floorZ(pin.x, pin.y, pin.map, pin.z)
  pin.z = (gz or pin.z or 0) + 0.12
  return tp(pin.x, pin.y, pin.z, pin.o, pin.map)
end

local function clickPopups()
  local did = false
  local want = {
    RESURRECT = true, RESURRECT_NO_SICKNESS = true, RESURRECT_NO_TIMER = true,
    DEATH = true, RECOVER_CORPSE = true, RECOVER_CORPSE_TIMER = true,
  }
  local i
  for i = 1, 4 do
    local f = _G["StaticPopup" .. i]
    if f and f.IsShown and f:IsShown() then
      local w = tostring(f.which or "")
      if want[w] or w:find("RESURRECT", 1, true) or w:find("DEATH", 1, true)
          or w:find("CORPSE", 1, true) or w:find("RECOVER", 1, true) then
        local btn = _G["StaticPopup" .. i .. "Button1"]
        if btn and btn.Click then
          pcall(btn.Click, btn)
          did = true
        end
      end
    end
  end
  return did
end

local function acceptRes()
  clearTaint()
  local did = false
  if type(AcceptResurrect) == "function" then
    if pcall(AcceptResurrect) then did = true end
  end
  if clickPopups() then did = true end
  return did
end

local function releaseSpirit()
  clearTaint()
  if type(RepopMe) == "function" then pcall(RepopMe) end
  clickPopups()
end

local function retrieveCorpse()
  clearTaint()
  if type(RetrieveCorpse) == "function" then pcall(RetrieveCorpse) end
  clickPopups()
end

local function spiritHealer()
  clearTaint()
  if type(HasSpiritHealer) == "function" and HasSpiritHealer() then
    if type(AcceptXPLoss) == "function" then pcall(AcceptXPLoss) end
    clickPopups()
    return true
  end
  if type(AcceptXPLoss) == "function" then pcall(AcceptXPLoss) end
  clickPopups()
  return false
end

local function corpseXY()
  if type(GetCorpsePosition) == "function" then
    local ok, x, y = pcall(GetCorpsePosition)
    if ok and finite(x) and finite(y) and not (x == 0 and y == 0) then
      return x, y
    end
  end
  if type(GetCorpseMapPosition) == "function" then
    local ok, x, y = pcall(GetCorpseMapPosition)
    if ok and finite(x) and finite(y) then
      -- normalised map coords — not world; ignore unless we have nothing else
    end
  end
  return nil
end

local function isDead()
  if UnitIsDeadOrGhost and UnitIsDeadOrGhost("player") then return true end
  if UnitIsDead and UnitIsDead("player") then return true end
  if UnitIsGhost and UnitIsGhost("player") then return true end
  return false
end

local function isGhost()
  return UnitIsGhost and UnitIsGhost("player") and true or false
end

local function anyRunning()
  local name, job
  for name, job in pairs(jobs) do
    if job.isRunning then
      local ok, run = pcall(job.isRunning)
      if ok and run then return true, name end
    end
  end
  return false
end

local function snapshotJobs()
  local intended, label, state, names = nil, nil, nil, {}
  local name, job
  for name, job in pairs(jobs) do
    local ok, run = pcall(job.isRunning)
    if ok and run then
      names[#names + 1] = name
      if job.snapshot then
        local ok2, snap = pcall(job.snapshot)
        if ok2 and type(snap) == "table" then
          intended = intended or copyPin(snap.intended or snap.dest or snap.pin)
          label = label or snap.label
          state = state or snap.state
        end
      end
    end
  end
  return intended, label, state, table.concat(names, ",")
end

local function resumeJobs(ctx)
  local name, job
  for name, job in pairs(jobs) do
    local ok, run = pcall(job.isRunning)
    if ok and run and job.resume then
      pcall(job.resume, ctx)
    end
  end
end

local function pruneHits(t)
  local k, v
  for k, v in pairs(D.hits or {}) do
    if t - (v.at or 0) > HIT_TTL then D.hits[k] = nil end
  end
end

local function noteDeathCell(pin)
  if not pin then return 1, false end
  D.hits = D.hits or {}
  local t = now()
  pruneHits(t)
  local key = cellKey(pin.map, pin.x, pin.y)
  local row = D.hits[key] or { n = 0, at = t }
  row.n = (row.n or 0) + 1
  row.at = t
  D.hits[key] = row
  return row.n, row.n >= 2
end

local function evadePin(from)
  if not from then return nil end
  local map = from.map or 0
  local i, r
  for r = 1, #EVADE_RADII do
    local rad = EVADE_RADII[r]
    for i = 1, #EVADE_ANGLES do
      local ang = EVADE_ANGLES[i] * math.pi
      local x = from.x + math.cos(ang) * rad
      local y = from.y + math.sin(ang) * rad
      local z = floorZ(x, y, map, from.z)
      if finite(z) then
        return { x = x, y = y, z = z + 0.12, o = from.o or 0, map = map, label = "evade" }
      end
    end
  end
  return {
    x = from.x + 40, y = from.y + 40,
    z = (from.z or 0) + 0.12, o = from.o or 0, map = map, label = "evade-fallback",
  }
end

local function corpsePin()
  local cx, cy = corpseXY()
  local death = D.death or D.lastAlive
  local map = (death and death.map) or 0
  local o = (death and death.o) or 0
  local zHint = death and death.z or 0
  if cx and cy then
    local z = floorZ(cx, cy, map, zHint)
    return { x = cx, y = cy, z = z or zHint, o = o, map = map, label = "corpse" }
  end
  if death then
    local z = floorZ(death.x, death.y, map, death.z)
    return { x = death.x, y = death.y, z = z or death.z, o = o, map = map, label = "death" }
  end
  local x, y, z, po, pmap = pose()
  if x then
    return { x = x, y = y, z = z, o = po, map = pmap, label = "here" }
  end
  return nil
end

local function resetRecover()
  D.busy = false
  D.phase = "idle"
  D.status = "idle"
  D.phaseAt = now()
  D.ghostTries = 0
  D.acceptT = 0
  D.lastTpAt = 0
  D.corpseInRange = D.corpseInRange or false
end

local function beginRecover(why)
  local x, y, z, o, map = pose()
  local live = D.lastAlive
  D.whereAlive = copyPin(live)
  D.whereNow = (x and { x = x, y = y, z = z, o = o, map = map }) or copyPin(live)
  local intended, label, state, names = snapshotJobs()
  D.intended = intended
  D.nextLabel = label
  D.jobState = state
  D.jobNames = names
  D.death = copyPin(live) or D.whereNow
  local n, evade = noteDeathCell(D.death)
  D.sameSpotN = n
  D.needEvade = evade and true or false
  D.busy = true
  D.phase = isGhost() and "ghost" or "dead"
  D.phaseAt = now()
  D.ghostTries = 0
  D.acceptT = 0
  D.lastTpAt = 0
  D.status = why or "recovering"
  chat(string.format(
    "died at %.1f %.1f z=%.1f map=%s  was-going=%s  jobs=%s  same-spot=%d%s",
    (D.death and D.death.x) or 0, (D.death and D.death.y) or 0,
    (D.death and D.death.z) or 0, tostring(D.death and D.death.map),
    (intended and intended.label) or label or "last pose",
    names ~= "" and names or "none",
    n, evade and " → EVADE" or ""))
end

local function maybeTp(pin, tag)
  local t = now()
  if t - (D.lastTpAt or 0) < TP_GAP then return false end
  if not pin then return false end
  D.lastTpAt = t
  D.status = tag or "tp"
  return tp(pin.x, pin.y, pin.z, pin.o, pin.map)
end

local function finishAlive()
  local ctx = {
    death = D.death,
    alive = D.whereAlive,
    intended = D.intended,
    evade = D.needEvade,
    sameSpotN = D.sameSpotN or 1,
    jobs = D.jobNames,
    state = D.jobState,
  }
  local dest = D.intended
  if D.needEvade then
    dest = evadePin(D.death or D.whereAlive or dest)
    ctx.evadePin = dest
    chat("same-spot death — teleporting elsewhere, then resume next work")
  elseif not dest then
    dest = copyPin(D.death or D.whereAlive)
  end
  if dest then
    tpFloor(dest)
    D.status = "resume TP"
  end
  if type(GmFreeMove) == "function" then pcall(GmFreeMove) end
  resumeJobs(ctx)
  chat("alive — back to " .. tostring(D.jobNames ~= "" and D.jobNames or "work")
    .. (D.nextLabel and (" (" .. D.nextLabel .. ")") or ""))
  resetRecover()
end

local function tickDead(dt)
  D.acceptT = (D.acceptT or 0) + dt
  if D.acceptT >= ACCEPT_GAP then
    D.acceptT = 0
    acceptRes()
    D.status = "dead — accept resurrect"
  end
  if now() - (D.phaseAt or 0) >= DEAD_WAIT_SEC then
    releaseSpirit()
    D.phase = "ghost"
    D.phaseAt = now()
    D.status = "released spirit → corpse"
  end
end

local function tickGhost(dt)
  acceptRes()
  local delay = 0
  if GetCorpseRecoveryDelay then
    delay = tonumber(GetCorpseRecoveryDelay()) or 0
  end
  local pin = corpsePin()
  D.corpse = pin
  local px, py = pose()
  local onCorpse = pin and px and dist2(px, py, pin.x, pin.y) <= (18 * 18)
  if pin and not onCorpse then
    maybeTp(pin, "ghost → corpse")
    D.ghostTries = (D.ghostTries or 0) + 1
  end
  if delay and delay > 0 then
    D.status = string.format("wait corpse timer %ds (never idle)", delay)
    -- Stay glued to the corpse so we retrieve the instant the timer hits 0.
    if pin and not onCorpse then maybeTp(pin, "hold corpse") end
    return
  end
  if D.corpseInRange or onCorpse then
    retrieveCorpse()
    D.status = "retrieving corpse"
    return
  end
  if pin then
    local i
    local try = (D.ghostTries or 0)
    local rad = RING[(try % #RING) + 1]
    local ang = (try * 0.7)
    maybeTp({
      x = pin.x + math.cos(ang) * rad,
      y = pin.y + math.sin(ang) * rad,
      z = pin.z, o = pin.o, map = pin.map,
    }, "corpse ring")
    retrieveCorpse()
  else
    retrieveCorpse()
    D.status = "ghost — searching corpse"
  end
  -- Still ghost after a long wait: spirit healer, then we TP back to work.
  if now() - (D.phaseAt or 0) > 22 then
    spiritHealer()
    D.status = "spirit healer fallback"
    D.phaseAt = now()
  end
end

local function trackAlive()
  local x, y, z, o, map = pose()
  if not x then return end
  D.lastAlive = { x = x, y = y, z = z, o = o, map = map, at = now() }
  local intended = select(1, snapshotJobs())
  if intended then D.liveIntended = intended end
end

function D.Register(name, api)
  if type(name) ~= "string" or name == "" then return end
  jobs[name] = api or {}
end

function D.IsBusy()
  return D.busy and true or false
end

function D.ShouldHold(name)
  if not D.busy then return false end
  if isDead() then return true end
  return D.phase ~= "idle"
end

function D.Status()
  return D.status or "idle"
end

function D.Context()
  return {
    phase = D.phase, status = D.status, busy = D.busy,
    death = D.death, alive = D.whereAlive, intended = D.intended,
    corpse = D.corpse, jobs = D.jobNames, evade = D.needEvade,
    sameSpotN = D.sameSpotN, next = D.nextLabel, state = D.jobState,
  }
end

function D.OnCorpseInRange(inside)
  D.corpseInRange = inside and true or false
  if inside and D.busy and isGhost() then retrieveCorpse() end
end

function D.OnResurrectRequest()
  if not D.busy and not select(1, anyRunning()) then return end
  acceptRes()
end

function D.OnPlayerDead()
  local run = select(1, anyRunning())
  if not run and not D.busy then return end
  if D.busy and D.phase ~= "idle" then
    -- Died again mid-recover — refresh snapshot, keep going.
    local n, evade = noteDeathCell(D.lastAlive or D.death)
    D.sameSpotN = n
    D.needEvade = D.needEvade or evade
    D.phase = isGhost() and "ghost" or "dead"
    D.phaseAt = now()
    D.status = "died again — still recovering"
    chat("died again in recover (same-spot=" .. tostring(n) .. ")")
    return
  end
  beginRecover("PLAYER_DEAD")
end

function D.OnAlive()
  if not D.busy then return end
  if isDead() then return end
  finishAlive()
end

function D.Pulse(dt)
  dt = dt or HOLD_SEC
  local run = select(1, anyRunning())
  if isDead() then
    if run or D.busy then
      if not D.busy then beginRecover("pulse") end
      if isGhost() then
        if D.phase ~= "ghost" then D.phase = "ghost" D.phaseAt = now() end
        tickGhost(dt)
      else
        if D.phase ~= "dead" then D.phase = "dead" D.phaseAt = now() end
        tickDead(dt)
      end
      return
    end
    return
  end
  if D.busy then
    finishAlive()
    return
  end
  if run then trackAlive() end
end

local function bindKnown()
  local Hunt = _G.LootCollectorHunt
  if Hunt and not jobs.Hunt then
    D.Register("Hunt", {
      isRunning = function() return Hunt.running end,
      snapshot = function()
        local dest = Hunt.dest or Hunt.obj
        local pin = Hunt.pin
        return {
          intended = dest and { x = dest.x, y = dest.y, z = dest.z, o = dest.o, map = dest.map } or nil,
          pin = pin,
          state = Hunt.state,
          label = (Hunt.PinLabel and Hunt:PinLabel(pin)) or "hunt pin",
        }
      end,
      resume = function(ctx)
        if not Hunt.running then return end
        if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
        Hunt.wentUnder = false
        Hunt.locked = false
        local st = Hunt.state
        if ctx.evade and st ~= "vendor_scan" and st ~= "vendor_open"
            and st ~= "vendor_sell" and st ~= "vendor_done" then
          if Hunt.Advance then Hunt:Advance("skip") end
          return
        end
        if st == "idle" or st == "loot_pose" or st == "loot_fire"
            or st == "wait_loot" or st == "take" or st == "scan"
            or st == "settle_hint" or st == "hint_tp" or st == "kite_wait"
            or st == "delay" then
          Hunt:SetState("delay")
        end
      end,
    })
  end

  local GB = _G.GatherBot
  if GB and GB.S and not jobs.GatherBot then
    D.Register("GatherBot", {
      isRunning = function() return GB.S.running end,
      snapshot = function()
        local S = GB.S
        local dest = S.huntReturn or S.curPin or S.deathPos
        return {
          intended = dest and { x = dest.x, y = dest.y, z = dest.z, o = dest.o, map = dest.map } or nil,
          state = S.state,
          label = S.statusLine or "gather",
        }
      end,
      resume = function(ctx)
        local S, F, ST = GB.S, GB.F, GB.ST
        if not S.running then return end
        if ctx.evade then S.huntReturn = nil end
        if F and F.setState and ST then
          F.setState(ST.PATROL or "PATROL", ctx.evade and "death evade" or "death resume")
        end
      end,
    })
  end

  local HB = _G.HuntingBot
  if HB and HB.S and not jobs.HuntingBot then
    D.Register("HuntingBot", {
      isRunning = function() return HB.S.running end,
      snapshot = function()
        local S = HB.S
        local dest = S.huntReturn or S.deathPos
        return {
          intended = dest and { x = dest.x, y = dest.y, z = dest.z, o = dest.o, map = dest.map } or nil,
          state = S.state,
          label = S.statusLine or "hunt",
        }
      end,
      resume = function(ctx)
        local S, F, ST = HB.S, HB.F, HB.ST
        if not S.running then return end
        if ctx.evade then S.huntReturn = nil end
        if F and F.setState and ST then
          F.setState(ST.PATROL or "PATROL", ctx.evade and "death evade" or "death resume")
        end
      end,
    })
  end

  local CTF = _G.CtfCap
  if CTF and CTF.S and not jobs.CtfCap then
    D.Register("CtfCap", {
      isRunning = function() return CTF.S.running end,
      snapshot = function()
        local S = CTF.S
        local dest = S.homePin or S.flagPin
        return {
          intended = dest and { x = dest.x, y = dest.y, z = dest.z, o = dest.o, map = dest.map } or nil,
          state = S.state,
          label = S.status or "ctf",
        }
      end,
      resume = function()
        -- Engine keeps running; recoverDeath will stop blocking once alive.
      end,
    })
  end

  local BG = _G.BgAfk
  if BG and BG.S and not jobs.BgAfk then
    D.Register("BgAfk", {
      isRunning = function() return BG.S.running end,
      snapshot = function()
        local S = BG.S
        local dest = S.homePin or S.cornerPin
        return {
          intended = dest and { x = dest.x, y = dest.y, z = dest.z, o = dest.o, map = dest.map } or nil,
          state = S.state,
          label = S.status or "bgafk",
        }
      end,
    })
  end

  local E = _G.GmExplore
  if E and not jobs.GmExplore then
    D.Register("GmExplore", {
      isRunning = function()
        return E.S and E.S.running
      end,
      snapshot = function()
        local S = E.S
        if not S then return {} end
        return { state = S.state, label = "explore #" .. tostring(S.index or 0) }
      end,
      resume = function(ctx)
        local S = E.S
        if not (S and S.running) then return end
        if ctx.evade then
          S.index = (S.index or 1) + 1
        end
        S.paused = false
        if S.death then S.death.active = false S.death.hold = false end
      end,
    })
  end

  local KX = _G.KnightOfXoroth
  if KX and not jobs.KnightOfXoroth then
    D.Register("KnightOfXoroth", {
      isRunning = function() return KX.enabled end,
      snapshot = function()
        return {
          intended = KX.deathPose,
          state = KX.state,
          label = "kx",
        }
      end,
      resume = function()
        if not KX.enabled then return end
        KX.pendingDeathReturn = false
        KX.wasDeadOrGhost = false
        KX.state = "HUNT"
      end,
    })
  end

  local BB = _G.BotBuilder
  if BB and BB.Scheduler and not jobs.BotBuilder then
    D.Register("BotBuilder", {
      isRunning = function()
        return BB.Scheduler.IsRunning and BB.Scheduler.IsRunning()
      end,
      snapshot = function()
        return { label = "botbuilder" }
      end,
    })
  end

  local AF = _G.ActionFlow
  if AF and AF.Runtime and not jobs.ActionFlow then
    D.Register("ActionFlow", {
      isRunning = function()
        return AF.Runtime.IsRunning and AF.Runtime.IsRunning()
      end,
      snapshot = function()
        return { label = "actionflow" }
      end,
    })
  end

  local GC = _G.GmCombat
  if GC and GC.Scheduler and not jobs.GmCombat then
    D.Register("GmCombat", {
      isRunning = function()
        return GC.Scheduler.IsRunning and GC.Scheduler.IsRunning()
      end,
      snapshot = function()
        return { label = "gmcombat" }
      end,
    })
  end
end

local function ensureFrame()
  if frame then return end
  frame = CreateFrame("Frame", "GmDeathWatch")
  frame:RegisterEvent("PLAYER_DEAD")
  frame:RegisterEvent("PLAYER_ALIVE")
  frame:RegisterEvent("PLAYER_UNGHOST")
  frame:RegisterEvent("CORPSE_IN_RANGE")
  frame:RegisterEvent("CORPSE_OUT_OF_RANGE")
  frame:RegisterEvent("RESURRECT_REQUEST")
  frame:RegisterEvent("PLAYER_ENTERING_WORLD")
  frame:SetScript("OnEvent", function(_, event)
    bindKnown()
    if event == "PLAYER_DEAD" then
      D.OnPlayerDead()
    elseif event == "PLAYER_ALIVE" or event == "PLAYER_UNGHOST" then
      D.OnAlive()
    elseif event == "CORPSE_IN_RANGE" then
      D.OnCorpseInRange(true)
    elseif event == "CORPSE_OUT_OF_RANGE" then
      D.OnCorpseInRange(false)
    elseif event == "RESURRECT_REQUEST" then
      D.OnResurrectRequest()
    end
  end)
  frame:SetScript("OnUpdate", function(_, elapsed)
    acc = acc + (elapsed or 0)
    if acc < HOLD_SEC then return end
    local step = acc
    acc = 0
    if step > 0.5 then step = 0.5 end
    bindKnown()
    local ok, err = pcall(D.Pulse, step)
    if not ok then
      D.status = "pulse error"
      chat("pulse error " .. tostring(err))
    end
  end)
end

resetRecover()
D.hits = {}
ensureFrame()
bindKnown()

SLASH_GMDEATH1 = "/gmdeath"
SlashCmdList["GMDEATH"] = function()
  local c = D.Context()
  chat(string.format("v%s  busy=%s phase=%s  %s  jobs=%s  same=%s evade=%s",
    D.VERSION, tostring(c.busy), tostring(c.phase), tostring(c.status),
    tostring(c.jobs), tostring(c.sameSpotN), tostring(c.evade)))
  if c.death then
    chat(string.format("  died %.1f %.1f %.1f map=%s", c.death.x, c.death.y, c.death.z, tostring(c.death.map)))
  end
  if c.intended then
    chat(string.format("  next  %.1f %.1f %.1f map=%s", c.intended.x, c.intended.y, c.intended.z, tostring(c.intended.map)))
  end
end
