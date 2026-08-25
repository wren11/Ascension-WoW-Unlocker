
GmMapTeleportDB = GmMapTeleportDB or {}

local C, S = {}, {}
local MT = {}
_G.GmMapTeleport = MT

local SCH = GmMapTeleportScheduler

C.VERSION = "1.5.8"
C.DB_REVISION = 3

C.TP_LOCK_MS = 2000
C.TP_FLAGS = 3
C.TP_LOCK_RADIUS = 28
C.UNLOCK_SEC = 0.05
C.JUMP_HOLD_SEC = 0.15
C.CLICK_DEBOUNCE = 0.35
C.SNAPSHOT_MAX_AGE = 1.5
C.ARRIVE_OK_YD = 5
C.CALIB_MAX_YD = 250
C.NAV_MISS = -99999

C.RING_RADII = { 0, 3, 6, 10, 16, 24, 32, 48 }
C.RING_STEPS = 8

S.lastClickAt = 0
S.sync = nil
S.jumpStopAt = nil
S.marker = nil
S.hint = nil
S.menu = nil
S.down = nil
S.hooked = false
S.uiDirty = false
S.hintKey = nil
S.markerArea = nil
S.markerNx = nil
S.markerNy = nil
S.markerW = nil

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmMapTeleport]|r " .. tostring(msg))
  end
end

local function DB()
  local d = GmMapTeleportDB
  local rev = tonumber(d.dbRevision) or 0
  -- Force working defaults for teleport (old SVs left enabled/allowAnywhere false).
  if rev < C.DB_REVISION then
    d.enabled = true
    d.allowAnywhere = true
    d.registerClicks = true
    d.showHint = true
    d.showMarker = true
    d.dbRevision = C.DB_REVISION
  end
  if d.enabled == nil then d.enabled = true end
  if d.registerClicks == nil then d.registerClicks = true end
  if d.showHint == nil then d.showHint = true end
  if d.showMarker == nil then d.showMarker = true end
  if d.jumpAfterTeleport == nil then d.jumpAfterTeleport = false end
  if d.allowAnywhere == nil then d.allowAnywhere = true end
  d.modifier = d.modifier or "none"
  d.menuButton = d.menuButton or "right"
  d.maxSnap = tonumber(d.maxSnap) or 48
  return d
end

local function clearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
end

local function nativeOk(v)
  return v ~= nil and v ~= false and v ~= 0
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

local function dist(ax, ay, az, bx, by, bz)
  local dx = (ax or 0) - (bx or 0)
  local dy = (ay or 0) - (by or 0)
  local dz = (az or 0) - (bz or 0)
  return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function hasNatives()
  return type(GmTeleportAnywhere) == "function"
    or type(GmTeleport) == "function"
    or type(GmTeleportRaw) == "function"
end

local function readPose()
  clearTaint()
  if type(GmPlayerPose) == "function" then
    local x, y, z, o, map = GmPlayerPose()
    if isFinite(x) and isFinite(y) and (math.abs(x) > 0.01 or math.abs(y) > 0.01) then
      if not facingOk(o) then o = 0 end
      return x, y, z or 0, o or 0, map or 0
    end
  end
  if type(GmPlayerXYZ) == "function" then
    local x, y, z, o = GmPlayerXYZ()
    if isFinite(x) and isFinite(y) then
      if not facingOk(o) then o = 0 end
      return x, y, z or 0, o or 0, nil
    end
  end
  return nil
end

local function currentMap()
  local _, _, _, _, map = readPose()
  if map ~= nil then return map end
  if type(GmMapId) == "function" then
    local m = GmMapId()
    if m ~= nil then return m end
  end
  return nil
end

local function navGroundZ(x, y, map, zHint)
  if type(GmNavZ) ~= "function" then return nil end
  local gz = GmNavZ(x, y, map or 0, zHint or 0)
  if gz and gz > C.NAV_MISS then return gz end
  return nil
end

local function isContinentMap(map)
  map = tonumber(map)
  return map == nil or map == 0 or map == 1 or map == 530 or map == 571
end

