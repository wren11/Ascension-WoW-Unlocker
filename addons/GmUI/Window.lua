local UI = GmUI

UI._windows = UI._windows or {}

local function savePos(win)
  local d = UI.DB()
  local c, _, _, x, y = win:GetPoint(1)
  d.windows[win._gmuiId] = d.windows[win._gmuiId] or {}
  local w = d.windows[win._gmuiId]
  w.point, w.x, w.y = c, x, y
  w.w, w.h = win:GetWidth(), win:GetHeight()
  w.docked = win._docked
  w.dockId = win._dockId
  w.alpha = win._userAlpha
end

local function restorePos(win)
  local w = UI.DB().windows[win._gmuiId]
  if not w or not w.point then return false end
  win:ClearAllPoints()
  win:SetPoint(w.point or "CENTER", UIParent, w.point or "CENTER", w.x or 0, w.y or 0)
  if w.w and w.h and win._resizable then
    win:SetWidth(w.w)
    win:SetHeight(w.h)
  end
  return true
end

local function snapFrame(win)
  local snap = UI.T("snapPx") or 28
  local scale = UIParent:GetEffectiveScale()
  local l, b, w, h = win:GetRect()
  if not l then return end
  local sw, sh = GetScreenWidth(), GetScreenHeight()
  local cx = l + w / 2
  local cy = b + h / 2
  local dx, dy = 0, 0

  local left, right = win:GetLeft(), win:GetRight()
  local top, bottom = win:GetTop(), win:GetBottom()
  if not left then return end

  local uiTop = (UIParent.GetTop and UIParent:GetTop()) or sh
  if left < snap then dx = -left end
  if right > sw - snap then dx = (sw - right) end
  if bottom < snap then dy = -bottom end
  if top > uiTop - snap then dy = (uiTop - top) end

  -- Keep windows out from under the overlay toolbar (bar is parented to UIParent).
  do
    local tb = UI._toolbar
    if tb and tb:IsShown() then
      local tbBottom = tb:GetBottom()
      -- WoW coords: top of screen = sh. Toolbar bottom in screen-from-bottom
      -- terms = tbBottom. Window top edge must be <= tbBottom to clear it.
      if top and tbBottom and top > tbBottom then
        dy = tbBottom - top
      end
    end
  end

  for id, other in pairs(UI._windows) do
    if other ~= win and other:IsShown() and not other._docked then
      local ol, orr = other:GetLeft(), other:GetRight()
      local ot, ob = other:GetTop(), other:GetBottom()
      if ol then
        if math.abs(left - orr) < snap then dx = orr - left end
        if math.abs(right - ol) < snap then dx = ol - right end
        if math.abs(top - ob) < snap then dy = ob - top end
        if math.abs(bottom - ot) < snap then dy = ot - bottom end
        if math.abs(left - ol) < snap then dx = ol - left end
        if math.abs(top - ot) < snap then dy = ot - top end
      end
    end
  end

  if dx ~= 0 or dy ~= 0 then
    local p, rel, rp, x, y = win:GetPoint(1)
    win:ClearAllPoints()
    win:SetPoint(p or "CENTER", rel or UIParent, rp or p or "CENTER", (x or 0) + dx, (y or 0) + dy)
  end
end

