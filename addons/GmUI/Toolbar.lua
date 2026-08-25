local UI = GmUI
UI.Toolbar = {}
local TB = UI.Toolbar

UI._tools = UI._tools or {}

local bar

local function addonLoaded(name)
  if type(name) ~= "string" or name == "" then return false end
  if type(IsAddOnLoaded) == "function" then
    local ok, loaded = pcall(IsAddOnLoaded, name)
    if ok and loaded then return true end
  end
  return false
end

--- True when tool deps are satisfied (any of deps[], or single addon, or no dep).
function UI.ToolDepsOk(t)
  if type(t) ~= "table" then return false end
  if type(t.deps) == "table" and #t.deps > 0 then
    for i = 1, #t.deps do
      if addonLoaded(t.deps[i]) then return true end
    end
    return false
  end
  if type(t.addon) == "string" and t.addon ~= "" then
    return addonLoaded(t.addon)
  end
  return true
end

function UI.RegisterTool(def)
  if type(def) ~= "table" or not def.id then return end
  UI._tools[def.id] = {
    id = def.id,
    label = def.label or def.id,
    color = def.color or def.id,
    tip = def.tip or def.label,
    toggle = def.toggle,
    order = tonumber(def.order) or 50,
    addon = def.addon,
    deps = def.deps,
  }
  if bar then TB.Rebuild() end
end

function UI.UnregisterTool(id)
  UI._tools[id] = nil
  if bar then TB.Rebuild() end
end