local function groundHints(zHint)
  -- Dummy 500 → lowest walkable sheet (NavHeightAt). Never lead with 3000
  -- (that used to return tree-tops).
  local hints = {}
  if isFinite(zHint) and zHint <= 400 and zHint >= -200 then
    hints[#hints + 1] = zHint
  end
  hints[#hints + 1] = 0
  hints[#hints + 1] = 500
  return hints
end

local function pickGroundZ(x, y, map, zHint)
  local hints = groundHints(zHint)
  local cands = {}
  local hi, i
  for hi = 1, #hints do
    local z = navGroundZ(x, y, map, hints[hi])
    if z then
      local dup = false
      for i = 1, #cands do
        if math.abs(cands[i] - z) < 0.15 then dup = true break end
      end
      if not dup then cands[#cands + 1] = z end
    end
  end
  if #cands == 0 then return nil end
  local hint = (isFinite(zHint) and zHint <= 400 and zHint >= -200) and zHint or nil
  local best
  -- Outdoor continents: lowest walkable (forest floor, not canopy).
  -- Instances: floor matching the hint (upper decks / multi-level).
  if hint and not isContinentMap(map) then
    local bestErr
    for i = 1, #cands do
      if cands[i] <= hint + 6 then
        local err = math.abs(cands[i] - hint)
        if not bestErr or err < bestErr then best, bestErr = cands[i], err end
      end
    end
  end
  if not best then
    best = cands[1]
    for i = 2, #cands do
      if cands[i] < best then best = cands[i] end
    end
  end
  return best
end

local function findGroundPin(wx, wy, o, map, zHint)
  local maxSnap = DB().maxSnap

  for ri = 1, #C.RING_RADII do
    local radius = C.RING_RADII[ri]
    if radius <= maxSnap then
      local steps = (radius == 0) and 1 or C.RING_STEPS
      for si = 0, steps - 1 do
        local ang = (si / steps) * 2 * math.pi
        local px = wx + math.cos(ang) * radius
        local py = wy + math.sin(ang) * radius
        local gz = pickGroundZ(px, py, map, zHint)
        if gz then
          return { x = px, y = py, z = gz, o = o }, radius
        end
      end
    end
  end
  return nil, nil, "no navmesh"
end

local function resolveMapRow()
  local rows = GmMapTeleport_MapAreas
  local byName = GmMapTeleport_MapAreaByName
  if not rows then return nil, nil, "MapAreas.lua did not load" end

  local id, texture
  if type(GetCurrentMapAreaID) == "function" then id = GetCurrentMapAreaID() end
  if type(GetMapInfo) == "function" then texture = GetMapInfo() end

  local byNameId = (texture and byName) and byName[texture] or nil
  if byNameId and rows[byNameId] then return rows[byNameId], byNameId end
  if id and rows[id] then return rows[id], id end

  local label = tostring(texture or "?") .. " / id " .. tostring(id or "?")
  return nil, id, "no WorldMapArea row for this map (" .. label .. ")"
end

local function mapLabel(id, row)
  if type(GetMapNameByID) == "function" and id then
    local name = GetMapNameByID(id)
    if name and name ~= "" then return name end
  end
  if row and row.n then return row.n end
  return "map " .. tostring(id or "?")
end

local function cursorToNorm()
  local canvas = WorldMapDetailFrame
  if not canvas then return nil, nil, "WorldMapDetailFrame missing" end
  local scale = canvas:GetEffectiveScale()
  if not scale or scale == 0 then return nil, nil, "map scale unavailable" end

  local left, top = canvas:GetLeft(), canvas:GetTop()
  local w, h = canvas:GetWidth(), canvas:GetHeight()
  if not left or not top or not w or not h or w == 0 or h == 0 then
    return nil, nil, "map geometry unavailable"
  end

  local cx, cy = GetCursorPosition()
  if not cx or not cy then return nil, nil, "no cursor position" end

  local nx = (cx / scale - left) / w
  local ny = (top - cy / scale) / h
  return nx, ny
end

local function projectToWorld(row, nx, ny)
  local wx = row.t - ny * (row.t - row.b)
  local wy = row.l - nx * (row.l - row.r)
  return wx, wy
end

local function rowProjectable(row)
  return row.l ~= row.r and row.t ~= row.b
end

local function calibrationError(row)
  if type(GetPlayerMapPosition) ~= "function" then return nil end
  local px, py = GetPlayerMapPosition("player")
  if not px or not py then return nil end
  if px <= 0 and py <= 0 then return nil end
  local wx, wy = projectToWorld(row, px, py)
  local ax, ay = readPose()
  if not ax then return nil end
  local dx, dy = wx - ax, wy - ay
  return math.sqrt(dx * dx + dy * dy)
end

local function wake(immediate)
  if SCH and SCH.RequestTick then
    SCH.RequestTick(immediate and true or false)
  end
end

local function markUiDirty()
  S.uiDirty = true
  wake(true)
end

--------------------------------------------------------------------------
-- Teleport path + sync arming (interactive; scheduler consumes unlock)
--------------------------------------------------------------------------

local function armSync(pin, quiet)
  -- ClientSync contract (stable across consumers): (x, y, z, o, quiet)
  if type(GmTeleport_ClientSync) == "function" then
    GmTeleport_ClientSync(pin.x, pin.y, pin.z or 0, pin.o or 0, quiet)
    return
  end
  S.sync = {
    x = pin.x, y = pin.y, z = pin.z or 0, o = pin.o or 0,
    unlockAt = GetTime() + C.UNLOCK_SEC,
    quiet = quiet and true or false,
  }
  clearTaint()
  if type(GmTpLock) == "function" then
    pcall(GmTpLock, S.sync.x, S.sync.y, S.sync.z, S.sync.o, C.TP_LOCK_MS, C.TP_LOCK_RADIUS)
  end
  if type(GmTpPulse) == "function" then pcall(GmTpPulse) end
  if type(GmSetFacing) == "function" then pcall(GmSetFacing, S.sync.o) end
  if type(GmTeleportRaw) == "function" then
    pcall(GmTeleportRaw, S.sync.x, S.sync.y, S.sync.z, S.sync.o, 3, C.TP_LOCK_MS)
  end
  wake(true)
end

local function emitAscTp(pin)
  pcall(function()
    local path = [[C:\Ascension\Launcher\resources\ascension-live\ALB_bridge.txt]]
    local f = io and io.open and io.open(path, "a")
    if not f then return end
    local line = string.format("ASC:TP:%.4f:%.4f:%.4f:%.4f",
      pin.x, pin.y, pin.z or 0, pin.o or 0)
    if pin.map ~= nil then line = line .. string.format(":%d", pin.map) end
    f:write(line .. "\n")
    f:close()
  end)
end

local function teleportTo(pin, reason)
  if not pin or not isFinite(pin.x) or not isFinite(pin.y) then return false end
  if not facingOk(pin.o or 0) then pin.o = 0 end
  if not isFinite(pin.z) then pin.z = 0 end

  local map = pin.map
  local here = currentMap()
  local pathUsed = nil
  local mapUsed = map

  clearTaint()
  if type(GmShared) == "table" and type(GmShared.TpGuardPulse) == "function" then
    pcall(GmShared.TpGuardPulse)
  end
  if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end

  -- Travel hops land ON navmesh. Hunt under-object loot is not this path.
  if type(GmShared) == "table" and type(GmShared.TeleportNav) == "function" then
    local navOk, destZ = GmShared.TeleportNav(pin.x, pin.y, pin.z, pin.o or 0, {
      lockMs = C.TP_LOCK_MS,
      map = map,
      clientSync = false,
    })
    if nativeOk(navOk) then
      if destZ then pin.z = destZ end
      pathUsed = "nav"
    end
  end

  local function unlockBetween()
    clearTaint()
    if type(GmTpUnlock) == "function" then pcall(GmTpUnlock) end
  end

  -- Raw-first: LoadEx often rejects map-cursor / snap pins that are slightly off mesh.
  if not pathUsed and type(GmTeleportRaw) == "function" then
    local sent = GmTeleportRaw(pin.x, pin.y, pin.z, pin.o or 0, C.TP_FLAGS, C.TP_LOCK_MS)
    if nativeOk(sent) then pathUsed = "raw" end
  end

  if not pathUsed and type(GmTeleport) == "function" then
    unlockBetween()
    local sent = GmTeleport(pin.x, pin.y, pin.z, pin.o or 0, C.TP_FLAGS, C.TP_LOCK_MS)
    if nativeOk(sent) then pathUsed = "load" end
  end

  -- Same-map → NgExplore map=-1 (EE-light); cross-map keeps pin.map (CrossTry).
  if not pathUsed and type(GmTeleportNgExplore) == "function" then
    unlockBetween()
    local mid
    if map ~= nil and map >= 0 and here ~= nil and tonumber(map) ~= tonumber(here) then
      mid = map
    else
      mid = -1
    end
    local sent = GmTeleportNgExplore(pin.x, pin.y, pin.z, pin.o or 0, mid, C.TP_FLAGS, C.TP_LOCK_MS)
    if nativeOk(sent) then
      pathUsed = "ng-light"
      mapUsed = mid
    end
  end
  if not pathUsed and type(GmTeleportNgEe) == "function" then
    unlockBetween()
    local sent = GmTeleportNgEe(pin.x, pin.y, pin.z, pin.o or 0, C.TP_FLAGS, C.TP_LOCK_MS)
    if nativeOk(sent) then pathUsed = "ng-ee" end
  end

  -- Last resort: same-map heartbeat (never WORLD_TELEPORT).
  if not pathUsed and type(GmTeleportRaw) == "function" then
    unlockBetween()
    local sent = GmTeleportRaw(pin.x, pin.y, pin.z, pin.o or 0, C.TP_FLAGS, C.TP_LOCK_MS)
    if nativeOk(sent) then
      pathUsed = "raw-ee"
      mapUsed = here
    end
  end

  emitAscTp(pin)

  if not pathUsed then
    chat("|cffff4444teleport failed|r — walk once in-world so ExtProxy has a move template, then retry")
    return false
  end

  clearTaint()
  if type(GmSetFacing) == "function" then pcall(GmSetFacing, pin.o or 0) end
  armSync(pin)

  local db = DB()
  db.lastDest = { x = pin.x, y = pin.y, z = pin.z or 0, o = pin.o or 0, map = pin.map }
  chat(string.format("|cff2ecc71teleport|r %.1f, %.1f, %.1f  map %s  [%s]%s",
    pin.x, pin.y, pin.z or 0, tostring(mapUsed or here), pathUsed,
    reason and (" — " .. reason) or ""))
  return true
end

--------------------------------------------------------------------------
-- Marker / hint UI (interactive helpers + snapshot-driven consumers)
--------------------------------------------------------------------------

local function ensureMarker()
  if S.marker then return S.marker end
  if not WorldMapButton then return nil end
  local edge = WorldMapButton:CreateTexture(nil, "OVERLAY")
  edge:SetTexture(0, 0, 0, 0.85)
  edge:SetWidth(12)
  edge:SetHeight(12)
  edge:Hide()
  local fill = WorldMapButton:CreateTexture(nil, "OVERLAY")
  fill:SetTexture(1, 0.82, 0.1, 1)
  fill:SetWidth(6)
  fill:SetHeight(6)
  fill:SetPoint("CENTER", edge, "CENTER", 0, 0)
  fill:Hide()
  edge.fill = fill
  S.marker = edge
  return edge
end

local function hideMarker()
  if not S.marker then return end
  S.marker:Hide()
  S.marker.fill:Hide()
end

local function showMarker(nx, ny, areaId)
  if not DB().showMarker then return end
  local t = ensureMarker()
  if not t or not WorldMapDetailFrame then return end
  S.markerArea = areaId
  S.markerNx, S.markerNy = nx, ny

  local _, viewed = resolveMapRow()
  if viewed ~= areaId then
    hideMarker()
    return
  end

  local w, h = WorldMapDetailFrame:GetWidth(), WorldMapDetailFrame:GetHeight()
  if not w or w == 0 then return end
  S.markerW = w
  t:ClearAllPoints()
  t:SetPoint("CENTER", WorldMapDetailFrame, "TOPLEFT", nx * w, -ny * h)
  t:Show()
  t.fill:Show()
end

local function refreshMarker(snap)
  if not S.marker or not S.markerArea then return end
  local show = snap and snap.settings and snap.settings.showMarker
  if show == nil then show = DB().showMarker end
  if not show then
    hideMarker()
    return
  end

  local viewed = snap and snap.mapAreaId
  if viewed == nil then
    local _, id = resolveMapRow()
    viewed = id
  end
  if viewed ~= S.markerArea then
    hideMarker()
    return
  end

  local w = snap and snap.mapWidth
  if not w or w == 0 then
    w = WorldMapDetailFrame and WorldMapDetailFrame:GetWidth()
  end
  if S.marker:IsShown() and w == S.markerW then return end
  showMarker(S.markerNx, S.markerNy, S.markerArea)
end

local function ensureHint()
  if S.hint then return S.hint end
  if not WorldMapDetailFrame then return nil end
  local fs = WorldMapButton and WorldMapButton:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  if not fs then return nil end
  fs:SetPoint("BOTTOMRIGHT", WorldMapDetailFrame, "BOTTOMRIGHT", -12, 10)
  fs:SetJustifyH("RIGHT")
  S.hint = fs
  return fs
end

local MENU_GESTURE = {
  ["right"] = "right-click",
  ["ctrl-right"] = "ctrl+right-click",
  ["alt-right"] = "alt+right-click",
  ["middle"] = "middle-click",
  ["left"] = "left-click",
}

local function refreshHint(snap)
  local settings = snap and snap.settings
  local enabled = settings and settings.enabled
  local showHint = settings and settings.showHint
  local modifier = settings and settings.modifier
  local menuButton = settings and settings.menuButton
  if enabled == nil or showHint == nil or not modifier or not menuButton then
    local db = DB()
    enabled = db.enabled and true or false
    showHint = db.showHint and true or false
    modifier = db.modifier
    menuButton = db.menuButton
  end

  local key = table.concat({
    tostring(showHint), tostring(enabled), tostring(modifier), tostring(menuButton),
  }, "/")
  if key == S.hintKey and S.hint then return end
  local fs = ensureHint()
  if not fs then return end
  S.hintKey = key
  if not showHint then
    fs:Hide()
    return
  end

  local parts = {}
  local gesture = MENU_GESTURE[menuButton]
  if gesture then
    parts[#parts + 1] = "|cff00b4d8" .. gesture .. "|r for teleport menu"
  end
  if enabled and menuButton ~= "middle" then
    local mod = (modifier ~= "none") and (modifier .. "+") or ""
    parts[#parts + 1] = "|cff00b4d8" .. mod .. "middle-click|r to teleport"
  end
  if #parts == 0 then
    parts[#parts + 1] = "|cff8c96a5map teleport off (/maptp on)|r"
  end
  fs:SetText(table.concat(parts, "  ·  "))
  fs:Show()
end

local function modifierHeld()
  local mod = DB().modifier
  if mod == "alt" then return IsAltKeyDown and IsAltKeyDown() end
  if mod == "ctrl" then return IsControlKeyDown and IsControlKeyDown() end
  if mod == "shift" then return IsShiftKeyDown and IsShiftKeyDown() end
  return true
end

local function resolveMapPoint(row, areaId, nx, ny)
  local label = mapLabel(areaId, row)
  if not rowProjectable(row) then
    return nil, label .. " has no world bounds in WorldMapArea — nothing to project"
  end
  nx, ny = tonumber(nx), tonumber(ny)
  if not (isFinite(nx) and isFinite(ny)) then
    return nil, "no map coordinates for that point"
  end
  if nx < 0 then nx = 0 elseif nx > 1 then nx = 1 end
  if ny < 0 then ny = 0 elseif ny > 1 then ny = 1 end

  local wx, wy = projectToWorld(row, nx, ny)
  if not isFinite(wx) or not isFinite(wy) then
    return nil, "projection produced non-finite coordinates"
  end

  local _, _, pz, po = readPose()
  local o = facingOk(po) and po or 0
  local zFallback = isFinite(pz) and pz or 0

  local ctx = {
    nx = nx, ny = ny, areaId = areaId, row = row, label = label,
    wx = wx, wy = wy, mapId = row.m,
  }

  local pin, snap = findGroundPin(wx, wy, o, row.m, zFallback)
  if pin then
    pin.map = row.m
    ctx.pin = pin
    ctx.snap = snap or 0
    ctx.zSource = "navmesh"
  else
    ctx.pin = { x = wx, y = wy, z = zFallback, o = o, map = row.m }
    ctx.snap = 0
    ctx.zSource = "player-z"
  end
  return ctx
end

local function resolveCursorTarget(clickSnap)
  if not hasNatives() then
    return nil, "ExtProxy natives missing — fully quit Ascension, run tools\\apply-extproxy.ps1"
  end

  if WorldMapFrame and WorldMapFrame.IsVisible and not WorldMapFrame:IsVisible() then
    return nil, "open the world map first"
  end

  local liveRow, liveId, rowWhy = resolveMapRow()
  local row, areaId = liveRow, liveId
  local rows = GmMapTeleport_MapAreas
  if clickSnap and clickSnap.areaId and rows and rows[clickSnap.areaId] then
    row, areaId = rows[clickSnap.areaId], clickSnap.areaId
  end
  if not row then return nil, rowWhy end

  local nx, ny, normWhy
  if clickSnap then nx, ny = clickSnap.nx, clickSnap.ny end
  if not nx then nx, ny, normWhy = cursorToNorm() end
  if not nx then return nil, normWhy end

  return resolveMapPoint(row, areaId, nx, ny)
end

local function teleportToCtx(ctx)
  if not ctx or not ctx.pin then return false end

  local note = ctx.note or ctx.label
  if ctx.snap and ctx.snap > 0 then
    note = string.format("%s (snapped %d yd to ground)", note, ctx.snap)
  elseif ctx.zSource == "player-z" then
    note = string.format("%s (Z = player %.1f)", note, ctx.pin.z or 0)
  end
  if not teleportTo(ctx.pin, note) then return false end
  showMarker(ctx.nx, ctx.ny, ctx.areaId)
  return true
end

function MT.TeleportToCursor(snapshot)
  local ctx, why = resolveCursorTarget(snapshot)
  if not ctx or not ctx.pin then
    if why then chat("|cffff8800" .. tostring(why) .. "|r") end
    return false
  end
  return teleportToCtx(ctx)
end

local function apiRow(areaId)
  local rows = GmMapTeleport_MapAreas
  if not rows then return nil, "GmMapTeleport's map table did not load" end
  local id = tonumber(areaId)
  if not id then return nil, "no map id for that point" end
  local row = rows[id]
  if not row then return nil, "unknown map (WorldMapArea " .. id .. ")" end
  if not rowProjectable(row) then
    return nil, mapLabel(id, row) .. " has no world bounds to project"
  end
  return row, nil, id
end

function MT.MapPointStatus(areaId)
  if not hasNatives() then return false, "ExtProxy natives missing" end
  local row, why = apiRow(areaId)
  if not row then return false, why end
  return true, nil
end

function MT.ProjectMapPoint(areaId, nx, ny)
  local row, _, id = apiRow(areaId)
  if not row then return nil end
  local x, y = tonumber(nx), tonumber(ny)
  if not (isFinite(x) and isFinite(y)) then return nil end
  local wx, wy = projectToWorld(row, x, y)
  if not isFinite(wx) or not isFinite(wy) then return nil end
  return wx, wy, row.m, id
end

function MT.TeleportToMapPoint(areaId, nx, ny, label)
  if not hasNatives() then
    local why = "ExtProxy natives missing — fully quit Ascension, run tools\\apply-extproxy.ps1"
    chat("|cffff8800" .. why .. "|r")
    return false, why
  end

  local row, rowWhy, id = apiRow(areaId)
  if not row then
    chat("|cffff8800" .. tostring(rowWhy) .. "|r")
    return false, rowWhy
  end

  local ctx, why = resolveMapPoint(row, id, tonumber(nx), tonumber(ny))
  if not ctx or not ctx.pin then
    chat("|cffff8800" .. tostring(why or "could not project that pin") .. "|r")
    return false, why
  end

  if label and label ~= "" then
    ctx.note = tostring(label) .. " (" .. ctx.label .. ")"
  end
  if not teleportToCtx(ctx) then
    return false, "inject failed"
  end
  return true, nil
end

function MT.TeleportXYZ(x, y, z, o, map)
  return teleportTo({
    x = tonumber(x), y = tonumber(y), z = tonumber(z) or 0,
    o = tonumber(o) or 0, map = map,
  }, "xyz")
end

function MT.LastWorld()
  local d = DB().lastDest
  if not d or not isFinite(d.x) then return nil end
  return d.x, d.y, d.z, d.map
end

function MT.CursorWorld()
  local ctx = resolveCursorTarget()
  if not ctx then return nil end
  local pin = ctx.pin
  if pin and isFinite(pin.x) then
    return pin.x, pin.y, pin.z or 0, ctx.mapId, ctx.nx, ctx.ny, ctx.areaId, ctx.label
  end
  if isFinite(ctx.wx) then
    return ctx.wx, ctx.wy, 0, ctx.mapId, ctx.nx, ctx.ny, ctx.areaId, ctx.label
  end
  return nil
end

local function menuFrame()
  if S.menu then return S.menu end
  S.menu = CreateFrame("Frame", "GmMapTeleportContextMenu", UIParent, "UIDropDownMenuTemplate")
  return S.menu
end

local function toggleEntry(text, field, after)
  return {
    text = text,
    checked = DB()[field] and true or false,
    keepShownOnClick = false,
    func = function()
      local db = DB()
      db[field] = not db[field]
      if after then after() end
    end,
  }
end

local function buildMenuList(ctx)
  local list = {}

  list[#list + 1] = { text = ctx.label, isTitle = true, notCheckable = true }
  list[#list + 1] = {
    text = string.format("|cff8c96a5%.1f, %.1f  ·  world %.0f, %.0f|r",
      ctx.nx * 100, ctx.ny * 100, ctx.wx, ctx.wy),
    notCheckable = true, disabled = true,
  }

  list[#list + 1] = {
    text = "|cff2ecc71Teleport here|r" .. (ctx.snap and ctx.snap > 0
      and string.format(" |cff8c96a5(+%d yd)|r", ctx.snap) or ""),
    notCheckable = true,
    func = function() teleportToCtx(ctx) end,
  }
  if ctx.pin then
    list[#list + 1] = {
      text = string.format("|cff8c96a5Z %.1f (%s)|r", ctx.pin.z or 0, ctx.zSource or "?"),
      notCheckable = true, disabled = true,
    }
  end

  list[#list + 1] = {
    text = "Print coordinates to chat",
    notCheckable = true,
    func = function()
      local z = ctx.pin and ctx.pin.z or nil
      chat(string.format("%s — x %.2f  y %.2f  z %s  map %s", ctx.label, ctx.wx, ctx.wy,
        z and string.format("%.2f", z) or "?", tostring(ctx.mapId)))
    end,
  }
  list[#list + 1] = {
    text = "Save as ActionFlow location",
    notCheckable = true,
    func = function()
      if not (_G.ActionFlow and ActionFlow.MapHook) then
        chat("|cffff8800ActionFlow not loaded|r")
        return
      end
      local z = ctx.pin and ctx.pin.z or 0
      ActionFlow.MapHook.AddWorld(ctx.wx, ctx.wy, z, ctx.mapId, ctx.label)
    end,
  }

  list[#list + 1] = { text = "", notCheckable = true, disabled = true }
  list[#list + 1] = toggleEntry("Middle-click teleports instantly", "enabled", function()
    markUiDirty()
  end)
  list[#list + 1] = toggleEntry("Destination marker", "showMarker", function()
    markUiDirty()
  end)
  list[#list + 1] = toggleEntry("Hint text on map", "showHint", function()
    markUiDirty()
  end)
  list[#list + 1] = { text = "Cancel", notCheckable = true, func = function() end }
  return list
end

function MT.OpenMenu(snapshot)
  local ctx, why = resolveCursorTarget(snapshot)
  if not ctx then
    chat("|cffff8800" .. tostring(why) .. "|r")
    return false
  end

  showMarker(ctx.nx, ctx.ny, ctx.areaId)

  local list = buildMenuList(ctx)
  local frame = menuFrame()
  if type(EasyMenu) == "function" then
    EasyMenu(list, frame, "cursor", 0, 0, "MENU", 2)
    return true
  end
  UIDropDownMenu_Initialize(frame, function(_, level)
    for i = 1, #list do UIDropDownMenu_AddButton(list[i], level) end
  end, "MENU")
  ToggleDropDownMenu(1, nil, frame, "cursor", 0, 0)
  return true
end

local function menuButtonMatches(button)
  local want = DB().menuButton
  if want == "none" then return false end
  if want == "middle" then return button == "MiddleButton" end
  if want == "left" then return button == "LeftButton" end
  if want == "ctrl-right" then
    return button == "RightButton" and IsControlKeyDown and IsControlKeyDown()
  end
  if want == "alt-right" then
    return button == "RightButton" and IsAltKeyDown and IsAltKeyDown()
  end
  return button == "RightButton"
end

local function onMapMouseDown(button)
  local id
  if type(GetCurrentMapAreaID) == "function" then id = GetCurrentMapAreaID() end
  local nx, ny = cursorToNorm()
  S.down = { button = button, areaId = id, nx = nx, ny = ny, t = GetTime() }
end

local function takeClickSnapshot(button)
  local d = S.down
  S.down = nil
  if not d or d.button ~= button then return nil end
  if not d.nx or (GetTime() - d.t) > C.SNAPSHOT_MAX_AGE then return nil end
  return d
end

local function onMapClick(button)
  local db = DB()
  local now = GetTime()
  if (now - S.lastClickAt) < C.CLICK_DEBOUNCE then return end

  local wantsMenu = menuButtonMatches(button)
  local wantsTeleport = (button == "MiddleButton") and db.menuButton ~= "middle"
    and db.enabled and modifierHeld()
  if not wantsMenu and not wantsTeleport then return end

  local snapshot = takeClickSnapshot(button)
  S.lastClickAt = now
  if wantsMenu then
    MT.OpenMenu(snapshot)
  else
    MT.TeleportToCursor(snapshot)
  end
end

local function hookMap()
  if S.hooked then return end
  local btn = WorldMapButton
  if not btn then return end

  local function attach(frame, script)
    if not frame then return end
    if frame.HookScript then
      frame:HookScript(script, function(_, button) onMapClick(button) end)
      return
    end
    local prev = frame:GetScript(script)
    frame:SetScript(script, function(self, button, ...)
      if prev then prev(self, button, ...) end
      onMapClick(button)
    end)
  end

  if btn.HookScript then
    btn:HookScript("OnMouseDown", function(_, button) onMapMouseDown(button) end)
  else
    local prev = btn:GetScript("OnMouseDown")
    btn:SetScript("OnMouseDown", function(self, button, ...)
      if prev then prev(self, button, ...) end
      onMapMouseDown(button)
    end)
  end

  attach(btn, "OnMouseUp")
  attach(btn, "OnClick")
  if DB().registerClicks and btn.RegisterForClicks then
    pcall(btn.RegisterForClicks, btn, "LeftButtonUp", "RightButtonUp", "MiddleButtonUp")
  end

  S.hooked = true
end

function MT.IsEnabled()
  return DB().enabled and true or false
end

function MT.SetEnabled(on)
  DB().enabled = on and true or false
  markUiDirty()
  chat(DB().enabled and "|cff2ecc71enabled|r — middle-click the world map to teleport"
    or "|cffff8800instant middle-click teleport disabled|r (the menu still works)")
end

function MT.SetMenuButton(which)
  if which ~= "none" and not MENU_GESTURE[which] then return false end
  DB().menuButton = which
  markUiDirty()
  chat("teleport menu: " .. (MENU_GESTURE[which] or "disabled"))
  return true
end

function MT.Toggle()
  MT.SetEnabled(not DB().enabled)
end

function MT.Resync()
  local d = DB().lastDest
  if not d or not d.x then
    chat("|cffff4444no previous destination to resync to|r")
    return false
  end
  armSync(d)
  chat(string.format("re-locking client to %.1f, %.1f, %.1f", d.x, d.y, d.z or 0))
  return true
end

function MT.Again()
  local d = DB().lastDest
  if not d or not d.x then
    chat("|cffff4444no previous destination|r")
    return false
  end
  return teleportTo({ x = d.x, y = d.y, z = d.z, o = d.o, map = d.map }, "last destination")
end

function MT.Unlock()
  if type(GmTpUnlock) == "function" then
    clearTaint()
    pcall(GmTpUnlock)
  end
  S.sync = nil
  S.jumpStopAt = nil
  if JumpOrAscendStop then pcall(JumpOrAscendStop) end
  if SCH and SCH.Stop then SCH.Stop() end
  chat("|cffff8800position lock released|r")
end

function MT.Check()
  local row, areaId, why = resolveMapRow()
  if not row then
    chat("|cffff4444" .. tostring(why) .. "|r")
    return
  end
  chat(string.format("map %s (id %s, area %d, client map %d)",
    mapLabel(areaId, row), tostring(areaId), row.a or -1, row.m or -1))
  chat(string.format("bounds  X %.1f .. %.1f   Y %.1f .. %.1f",
    row.b, row.t, row.r, row.l))

  local x, y, z, o, map = readPose()
  if x then
    local here = map or currentMap()
    chat(string.format("player  %.1f, %.1f, %.1f  facing %.3f  map %s",
      x, y, z, o, tostring(here)))
    local gz = navGroundZ(x, y, here, z)
    if gz then
      chat(string.format("|cff2ecc71navmesh: ground %.1f under your feet (%.1f off)|r", gz, z - gz))
    else
      chat("|cff8c96a5navmesh: no tile here — teleports still fire using player Z|r")
    end
  else
    chat("|cffff4444player position unavailable|r")
  end

  if not rowProjectable(row) then
    chat("|cffff8800this map has collapsed bounds — it cannot be projected|r")
    return
  end
  local err = calibrationError(row)
  if err then
    local colour = (err <= C.CALIB_MAX_YD) and "|cff2ecc71" or "|cffff4444"
    chat(string.format("%scalibration: player blip projects %.1f yd from real XY|r", colour, err))
  else
    chat("|cff8c96a5calibration: player is not on the viewed map (nothing to compare)|r")
  end
end

--------------------------------------------------------------------------
-- Scheduler: collector + pure consumers (snapshot in, side-effects out)
--------------------------------------------------------------------------

local function buildSchedulerSnapshot()
  local now = GetTime()
  local db = DB()

  local px, py, pz, po, pmap = readPose()
  local player = nil
  if px then
    player = { x = px, y = py, z = pz, o = po, map = pmap }
  end

  local mapVisible = false
  if WorldMapFrame and WorldMapFrame.IsVisible then
    mapVisible = WorldMapFrame:IsVisible() and true or false
  end

  local needMap = S.uiDirty or mapVisible
  local mapAreaId, mapTexture, mapRow, mapRowWhy = nil, nil, nil, nil
  if needMap then
    mapRow, mapAreaId, mapRowWhy = resolveMapRow()
    if type(GetMapInfo) == "function" then
      mapTexture = GetMapInfo()
    end
  end

  local mapWidth = 0
  if WorldMapDetailFrame then
    mapWidth = WorldMapDetailFrame:GetWidth() or 0
  end

  local sync = nil
  local syncPending = false
  if S.sync then
    local ready = now >= (S.sync.unlockAt or 0)
    sync = {
      x = S.sync.x,
      y = S.sync.y,
      z = S.sync.z,
      o = S.sync.o,
      unlockAt = S.sync.unlockAt,
      quiet = S.sync.quiet and true or false,
      ready = ready,
    }
    syncPending = not ready
  end

  local jumpReady = false
  local jumpPending = false
  if S.jumpStopAt then
    jumpReady = now >= S.jumpStopAt
    jumpPending = not jumpReady
  end

  local hasWork = (S.sync ~= nil) or (S.jumpStopAt ~= nil) or S.uiDirty
  local keepAlive = syncPending or jumpPending

  return {
    now = now,
    player = player,
    mapVisible = mapVisible,
    mapAreaId = mapAreaId,
    mapTexture = mapTexture,
    mapRow = mapRow and {
      m = mapRow.m, a = mapRow.a, n = mapRow.n,
      l = mapRow.l, r = mapRow.r, t = mapRow.t, b = mapRow.b,
    } or nil,
    mapRowWhy = mapRowWhy,
    mapWidth = mapWidth,
    sync = sync,
    jumpStopAt = S.jumpStopAt,
    jumpReady = jumpReady,
    marker = {
      areaId = S.markerArea,
      nx = S.markerNx,
      ny = S.markerNy,
      width = S.markerW,
      shown = (S.marker and S.marker.IsShown and S.marker:IsShown()) and true or false,
    },
    uiDirty = S.uiDirty and true or false,
    hintKey = S.hintKey,
    settings = {
      enabled = db.enabled and true or false,
      showHint = db.showHint and true or false,
      showMarker = db.showMarker and true or false,
      modifier = db.modifier,
      menuButton = db.menuButton,
      maxSnap = db.maxSnap,
    },
    hasWork = hasWork,
    keepAlive = keepAlive,
  }
end

local function consumeSync(snap)
  local s = snap.sync
  if not s or not s.ready then return end
  if not S.sync then return end

  S.sync = nil
  clearTaint()
  if type(GmFreeMove) == "function" then
    pcall(GmFreeMove)
  elseif type(GmTpUnlock) == "function" then
    pcall(GmTpUnlock)
  end
  if type(GmSetFacing) == "function" then pcall(GmSetFacing, s.o) end
  if s.quiet then return end

  local p = snap.player
  if not p then
    chat("|cffff8800unlocked — client reports no position yet|r")
    return
  end
  local d = dist(p.x, p.y, p.z, s.x, s.y, s.z)
  if d <= C.ARRIVE_OK_YD then
    chat(string.format("|cff2ecc71arrived|r %.1f, %.1f, %.1f (within %.1f yd)",
      s.x, s.y, s.z, d))
  else
    chat(string.format(
      "|cffff8800streaming|r client %.0f yd from pin — wait a moment for ADT", d))
  end
end

local function consumeJump(snap)
  if not snap.jumpReady then return end
  if not S.jumpStopAt then return end
  S.jumpStopAt = nil
  clearTaint()
  if JumpOrAscendStop then pcall(JumpOrAscendStop) end
end

local function consumeHint(snap)
  if not snap.uiDirty then return end
  refreshHint(snap)
end

local function consumeMarker(snap)
  if not snap.uiDirty then return end
  refreshMarker(snap)
end

local function consumeUiAck(snap)
  if snap.uiDirty then
    S.uiDirty = false
  end
end

BINDING_HEADER_GMMAPTELEPORT = "GmMapTeleport"
BINDING_NAME_GMMAPTELEPORT_CURSOR = "Teleport to map cursor"

local function pushHash(slash, fn)
  if type(hash_SlashCmdList) == "table" then
    hash_SlashCmdList[slash] = fn
  end
end

local function registerSlash()
  SLASH_GMMAPTELEPORT1 = "/maptp"
  SLASH_GMMAPTELEPORT2 = "/gmmaptp"
  SlashCmdList["GMMAPTELEPORT"] = function(msg)
    msg = string.lower(string.match(msg or "", "^%s*(.-)%s*$") or "")
    local cmd, rest = string.match(msg, "^(%S*)%s*(.*)$")

    if cmd == "" or cmd == "status" then
      local db = DB()
      chat(string.format("v%s — instant %s, menu on %s, modifier %s, snap %d yd, marker %s, hint %s",
        C.VERSION, db.enabled and "|cff2ecc71on|r" or "|cffff8800off|r",
        MENU_GESTURE[db.menuButton] or "|cffff8800nothing|r",
        db.modifier, db.maxSnap,
        db.showMarker and "on" or "off", db.showHint and "on" or "off"))
      MT.Check()
    elseif cmd == "menu" then
      if rest == "" then
        MT.OpenMenu()
      elseif not MT.SetMenuButton(rest) then
        chat("usage: /maptp menu right|ctrl-right|alt-right|middle|left|none   (no argument opens it)")
      end
    elseif cmd == "on" then
      MT.SetEnabled(true)
    elseif cmd == "off" then
      MT.SetEnabled(false)
    elseif cmd == "toggle" then
      MT.Toggle()
    elseif cmd == "check" then
      MT.Check()
    elseif cmd == "again" then
      MT.Again()
    elseif cmd == "resync" then
      MT.Resync()
    elseif cmd == "unlock" then
      MT.Unlock()
    elseif cmd == "marker" then
      DB().showMarker = (rest ~= "off")
      if not DB().showMarker then hideMarker() else markUiDirty() end
      chat("marker " .. (DB().showMarker and "on" or "off"))
    elseif cmd == "hint" then
      DB().showHint = (rest ~= "off")
      markUiDirty()
      chat("hint " .. (DB().showHint and "on" or "off"))
    elseif cmd == "snap" then
      local n = tonumber(rest)
      if not n or n < 0 or n > 200 then
        chat("usage: /maptp snap <0-200>   (yards the ground search may move the pin)")
      else
        DB().maxSnap = n
        chat(string.format("ground snap limit %d yd", n))
      end
    elseif cmd == "modifier" or cmd == "mod" then
      if rest == "none" or rest == "alt" or rest == "ctrl" or rest == "shift" then
        DB().modifier = rest
        markUiDirty()
        chat("modifier " .. rest)
      else
        chat("usage: /maptp modifier none|alt|ctrl|shift")
      end
    else
      chat("usage: /maptp on|off|toggle|status|check|again|resync|unlock")
      chat("       /maptp menu [right|ctrl-right|alt-right|middle|left|none]")
      chat("       /maptp marker on|off · hint on|off · snap <yd> · modifier none|alt|ctrl|shift")
    end
  end
  pushHash("/maptp", SlashCmdList["GMMAPTELEPORT"])
  pushHash("/gmmaptp", SlashCmdList["GMMAPTELEPORT"])
  if type(ChatFrame_ImportAllListsToHash) == "function" then
    pcall(ChatFrame_ImportAllListsToHash)
  end
end

registerSlash()

--------------------------------------------------------------------------
-- Wire scheduler (single event subscriber + single heartbeat)
--------------------------------------------------------------------------

SCH.RegisterCollector(buildSchedulerSnapshot)

SCH.RegisterConsumer("SyncUnlock", consumeSync)
SCH.RegisterConsumer("JumpStop", consumeJump)
SCH.RegisterConsumer("Hint", consumeHint)
SCH.RegisterConsumer("Marker", consumeMarker)
SCH.RegisterConsumer("UiAck", consumeUiAck)

SCH.RegisterEvents({
  "PLAYER_LOGIN",
  "PLAYER_ENTERING_WORLD",
  "WORLD_MAP_UPDATE",
}, function(event)
  if event == "PLAYER_LOGIN" then
    if _G.GmtEntitlementGate and not GmtEntitlementGate.RequireAddon("GmMapTeleport") then return end
    registerSlash()
    local db = DB()
    local instHint = ""
    if type(GmInstanceInfo) == "function" then
      local id, total = GmInstanceInfo()
      if id and tonumber(id) and tonumber(id) > 0 then
        instHint = string.format(" · instance %d/%d", tonumber(id), math.max(tonumber(total) or 1, 1))
      end
    end
    chat(string.format("v%s — %s the world map for the teleport menu, middle-click to go (/maptp)%s",
      C.VERSION, MENU_GESTURE[db.menuButton] or "/maptp menu", instHint))
    -- Natives often register a moment after login (world Lua state). Retry quietly.
    if not hasNatives() then
      local f = CreateFrame("Frame")
      local left = 8
      f:SetScript("OnUpdate", function(self, dt)
        left = left - (dt or 0)
        if hasNatives() then
          chat("|cff2ecc71ExtProxy natives ready|r")
          self:SetScript("OnUpdate", nil)
        elseif left <= 0 then
          chat("|cffff8800ExtProxy natives not ready yet|r — walk once / wait for world; GMToolBox must have launched this client")
          self:SetScript("OnUpdate", nil)
        end
      end)
    end
    return
  end

  if event == "PLAYER_ENTERING_WORLD" and not hasNatives() then
    -- Force another quiet wait after loading screen.
    local f = CreateFrame("Frame")
    local left = 5
    f:SetScript("OnUpdate", function(self, dt)
      left = left - (dt or 0)
      if hasNatives() or left <= 0 then self:SetScript("OnUpdate", nil) end
    end)
  end

  hookMap()
  if event == "WORLD_MAP_UPDATE" then
    markUiDirty()
  end
end)