local function titleMenu(win)
  local menu = {
    { text = win._title or "Window", isTitle = true, notCheckable = true },
  }
  if not win._noDock and win._gmuiId ~= "dockhost" then
    menu[#menu + 1] = { text = "Dock into tab panel", notCheckable = true, func = function()
        if UI.DockWindow then UI.DockWindow(win) end
      end }
    menu[#menu + 1] = { text = "Undock", notCheckable = true, func = function()
        if UI.UndockWindow then UI.UndockWindow(win) end
      end }
  end
  menu[#menu + 1] = { text = "Snap now", notCheckable = true, func = function() snapFrame(win) end }
  menu[#menu + 1] = { text = "Reset position", notCheckable = true, func = function()
      win:ClearAllPoints()
      win:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
      savePos(win)
    end }
  menu[#menu + 1] = { text = "Opacity 20% (ghost)", notCheckable = true, func = function()
      win._userAlpha = 0.15
      if win._gmuiBg then
        local r, g, b = UI.RGB("bg")
        win._gmuiBg:SetTexture(r, g, b, 0.15)
      end
      savePos(win)
    end }
  menu[#menu + 1] = { text = "Opacity 80% transparent", notCheckable = true, func = function()
      win._userAlpha = UI.T("windowAlpha") or 0.22
      UI.ApplyBackdrop(win, "window")
      savePos(win)
    end }
  menu[#menu + 1] = { text = "Compact UI 50%", notCheckable = true, func = function()
      if UI.SetCompactScale then UI.SetCompactScale(0.50) end
    end }
  menu[#menu + 1] = { text = "Compact UI 60%", notCheckable = true, func = function()
      if UI.SetCompactScale then UI.SetCompactScale(0.60) end
    end }
  menu[#menu + 1] = { text = "Compact UI 75%", notCheckable = true, func = function()
      if UI.SetCompactScale then UI.SetCompactScale(0.75) end
    end }
  menu[#menu + 1] = { text = "Close", notCheckable = true, func = function() win:Hide() end }
  menu[#menu + 1] = { text = "Cancel", notCheckable = true, func = function() end }
  if not UI._dd then
    UI._dd = CreateFrame("Frame", "GmUIDropDown", UIParent, "UIDropDownMenuTemplate")
  end
  EasyMenu(menu, UI._dd, "cursor", 0, 0, "MENU")
end

--[[
  CreateWindow OnShow / OnHide CONTRACT (load-bearing for toolbar + geometry):
  This factory owns the window's OnShow/OnHide via SetScript:
    OnShow → UI.Toolbar.SetActive(id, true)
    OnHide → UI.Toolbar.SetActive(id, false) + savePos
  Consumers MUST NOT replace those handlers with a bare SetScript.
  Correct patterns (Lua 5.1 / WoW 3.3.5):
    1) Prefer HookScript when present (Scheduler / OnShownUpdate do this):
         f:HookScript("OnShow", function() ... end)
    2) Or chain explicitly:
         local prev = f:GetScript("OnShow")
         f:SetScript("OnShow", function(...)
           if prev then prev(...) end
           -- consumer work
         end)
  Replacing without chaining drops toolbar active highlight and position save.
  Soft-enforce (wrap SetScript to auto-chain) is intentionally NOT installed:
  Round 2 audit found all CreateWindow consumers either HookScript or
  GetScript+chain — wrapping would risk double-fire for HookScript users.
]]
function UI.CreateWindow(opts)
  opts = opts or {}
  local id = opts.id or ("win" .. tostring(math.random(1, 1e6)))
  if UI._windows[id] then return UI._windows[id] end

  local f = CreateFrame("Frame", "GmUIWin_" .. id, UIParent)
  f._gmuiId = id
  f._title = opts.title or id
  f._color = opts.color or "toolbox"
  f._resizable = opts.resizable
  f._noDock = opts.dockable == false
  f._userAlpha = UI.DB().windows[id] and UI.DB().windows[id].alpha or UI.T("windowAlpha")

  f:SetWidth(opts.width or 420)
  f:SetHeight(opts.height or 480)
  f:SetFrameStrata(opts.strata or "DIALOG")
  f:SetClampedToScreen(true)
  f:SetMovable(true)
  f:EnableMouse(true)
  f:SetToplevel(true)
  f:Hide()

  UI.ApplyBackdrop(f, "window")
  UI.ApplyThemeArt(f, 28)
  local col = UI.ToolColor(f._color)
  if f._gmuiEdge then f._gmuiEdge:SetTexture(col[1], col[2], col[3], 0.95) end
  -- glossy "wet panel" finish so every addon window shares the polished look.
  UI.ApplyGloss(f, "window")
  -- focused-window shimmer: the active/topmost window's title bar sweeps.
  f:SetScript("OnMouseDown", function()
    if UI.Gloss and UI.Gloss.Shimmer and f.titleBar then
      UI.Gloss.Shimmer(f.titleBar, { color = col, speed = 2.4 })
    end
  end)

  local bar = CreateFrame("Frame", nil, f)
  bar:SetPoint("TOPLEFT", 0, 0)
  bar:SetPoint("TOPRIGHT", 0, 0)
  bar:SetHeight(32)
  bar.bg = bar:CreateTexture(nil, "BACKGROUND")
  bar.bg:SetAllPoints()
  bar.bg:SetTexture(col[1] * 0.12, col[2] * 0.12, col[3] * 0.12, 0.88)
  bar:EnableMouse(true)
  bar:RegisterForDrag("LeftButton")
  bar:SetScript("OnDragStart", function()
    if f._docked then return end
    f:StartMoving()
  end)
  bar:SetScript("OnDragStop", function()
    f:StopMovingOrSizing()
    snapFrame(f)
    savePos(f)
  end)
  bar:SetScript("OnMouseUp", function(self, button)
    if button == "RightButton" then titleMenu(f) end
  end)
  f.titleBar = bar

  local titleFs = UI.Label(bar, f._title, "GameFontNormal", "text")
  titleFs:SetPoint("LEFT", 10, 0)
  f.titleFs = titleFs
  titleFs:SetText(UI.Hex(col[1], col[2], col[3]) .. f._title .. "|r")

  local close = UI.Button(bar, "×", 22, 20, function() f:Hide() end, "Close")
  close:SetPoint("RIGHT", -4, 0)

  if not f._noDock then
    local dockBtn = UI.Button(bar, "⧉", 22, 20, function()
      if f._docked then UI.UndockWindow(f) else UI.DockWindow(f) end
    end, "Dock / undock into tab panel")
    dockBtn:SetPoint("RIGHT", close, "LEFT", -4, 0)
  end

  local body = CreateFrame("Frame", nil, f)
  body:SetPoint("TOPLEFT", 10, -40)
  body:SetPoint("BOTTOMRIGHT", -10, 10)
  f.body = body
  f.content = body

  if opts.resizable then
    f:SetResizable(true)
    if f.SetMinResize then f:SetMinResize(420, 320) end
    local grip = CreateFrame("Button", nil, f)
    grip:SetWidth(16)
    grip:SetHeight(16)
    grip:SetPoint("BOTTOMRIGHT", -2, 2)
    grip:SetNormalTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Up")
    grip:SetHighlightTexture("Interface\\ChatFrame\\UI-ChatIM-SizeGrabber-Highlight")
    grip:SetScript("OnMouseDown", function() f:StartSizing("BOTTOMRIGHT") end)
    grip:SetScript("OnMouseUp", function()
      f:StopMovingOrSizing()
      savePos(f)
    end)
  end

  -- Owned scripts — see CreateWindow CONTRACT above. Chain/HookScript only.
  f:SetScript("OnShow", function()
    if UI.ScaleFrame then UI.ScaleFrame(f, f._gmuiScaleMul or 1) end
    if UI.Toolbar then UI.Toolbar.SetActive(id, true) end
  end)
  f:SetScript("OnHide", function()
    if UI.Toolbar then UI.Toolbar.SetActive(id, false) end
    savePos(f)
  end)

  f.SetTitle = function(self, t)
    self._title = t
    local c = UI.ToolColor(self._color)
    self.titleFs:SetText(UI.Hex(c[1], c[2], c[3]) .. t .. "|r")
  end

  f.Toggle = function(self)
    if self:IsShown() then self:Hide() else self:Show() end
  end

  if not restorePos(f) then
    f:SetPoint("CENTER", UIParent, "CENTER", opts.x or 0, opts.y or 0)
  end

  f._wantDock = UI.DB().windows[id] and UI.DB().windows[id].docked

  UI._windows[id] = f
  if UI.ScaleFrame then UI.ScaleFrame(f) end
  return f
end

function UI.Adopt(frame, opts)
  if not frame then return end
  opts = opts or {}
  local id = opts.id or frame:GetName() or "adopted"
  frame._gmuiId = id
  frame._title = opts.title or id
  frame._color = opts.color or "toolbox"
  frame._gmuiAdopted = true

  if frame.SetBackdrop then pcall(frame.SetBackdrop, frame, nil) end
  UI.ApplyBackdrop(frame, "window")
  local col = UI.ToolColor(frame._color)
  if frame._gmuiEdge then frame._gmuiEdge:SetTexture(col[1], col[2], col[3], 0.95) end

  local kids = { frame:GetChildren() }
  for i = 1, #kids do
    local c = kids[i]
    if c and c.GetObjectType and c:GetObjectType() == "Button" then
      local n = c.GetName and c:GetName()
      if (n and string.find(string.lower(n), "close"))
          or (c:GetWidth() == 32 and c:GetHeight() == 32) then
        local p, _, _, _, y = c:GetPoint(1)
        if p == "TOPRIGHT" or (n and string.find(string.lower(n), "close")) then
          c:Hide()
        end
      end
    end
  end
  local regs = { frame:GetRegions() }
  for i = 1, #regs do
    local r = regs[i]
    if r and r.GetObjectType and r:GetObjectType() == "FontString" then
      local fo = r.GetFontObject and r:GetFontObject()
      if fo == GameFontNormalLarge then
        r:Hide()
      end
    end
  end

  frame:SetMovable(true)
  frame:EnableMouse(true)
  frame:SetClampedToScreen(true)
  frame:RegisterForDrag("LeftButton")
  frame:SetScript("OnDragStart", function(self)
    if self._docked then return end
    self:StartMoving()
  end)
  frame:SetScript("OnDragStop", function(self)
    self:StopMovingOrSizing()
    snapFrame(self)
    savePos(self)
  end)

  if not frame._gmuiTitleBar then
    local bar = CreateFrame("Frame", nil, frame)
    bar:SetPoint("TOPLEFT", 0, 0)
    bar:SetPoint("TOPRIGHT", 0, 0)
    bar:SetHeight(28)
    bar:SetFrameLevel((frame:GetFrameLevel() or 1) + 5)
    bar.bg = bar:CreateTexture(nil, "BACKGROUND")
    bar.bg:SetAllPoints()
    bar.bg:SetTexture(col[1] * 0.14, col[2] * 0.14, col[3] * 0.14, 0.92)
    bar.accent = bar:CreateTexture(nil, "ARTWORK")
    bar.accent:SetPoint("BOTTOMLEFT", 0, 0)
    bar.accent:SetPoint("BOTTOMRIGHT", 0, 0)
    bar.accent:SetHeight(2)
    bar.accent:SetTexture(col[1], col[2], col[3], 0.95)
    frame._gmuiTitleBar = bar

    local titleFs = UI.Label(bar, frame._title, "GameFontNormal", "text")
    titleFs:SetPoint("LEFT", 10, 0)
    titleFs:SetText(UI.Hex(col[1], col[2], col[3]) .. frame._title .. "|r")
    frame.titleFs = titleFs

    local close = UI.Button(bar, "×", 22, 20, function() frame:Hide() end, "Close")
    close:SetPoint("RIGHT", -4, 0)
    local dockBtn = UI.Button(bar, "⧉", 22, 20, function()
      if frame._docked then UI.UndockWindow(frame) else UI.DockWindow(frame) end
    end, "Dock / undock")
    dockBtn:SetPoint("RIGHT", close, "LEFT", -4, 0)

    bar:EnableMouse(true)
    bar:RegisterForDrag("LeftButton")
    bar:SetScript("OnDragStart", function()
      if frame._docked then return end
      frame:StartMoving()
    end)
    bar:SetScript("OnDragStop", function()
      frame:StopMovingOrSizing()
      snapFrame(frame)
      savePos(frame)
    end)
    bar:SetScript("OnMouseUp", function(_, button)
      if button == "RightButton" then titleMenu(frame) end
    end)

    frame:SetHeight(frame:GetHeight() + 8)
  end

  UI._windows[id] = frame
  if not restorePos(frame) and opts.x then
    frame:ClearAllPoints()
    frame:SetPoint("CENTER", UIParent, "CENTER", opts.x or 0, opts.y or 0)
  end
  if UI.ScaleFrame then UI.ScaleFrame(frame) end
  return frame
end

function UI.GetWindow(id)
  return UI._windows[id]
end

function UI.ToggleWindow(id)
  local w = UI._windows[id]
  if not w then return end
  if w._docked and UI.FocusDockTab then
    UI.FocusDockTab(id)
    return
  end
  if w:IsShown() then w:Hide() else w:Show() end
end

function UI.ForEachWindow(fn)
  for id, w in pairs(UI._windows) do fn(id, w) end
end
