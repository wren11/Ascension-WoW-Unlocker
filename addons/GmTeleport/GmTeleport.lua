--[[
  GmTeleport — load-teleport bookmark with mmap validation.
  Public APIs preserved: GmTeleport_ClientSync / Unlock / SetHere / Go / Toggle.

  All timed work flows through GmTeleportAddon.Scheduler (300ms heartbeat).
  Consumers are pure w.r.t. the snapshot (no OM queries, no timers).
]]

GmTeleportDB = GmTeleportDB or {}
GmTeleportAddon = GmTeleportAddon or {}

local Addon = GmTeleportAddon
local ADDON = "GmTeleport"
local VERSION = "1.9.9"

local TP_LOCK_MS = 2000
-- skipGround|skipJump on EE injects — load kick is FallLand (native), not MOVE_JUMP
local TP_FLAGS = 3
Addon.SYNC_HARD_SEC = 1.00

local SCH = Addon.Scheduler
if not SCH then
  error("GmTeleport: Scheduler.lua must load before GmTeleport.lua")
end

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmTeleport]|r " .. tostring(msg))
  end
end

local function clearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
end

function Addon.HasTeleport()
  return type(GmTeleport) == "function" or type(GmTeleportRaw) == "function"
end

local function fmtPin(p)
  if not p or not p.x then return "none" end
  return string.format("%.1f, %.1f, %.1f  facing %.3f", p.x, p.y, p.z or 0, p.o or 0)
end

local function refreshPinLabelFromPin(p)
  local ui = _G[ADDON .. "UI"]
  if not (ui and ui.pinLabel) then return end
  if p and p.x then
    ui.pinLabel:SetText("|cff8c96a5Saved:|r " .. fmtPin(p))
  else
    ui.pinLabel:SetText("|cff8c96a5Saved:|r (none — press Set here)")
  end
end

local function refreshPinLabel()
  refreshPinLabelFromPin(GmTeleportDB.pin)
end

local function isFinite(n)
  return type(n) == "number" and n == n and n > -1e30 and n < 1e30
end

local function facingOk(o)
  if not isFinite(o) then return false end
  if math.abs(o) > 100 then return false end
  if o ~= 0 and math.abs(o) < 1e-20 then return false end
  return true
end

local function pinCoordsOk(p)
  if not p then return false, "no pin" end
  if not (isFinite(p.x) and isFinite(p.y) and isFinite(p.z)) then
    return false, "non-finite xyz"
  end
  if math.abs(p.x) > 60000 or math.abs(p.y) > 60000 then
    return false, "xy out of range"
  end
  if p.z < -2000 or p.z > 3000 then
    return false, "z out of range"
  end
  if not facingOk(p.o or 0) then
    return false, "bad facing — Set here again after turning"
  end
  return true
end

function Addon.ReadPose()
  clearTaint()
  if type(GmPlayerPose) == "function" then
    local x, y, z, o, map = GmPlayerPose()
    if isFinite(x) and isFinite(y) and (math.abs(x) > 0.01 or math.abs(y) > 0.01) then
      if not facingOk(o) then o = 0 end
      return x, y, z or 0, o or 0, map or 0, "client"
    end
  end
  if type(GmPlayerXYZ) == "function" then
    local x, y, z, o = GmPlayerXYZ()
    if isFinite(x) and isFinite(y) then
      if not facingOk(o) then o = 0 end
      return x, y, z or 0, o or 0, 0, "xyz"
    end
  end
  return nil
end

local function validateDest(x, y, z, o)
  if type(GmTeleportValidate) == "function" then
    local ok, detail = GmTeleportValidate(x, y, z, o or 0)
    if ok and ok ~= 0 then
      return true, detail
    end
    local why = tostring(detail or "invalid")
    if type(IsInInstance) == "function" and IsInInstance()
        and facingOk(o or 0)
        and (why:find("navmesh", 1, true) or why:find("ground", 1, true)
             or why:find("below", 1, true) or why:find("above", 1, true)) then
      return true, z
    end
    return false, why
  end
  if type(GmNavZ) == "function" then
    local gz = GmNavZ(x, y, 0, z)
    if not gz or gz < -99999 then
      if type(IsInInstance) == "function" and IsInInstance() then
        return true, z
      end
      return false, "no walkable navmesh poly"
    end
    local dz = (z or 0) - gz
    if dz > 3 or dz < -3 then
      if type(IsInInstance) == "function" and IsInInstance() and dz < -25 then
        return true, z
      end
      return false, string.format("z off ground by %.1f (ground=%.1f)", dz, gz)
    end
    return true, gz
  end
  return false, "GmTeleportValidate missing — restart client for new ExtProxy"