local function sortedTools()
  local list = {}
  for _, t in pairs(UI._tools) do
    local hid = UI.DB().tools[t.id] and UI.DB().tools[t.id].hidden
    if not hid and UI.ToolDepsOk(t) then
      list[#list + 1] = t
    end
  end
  table.sort(list, function(a, b)
    if a.order == b.order then return a.label < b.label end
    return a.order < b.order
  end)
  return list
end

function TB.SetActive(id, on)
  if not bar or not bar.chips then return end
  local chip = bar.chips[id]
  if chip and chip.SetActive then chip:SetActive(on) end
  -- attach/remove a shimmer sweep on the active chip so the open addon's tab
  -- visibly "shines", and pulse it once for attention.
  if chip then
    if on and UI.Gloss and UI.Gloss.Shimmer then
      local col = UI.ToolColor(chip._color or id)
      UI.Gloss.Shimmer(chip, { color = col })
      if UI.Gloss.Pulse then UI.Gloss.Pulse(chip, chip._color or id) end
    end
  end
end

function TB.Rebuild()
  if not bar or not bar._chipParent then return end
  if bar.chipList then
    for i = 1, #bar.chipList do bar.chipList[i]:Hide() end
  end
  bar.chipList = {}
  bar.chips = {}
  local parent = bar._chipParent
  local tools = sortedTools()
  local x = 4
  local barH = bar:GetHeight() or 28
  local chipH = math.max(18, barH - 6)   -- leave 3px padding top+bottom
  for i = 1, #tools do
    local t = tools[i]
    local w = math.max(60, #t.label * 7 + 18)
    local chip = UI.Button(parent, t.label, w, chipH, function()
      if type(t.toggle) == "function" then pcall(t.toggle) end
    end, t.tip, t.color)
    chip._color = t.color
    chip:ClearAllPoints()
    chip:SetPoint("LEFT", x, 0)
    bar.chips[t.id] = chip
    bar.chipList[#bar.chipList + 1] = chip
    x = x + w + 4
  end
  parent:SetWidth(math.max(500, x + 8))
  -- Clamp horizontal scroll after chip layout (narrow screens / many tools).
  local sc = bar._chipScroll
  if sc then
    local max = sc:GetHorizontalScrollRange() or 0
    local cur = sc:GetHorizontalScroll() or 0
    if cur > max then sc:SetHorizontalScroll(max) end
  end
end

function TB.Create()
  if bar then return bar end
  local cfg = UI.DB().toolbar

  bar = CreateFrame("Frame", "GmUIToolbar", UIParent)
  -- Overlay the top of UIParent. Never ClearAllPoints/SetPoint UIParent or
  -- WorldFrame: those are protected on 3.3.5 and taint-block the addon.
  bar:SetHeight(UI.T("toolbarHeight") or 28)
  bar:SetFrameStrata("HIGH")
  bar:SetFrameLevel(120)
  bar:SetClampedToScreen(false)
  bar:EnableMouse(true)
  bar:SetMovable(false)
  UI.ApplyBackdrop(bar, "toolbar")
  if bar._gmuiBg then
    bar._gmuiBg:SetTexture(0.04, 0.05, 0.07, 0.82)
  end
  -- glossy "wet panel" finish + the traveling spark that glides along the bar.
  UI.ApplyGloss(bar, "bar")
  if UI.Gloss and UI.Gloss.Spark then UI.Gloss.Spark(bar, { color = { 1.0, 0.85, 0.40 } }) end

  local barH = UI.T("toolbarHeight") or 28
  local btnH = math.max(18, barH - 6)

  local brand = CreateFrame("Frame", nil, bar)
  brand:SetWidth(72)
  brand:SetHeight(barH - 4)
  brand:SetPoint("LEFT", 4, 0)
  local art = brand:CreateTexture(nil, "ARTWORK")
  art:SetWidth(barH - 6)
  art:SetHeight(barH - 6)
  art:SetPoint("LEFT", 2, 0)
  pcall(function() art:SetTexture(UI.THEME_ART) end)
  if not art:GetTexture() then
    local r, g, b = UI.RGB("accent")
    art:SetTexture(r, g, b, 0.85)
  end
  local bfs = UI.Label(brand, "|cffe6007eGm|rUI", "GameFontNormalSmall", "accent")
  bfs:SetPoint("LEFT", art, "RIGHT", 4, 0)
  brand:EnableMouse(true)

  local scroll = CreateFrame("ScrollFrame", "GmUIToolbarScroll", bar)
  scroll:SetPoint("LEFT", 80, 0)
  scroll:SetPoint("RIGHT", -100, 0)
  scroll:SetHeight(barH - 4)
  local child = CreateFrame("Frame", nil, scroll)
  child:SetHeight(barH - 4)
  child:SetWidth(900)
  scroll:SetScrollChild(child)
  -- Horizontal wheel when chips overflow the bar (raw ScrollFrame — no UI.Scroll).
  scroll:EnableMouseWheel(true)
  scroll:SetScript("OnMouseWheel", function(self, delta)
    local max = self:GetHorizontalScrollRange() or 0
    if max <= 0 then return end
    local step = 48
    local next = (self:GetHorizontalScroll() or 0) - delta * step
    if next < 0 then next = 0 elseif next > max then next = max end
    self:SetHorizontalScroll(next)
  end)
  bar._chipParent = child
  bar._chipScroll = scroll

  local gear = UI.Button(bar, "⚙", btnH, btnH, function()
    if UI.ToggleSettings then UI.ToggleSettings() end
  end, "Settings — opacity, toolbar, tools")
  gear:SetPoint("RIGHT", -6, 0)

  local dock = UI.Button(bar, "Dock", 44, btnH, function()
    UI.ShowDock()
  end, "Tab dock panel", "dock")
  dock:SetPoint("RIGHT", gear, "LEFT", -5, 0)

  bar:ClearAllPoints()
  bar:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 0, 0)
  bar:SetPoint("TOPRIGHT", UIParent, "TOPRIGHT", 0, 0)

  if cfg.hidden then bar:Hide() else bar:Show() end
  TB.Rebuild()
  UI._toolbar = bar
  TB.ApplyLayoutInset()
  TB.HookLayout()
  return bar
end

local applyingInset = false
local layoutHooked = false

function TB.ReserveHeight()
  if not bar or not bar:IsShown() then return 0 end
  return bar:GetHeight() or UI.T("toolbarHeight") or 28
end

--- Pin the toolbar to the top of UIParent. Must never move UIParent/WorldFrame
--- (secure frames — taint logs "prevented the call of ... ClearAllPoints").
function TB.ApplyLayoutInset()
  if applyingInset or not bar then return end
  applyingInset = true
  local h = TB.ReserveHeight()
  if h <= 0 then h = UI.T("toolbarHeight") or 28 end
  bar:SetParent(UIParent)
  bar:ClearAllPoints()
  bar:SetPoint("TOPLEFT", UIParent, "TOPLEFT", 0, 0)
  bar:SetPoint("TOPRIGHT", UIParent, "TOPRIGHT", 0, 0)
  bar:SetHeight(h)
  bar:SetFrameStrata("HIGH")
  bar:SetFrameLevel(120)
  applyingInset = false
end

function TB.ClearLayoutInset()
  -- No-op. A prior build inset UIParent/WorldFrame; that is forbidden.
end

function TB.HookLayout()
  if layoutHooked then return end
  layoutHooked = true
  if type(hooksecurefunc) == "function" and type(UIParent_ManageFramePositions) == "function" then
    hooksecurefunc("UIParent_ManageFramePositions", function()
      if bar and bar:IsShown() then TB.ApplyLayoutInset() end
    end)
  end
  local ev = CreateFrame("Frame")
  ev:RegisterEvent("PLAYER_ENTERING_WORLD")
  ev:RegisterEvent("DISPLAY_SIZE_CHANGED")
  if ev.RegisterEvent then
    pcall(function() ev:RegisterEvent("UI_SCALE_CHANGED") end)
  end
  ev:SetScript("OnEvent", function()
    if bar and bar:IsShown() then
      TB.ApplyLayoutInset()
    end
  end)
  if UI.Scheduler and UI.Scheduler.After then
    UI.Scheduler.After(0.25, function()
      if bar and bar:IsShown() then TB.ApplyLayoutInset() end
    end)
    UI.Scheduler.After(1.25, function()
      if bar and bar:IsShown() then TB.ApplyLayoutInset() end
    end)
  end
end

function TB.Show()
  TB.Create():Show()
  UI.DB().toolbar.hidden = false
  TB.ApplyLayoutInset()
end

function TB.Hide()
  if bar then bar:Hide() end
  UI.DB().toolbar.hidden = true
  TB.ClearLayoutInset()
end

function TB.Toggle()
  if bar and bar:IsShown() then TB.Hide() else TB.Show() end
end