end

local function writeFacing(o)
  clearTaint()
  if type(GmSetFacing) == "function" then
    pcall(GmSetFacing, o)
  elseif type(GmFaceAngle) == "function" then
    pcall(GmFaceAngle, o)
  end
end

local function reassertPin(x, y, z, o)
  clearTaint()
  if type(GmTpLock) == "function" then
    pcall(GmTpLock, x, y, z, o, TP_LOCK_MS, 28)
  end
  if type(GmTpPulse) == "function" then
    pcall(GmTpPulse)
  end
  if type(GmTeleportRaw) == "function" then
    pcall(GmTeleportRaw, x, y, z, o, TP_FLAGS, TP_LOCK_MS)
  end
  writeFacing(o)
end

local function finishSync(snap)
  local s = snap.sync
  clearTaint()
  if type(GmTpUnlock) == "function" then
    pcall(GmTpUnlock)
  end
  writeFacing(s.o)
  local quiet = s.quiet
  SCH.ClearSync()

  if quiet then return end
  local pose = snap.pose
  local d2 = -1
  if pose then
    local dx, dy, dz = pose.x - s.x, pose.y - s.y, (pose.z or 0) - s.z
    d2 = dx * dx + dy * dy + dz * dz
  end
  if d2 >= 0 and d2 < 25 then
    chat(string.format("|cff2ecc71client synced|r %.1f, %.1f, %.1f", s.x, s.y, s.z))
  else
    chat(string.format("|cffff8800unlocked — streaming|r d2=%.0f", d2))
  end
end

---------------------------------------------------------------------------
-- Consumers (snapshot only — no live OM, no timers)
---------------------------------------------------------------------------

-- 1) Client sync: pulse during hard window, unlock after.
local function consumeSync(snap)
  local s = snap.sync
  if not s.active then return end

  local hard = s.hardSec or Addon.SYNC_HARD_SEC
  if s.elapsed < hard then
    -- One reassert per scheduler tick (replaces former 50ms pulseAcc loop).
    reassertPin(s.x, s.y, s.z, s.o)
    return
  end

  finishSync(snap)
end

-- 2) UI pin label from snapshot when marked dirty (SetHere / OnShow).
local function consumeUi(snap)
  if not snap.uiDirty then return end
  refreshPinLabelFromPin(snap.pin)
end

-- 3) Boot banner (one-shot via loginPending flag in snapshot).
local function consumeLogin(snap)
  if not snap.loginPending then return end
  chat("v" .. VERSION .. " — Set here / Teleport (heartbeat load, no jump)")
end

SCH.RegisterConsumer("login", consumeLogin, 10)
SCH.RegisterConsumer("sync", consumeSync, 20)
SCH.RegisterConsumer("ui", consumeUi, 30)

---------------------------------------------------------------------------
-- Public API
---------------------------------------------------------------------------

local function armClientSync(x, y, z, o, quiet)
  if type(GmShared) == "table" and type(GmShared.TpGuardPulse) == "function" then
    pcall(GmShared.TpGuardPulse)
  end
  clearTaint()
  if type(GmTpLock) == "function" then
    pcall(GmTpLock, x, y, z, o, TP_LOCK_MS, 28)
  end
  -- No JumpOrAscendStart — native LoadEx already did FallLand load kick.
  if type(GmTpPulse) == "function" then
    pcall(GmTpPulse)
  end
  if type(GmTeleportRaw) == "function" then
    pcall(GmTeleportRaw, x, y, z, o, TP_FLAGS, TP_LOCK_MS)
  end
  writeFacing(o)
  SCH.ArmSync(x, y, z, o, quiet)
end

-- ClientSync contract (STABLE — do not change arity/meaning without A2/A6 sync):
--   GmTeleport_ClientSync(x, y, z, o, quiet) → bool
-- Arms lock+pulse+Raw reassert via scheduler; quiet suppresses chat.
function GmTeleport_ClientSync(x, y, z, o, quiet)
  if not isFinite(x) or not isFinite(y) then return false end
  if not facingOk(o or 0) then o = 0 end
  if not isFinite(z) then z = 0 end
  armClientSync(x, y, z, o or 0, quiet)
  return true
end

function GmTeleport_Unlock()
  clearTaint()
  if type(GmTpUnlock) == "function" then
    pcall(GmTpUnlock)
  end
  SCH.ClearSync()
  if not SCH.NeedsWork() then
    SCH.Stop()
  end
  return true
end

function GmTeleport_SetHere()
  local x, y, z, o, map, src = Addon.ReadPose()
  if not x then
    chat("|cffff4444GmPlayerPose missing / no position — ExtProxy loaded?|r")
    return false
  end
  if not facingOk(o) then
    chat("|cffff8800facing invalid — turn left/right once, then Set here again|r")
    return false
  end
  local ok, detail = validateDest(x, y, z, o)
  if not ok then
    chat("|cffff4444set REJECTED|r — " .. tostring(detail))
    return false
  end
  GmTeleportDB.pin = {
    x = x, y = y, z = z, o = o, map = map,
    facingSrc = src, ground = detail,
  }
  chat(string.format("|cff2ecc71set|r %s  (%s, ground=%.1f)",
    fmtPin(GmTeleportDB.pin), src, tonumber(detail) or z))
  SCH.MarkUiDirty()
  return true
end

function GmTeleport_Go()
  local p = GmTeleportDB.pin
  if not p or not p.x then
    chat("|cffff4444no saved position — press Set here first|r")
    return false
  end
  if not Addon.HasTeleport() then
    chat("|cffff4444GmTeleport native missing — launch hooked client / ExtProxy|r")
    return false
  end

  -- Always clear prior sync/lock so the 2nd/3rd/... hop works the same as the first.
  GmTeleport_Unlock()

  local okPin, pinWhy = pinCoordsOk(p)
  if not okPin then
    chat("|cffff4444teleport REJECTED|r — " .. tostring(pinWhy))
    return false
  end

  local okNav, navDetail = validateDest(p.x, p.y, p.z or 0, p.o or 0)
  if not okNav then
    chat("|cffff4444teleport REJECTED|r — " .. tostring(navDetail))
    return false
  end

  clearTaint()
  local destZ = p.z or 0
  if type(GmShared) == "table" and type(GmShared.TeleportNav) == "function" then
    local navOk, nz = GmShared.TeleportNav(p.x, p.y, p.z or 0, p.o or 0, {
      lockMs = TP_LOCK_MS,
      map = p.map,
      clientSync = false,
    })
    if navOk then
      destZ = nz or destZ
      armClientSync(p.x, p.y, destZ, p.o or 0)
      chat(string.format("|cff2ecc71teleport OK|r (nav floor) syncing %.0fms… %s",
        (Addon.SYNC_HARD_SEC or 1) * 1000, fmtPin(p)))
      return true
    end
  end
  -- First hop: LoadEx-first. Sticky-lock retry: Raw only.
  local fnLoad = GmTeleport
  local fnRaw = GmTeleportRaw
  local usingLoad = type(fnLoad) == "function"
  local ok = nil
  if usingLoad then
    ok = fnLoad(p.x, p.y, destZ, p.o or 0, TP_FLAGS, TP_LOCK_MS)
  elseif type(fnRaw) == "function" then
    ok = fnRaw(p.x, p.y, destZ, p.o or 0, TP_FLAGS, TP_LOCK_MS)
  end

  if not ok or ok == 0 then
    -- Sticky lock / prior sync: unlock then Raw retry only (do not re-LoadEx).
    clearTaint()
    if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
    if type(fnRaw) == "function" then
      ok = fnRaw(p.x, p.y, destZ, p.o or 0, TP_FLAGS, TP_LOCK_MS)
      if ok and ok ~= 0 then usingLoad = false end
    end
  end

  if not ok or ok == 0 then
    chat("|cffff4444teleport FAILED|r — walk once in-world (seed move template), then retry")
    return false
  end

  armClientSync(p.x, p.y, destZ, p.o or 0)

  local mode = usingLoad and "load+FallLand" or "raw"
  chat(string.format("|cff2ecc71teleport OK|r (%s) syncing %.0fms… %s",
    mode, (Addon.SYNC_HARD_SEC or 1) * 1000, fmtPin(p)))
  return true
end

---------------------------------------------------------------------------
-- UI (interaction scripts only — no update loops)
---------------------------------------------------------------------------

local function makeBtn(parent, text, w, h, onClick)
  if GmUI and GmUI.Button then
    return GmUI.Button(parent, text, w or 120, h or 26, onClick, nil, "teleport")
  end
  local b = CreateFrame("Button", nil, parent, "UIPanelButtonTemplate")
  b:SetWidth(w or 120)
  b:SetHeight(h or 24)
  b:SetText(text)
  b:SetScript("OnClick", onClick)
  return b
end

local function createUI()
  local name = ADDON .. "UI"
  if _G[name] then return _G[name] end

  if GmUI and GmUI.CreateWindow and GmUI.TabBar then
    local f = GmUI.CreateWindow({
      id = "teleport", title = "GmTeleport", color = "teleport",
      width = 320, height = 200, x = 0, y = 120,
    })
    local r = f.body
    local pages = {}
    local function page()
      local p = CreateFrame("Frame", nil, r)
      p:SetPoint("TOPLEFT", 0, -32)
      p:SetPoint("BOTTOMRIGHT", 0, 0)
      p:Hide()
      return p
    end
    pages.bookmarks = page()
    pages.go = page()

    local function showTab(id)
      GmTeleportDB.lastTab = id
      for k, p in pairs(pages) do
        if k == id then p:Show() else p:Hide() end
      end
      if f.tabBar and f.tabBar.Select then f.tabBar:Select(id) end
    end

    f.tabBar = GmUI.TabBar(r, {
      { id = "bookmarks", label = "Bookmarks", w = 92, color = "teleport" },
      { id = "go", label = "Go", w = 48, color = "teleport" },
    }, showTab)
    f.tabBar:SetPoint("TOPLEFT", 0, 0)

    local pinLabel = GmUI.Label(pages.bookmarks, "", "GameFontHighlightSmall", "text")
    pinLabel:SetPoint("TOPLEFT", 0, -4)
    pinLabel:SetWidth(300)
    pinLabel:SetJustifyH("LEFT")
    f.pinLabel = pinLabel
    refreshPinLabel()
    makeBtn(pages.bookmarks, "Set here", 130, 30, GmTeleport_SetHere)
      :SetPoint("BOTTOMLEFT", 0, 4)

    makeBtn(pages.go, "Teleport", 160, 34, GmTeleport_Go)
      :SetPoint("TOP", 0, -20)
    GmUI.Muted(pages.go, "Load-teleport saved pin · /tpface [gap] for target")
      :SetPoint("TOPLEFT", 8, -64)
      :SetWidth(280)

    showTab(GmTeleportDB.lastTab or "bookmarks")

    f:HookScript("OnShow", function()
      SCH.MarkUiDirty()
      showTab(GmTeleportDB.lastTab or "bookmarks")
    end)
    _G[name] = f
    return f
  end

  local f = CreateFrame("Frame", name, UIParent)
  f:SetWidth(300)
  f:SetHeight(120)
  f:SetPoint("CENTER", 0, 120)
  f:EnableMouse(true)
  f:SetMovable(true)
  f:RegisterForDrag("LeftButton")
  f:SetScript("OnDragStart", f.StartMoving)
  f:SetScript("OnDragStop", f.StopMovingOrSizing)
  f:Hide()
  local pinLabel = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  pinLabel:SetPoint("TOPLEFT", 14, -40)
  f.pinLabel = pinLabel
  refreshPinLabel()
  makeBtn(f, "Set here", 120, 24, GmTeleport_SetHere):SetPoint("BOTTOMLEFT", 14, 14)
  makeBtn(f, "Teleport", 120, 24, GmTeleport_Go):SetPoint("BOTTOMRIGHT", -14, 14)
  f:SetScript("OnShow", function()
    SCH.MarkUiDirty()
  end)
  if GmShared and GmShared.ScaleHud then GmShared.ScaleHud(f) end
  return f
end

function GmTeleport_Toggle()
  local f = createUI()
  if f:IsShown() then f:Hide() else f:Show() end
end

local function normGuid(g)
  if not g then return nil end
  g = tostring(g):gsub("^0[xX]", ""):lower()
  return g
end

--- Instant teleport beside current target + face.
--- Macro: /tpface 5   OR   /run GmTeleport_TpFace(5)
function GmTeleport_TpFace(gap, quiet)
  gap = tonumber(gap) or 5
  if gap < 2 then gap = 2 end
  if gap > 35 then gap = 35 end
  local function say(msg)
    if quiet then return end
    chat(msg)
  end
  if not (UnitExists and UnitExists("target")) then
    say("|cffff5555tpface: no target|r")
    return false
  end
  if not Addon.HasTeleport() then
    say("|cffff5555tpface: ExtProxy GmTeleport missing|r — launch hooked client")
    return false
  end

  -- Same unlock path as /gmteleport go (sticky lock / prior sync breaks hops).
  GmTeleport_Unlock()
  clearTaint()
  if type(GmHwEvent) == "function" then pcall(GmHwEvent, 1) end
  if type(GmObjectPump) == "function" then pcall(GmObjectPump, 0) end

  local want = UnitGUID and normGuid(UnitGUID("target"))
  local tx, ty, tz = nil, nil, nil
  -- Canonical OmXyz (PushUnit r[3..5]); no local ByGuid pack duplicate.
  if want and type(GmShared) == "table" and type(GmShared.OmXyz) == "function" then
    tx, ty, tz = GmShared.OmXyz(want)
  end
  if not tx and want and type(GmObjectCount) == "function" and type(GmObjectInfo) == "function" then
    local n = GmObjectCount() or 0
    for i = 1, n do
      local g, x, y, z = GmObjectInfo(i)
      x, y, z = tonumber(x), tonumber(y), tonumber(z)
      if g and normGuid(g) == want and isFinite(x) and isFinite(y) then
        tx, ty, tz = x, y, (isFinite(z) and z) or 0
        break
      end
    end
  end
  -- Last resort: UnitPosition / GetPlayerMapPosition won't give world XYZ on 3.3.5;
  -- try GmTargetXYZ / GmUnitPos if present.
  if not tx and type(GmTargetXYZ) == "function" then
    local ok, x, y, z = pcall(GmTargetXYZ)
    x, y, z = tonumber(x), tonumber(y), tonumber(z)
    if ok and isFinite(x) and isFinite(y) then
      tx, ty, tz = x, y, (isFinite(z) and z) or 0
    end
  end
  if not (isFinite(tx) and isFinite(ty)) then
    say("|cffff5555tpface: no target XYZ (OM)|r — retarget / walk once")
    return false
  end
  if not isFinite(tz) then tz = 0 end

  local px, py = nil, nil
  if type(GmPlayerXYZ) == "function" then
    px, py = GmPlayerXYZ()
  elseif type(GmPlayerPose) == "function" then
    px, py = GmPlayerPose()
  end
  px, py = tonumber(px), tonumber(py)
  if not px then px, py = tx - gap, ty end

  local ang = math.atan2(py - ty, px - tx)
  local x = tx + math.cos(ang) * gap
  local y = ty + math.sin(ang) * gap
  local z = tz or 0
  if type(GmNavZ) == "function" then
    local map = (type(GmMapId) == "function" and GmMapId()) or 0
    local gz = GmNavZ(x, y, map, z)
    if gz and gz > -99999 then z = gz + 0.5 end
  end
  local o = math.atan2(ty - y, tx - x)
  if not facingOk(o) then o = 0 end

  local okNav, navDetail = validateDest(x, y, z, o)
  if not okNav then
    -- Soft: still attempt if only ground soft-fail; hard reject bad coords.
    if not pinCoordsOk({ x = x, y = y, z = z, o = o }) then
      say("|cffff5555tpface REJECTED|r — " .. tostring(navDetail))
      return false
    end
  end

  clearTaint()
  local fnRaw = GmTeleportRaw
  local fnLoad = GmTeleport
  local sent = nil
  -- Prefer Raw first (LoadEx often rejects offset/sky-ish pins); then LoadEx.
  if type(fnRaw) == "function" then
    sent = fnRaw(x, y, z, o, TP_FLAGS, TP_LOCK_MS)
  end
  if (not sent or sent == 0) and type(fnLoad) == "function" then
    sent = fnLoad(x, y, z, o, TP_FLAGS, TP_LOCK_MS)
  end
  if not sent or sent == 0 then
    clearTaint()
    if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
    -- Last resort: stand ON the target (gap 0) like HuntingBot Under.
    if type(fnRaw) == "function" then
      sent = fnRaw(tx, ty, tz or 0, o, TP_FLAGS, TP_LOCK_MS)
      if sent and sent ~= 0 then x, y, z = tx, ty, tz or 0 end
    end
    if (not sent or sent == 0) and type(fnLoad) == "function" then
      sent = fnLoad(tx, ty, tz or 0, o, TP_FLAGS, TP_LOCK_MS)
      if sent and sent ~= 0 then x, y, z = tx, ty, tz or 0 end
    end
  end

  if sent and sent ~= 0 then
    pcall(GmTeleport_ClientSync, x, y, z, o, true)
    if type(GmFaceTarget) == "function" then pcall(GmFaceTarget) end
    if type(GmFaceUnit) == "function" and want then pcall(GmFaceUnit, want) end
    -- Extra face after a tick of sync.
    if type(GmFaceTarget) == "function" then pcall(GmFaceTarget) end
    say(string.format("|cff2ecc71tpface|r %s gap=%.0f", UnitName("target") or "?", gap))
    return true
  end

  say("|cffff5555tpface failed|r — walk once in-world (seed move template), retry")
  return false
end

local function registerSlash()
  SLASH_GMTELEPORT1 = "/gmteleport"
  SLASH_GMTELEPORT2 = "/gmtp"
  SlashCmdList["GMTELEPORT"] = function(msg)
    msg = string.lower((msg or ""):match("^%s*(.-)%s*$") or "")
    if msg == "" or msg == "gui" or msg == "show" then
      GmTeleport_Toggle()
    elseif msg == "set" then
      GmTeleport_SetHere()
    elseif msg == "go" or msg == "tp" then
      GmTeleport_Go()
    elseif msg == "face" or msg:match("^face") then
      local g = msg:match("face%s+([%d%.]+)")
      GmTeleport_TpFace(g)
    else
      chat("usage: /gmteleport | set | go | face [gap]   · also /tpface [gap]")
    end
  end

  -- Macro-friendly. Also push into WotLK hash table so chat/macros resolve.
  SLASH_TPFACE1 = "/tpface"
  SLASH_TPFACE2 = "/tpf"
  SlashCmdList["TPFACE"] = function(msg)
    local gap = tonumber((msg or ""):match("([%d%.]+)"))
    GmTeleport_TpFace(gap)
  end
  local function pushHash(slash, fn)
    if type(hash_SlashCmdList) == "table" then
      hash_SlashCmdList[slash] = fn
    end
  end
  pushHash("/tpface", SlashCmdList["TPFACE"])
  pushHash("/tpf", SlashCmdList["TPFACE"])
  pushHash("/gmteleport", SlashCmdList["GMTELEPORT"])
  pushHash("/gmtp", SlashCmdList["GMTELEPORT"])
  if type(ChatFrame_ImportAllListsToHash) == "function" then
    pcall(ChatFrame_ImportAllListsToHash)
  end
end

registerSlash()

local boot = CreateFrame("Frame")
boot:RegisterEvent("PLAYER_LOGIN")
boot:SetScript("OnEvent", function()
  if _G.GmtEntitlementGate and not GmtEntitlementGate.RequireAddon("GmTeleport") then return end
  registerSlash()
  chat("v" .. VERSION .. " ready — macro: |cffffd200/tpface 5|r  or  |cffffd200/run GmTeleport_TpFace(5)|r")
end)
