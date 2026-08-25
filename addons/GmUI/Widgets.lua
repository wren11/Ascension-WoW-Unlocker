local UI = GmUI
local uid = 0
local function nextName(prefix)
  uid = uid + 1
  return (prefix or "GmUI") .. uid
end

local function tip(widget, text)
  if not text then return widget end
  widget:SetScript("OnEnter", function(self)
    GameTooltip:SetOwner(self, "ANCHOR_RIGHT")
    GameTooltip:SetText(text, 1, 1, 1, 1, true)
    GameTooltip:Show()
  end)
  widget:SetScript("OnLeave", function() GameTooltip:Hide() end)
  return widget
end

local function fill(tex, r, g, b, a)
  tex:SetTexture(r, g, b, a)
end

function UI.Label(parent, text, font, colorKey)
  local fs = parent:CreateFontString(nil, "OVERLAY", font or "GameFontHighlightSmall")
  fs:SetJustifyH("LEFT")
  local r, g, b = UI.RGB(colorKey or "text")
  fs:SetTextColor(r, g, b)
  fs:SetText(text or "")
  return fs
end

function UI.Header(parent, text)
  local fs = UI.Label(parent, text, "GameFontNormal", "textGold")
  return fs
end

function UI.Muted(parent, text)
  return UI.Label(parent, text, "GameFontHighlightSmall", "textDim")
end

function UI.Button(parent, text, w, h, onClick, tipText, colorId)
  local b = CreateFrame("Button", nextName("GmUIBtn"), parent)
  b:SetWidth(w or 96)
  b:SetHeight(h or 26)
  b._col = colorId and UI.ToolColor(colorId) or { UI.RGB("accent") }
  local ir, ig, ib = UI.RGB("bgInput")
  local tr, tg, tb = UI.RGB("text")

  b.bg = b:CreateTexture(nil, "BACKGROUND")
  b.bg:SetAllPoints()
  fill(b.bg, ir, ig, ib, 0.94)

  b.accent = b:CreateTexture(nil, "ARTWORK")
  b.accent:SetPoint("TOPLEFT", 0, 0)
  b.accent:SetPoint("BOTTOMLEFT", 0, 0)
  b.accent:SetWidth(3)
  fill(b.accent, b._col[1], b._col[2], b._col[3], 1)

  b.hi = b:CreateTexture(nil, "BORDER")
  b.hi:SetPoint("TOPLEFT", 3, 0)
  b.hi:SetPoint("TOPRIGHT", 0, 0)
  b.hi:SetHeight(1)
  fill(b.hi, 1, 1, 1, 0.08)

  b.fs = b:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  b.fs:SetPoint("CENTER", 2, 0)
  b.fs:SetText(text or "")
  b.fs:SetTextColor(tr, tg, tb)

  b:SetScript("OnEnter", function(self)
    fill(self.bg, self._col[1] * 0.28, self._col[2] * 0.28, self._col[3] * 0.28, 0.96)
    fill(self.hi, self._col[1], self._col[2], self._col[3], 0.40)
    if tipText then
      GameTooltip:SetOwner(self, "ANCHOR_RIGHT")
      GameTooltip:SetText(tipText, 1, 1, 1, 1, true)
      GameTooltip:Show()
    end
  end)
  b:SetScript("OnLeave", function(self)
    local r, g, bcol = UI.RGB("bgInput")
    fill(self.bg, r, g, bcol, 0.94)
    fill(self.hi, 1, 1, 1, 0.08)
    GameTooltip:Hide()
  end)
  b:SetScript("OnMouseDown", function(self)
    fill(self.bg, self._col[1] * 0.38, self._col[2] * 0.38, self._col[3] * 0.38, 1)
  end)
  b:SetScript("OnMouseUp", function(self)
    fill(self.bg, self._col[1] * 0.28, self._col[2] * 0.28, self._col[3] * 0.28, 0.96)
  end)
  b:SetScript("OnClick", onClick)
  b.SetLabel = function(self, t) self.fs:SetText(t or "") end
  b.SetActive = function(self, on)
    if on then
      fill(self.bg, self._col[1] * 0.32, self._col[2] * 0.32, self._col[3] * 0.32, 0.98)
      fill(self.accent, self._col[1], self._col[2], self._col[3], 1)
      if GmUI and GmUI.Gloss and GmUI.Gloss.Apply then GmUI.Gloss.Apply(self, "panel") end
    else
      local r, g, bcol = UI.RGB("bgInput")
      fill(self.bg, r, g, bcol, 0.94)
    end
  end
  return b
end

function UI.Check(parent, text, get, set, tipText)
  local name = nextName("GmUICheck")
  local c = CreateFrame("CheckButton", name, parent, "UICheckButtonTemplate")
  c:SetWidth(26)
  c:SetHeight(26)
  local fs = _G[name .. "Text"]
  if fs then
    fs:SetText(text or "")
    fs:SetFontObject("GameFontHighlightSmall")
    fs:SetTextColor(0.90, 0.92, 0.95)
  end
  if get then c:SetChecked(get() and true or false) end
  c:SetScript("OnClick", function(self)
    if set then set(self:GetChecked() and true or false) end
  end)
  tip(c, tipText)
  c.Refresh = function(self)
    if get then self:SetChecked(get() and true or false) end
  end
  return c
end

function UI.Edit(parent, w, get, set, tipText)
  local e = CreateFrame("EditBox", nextName("GmUIEdit"), parent)
  e:SetWidth(w or 160)
  e:SetHeight(24)
  e:SetAutoFocus(false)
  e:SetFontObject(GameFontHighlight)
  e:SetTextInsets(8, 8, 0, 0)
  e:SetMaxLetters(255)

  e.bg = e:CreateTexture(nil, "BACKGROUND")
  e.bg:SetAllPoints()
  fill(e.bg, 0.08, 0.09, 0.11, 0.95)

  e.border = e:CreateTexture(nil, "BORDER")
  e.border:SetPoint("TOPLEFT", 0, 0)
  e.border:SetPoint("BOTTOMRIGHT", 0, 0)
  e.line = e:CreateTexture(nil, "ARTWORK")
  e.line:SetPoint("BOTTOMLEFT", 0, 0)
  e.line:SetPoint("BOTTOMRIGHT", 0, 0)
  e.line:SetHeight(2)
  local ar, ag, ab = UI.RGB("accent")
  fill(e.line, ar, ag, ab, 0.55)

  e:SetTextColor(0.95, 0.96, 0.98)
  if get then e:SetText(tostring(get() or "")) end

  e:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
  e:SetScript("OnEnterPressed", function(self) self:ClearFocus() end)
  e:SetScript("OnEditFocusGained", function(self)
    fill(self.line, ar, ag, ab, 1)
    fill(self.bg, 0.10, 0.12, 0.15, 0.98)
  end)
  e:SetScript("OnEditFocusLost", function(self)
    fill(self.line, ar, ag, ab, 0.55)
    fill(self.bg, 0.08, 0.09, 0.11, 0.95)
    if set then set(self:GetText() or "") end
  end)
  tip(e, tipText)
  e.Refresh = function(self)
    if get and not self:HasFocus() then self:SetText(tostring(get() or "")) end
  end
  return e
end

function UI.Multi(parent, w, h, get, set, tipText)
  local hold = CreateFrame("Frame", nextName("GmUIMulti"), parent)
  hold:SetWidth(w or 400)
  hold:SetHeight(h or 100)
  hold.bg = hold:CreateTexture(nil, "BACKGROUND")
  hold.bg:SetAllPoints()
  fill(hold.bg, 0.08, 0.09, 0.11, 0.95)
  hold.line = hold:CreateTexture(nil, "ARTWORK")
  hold.line:SetPoint("TOPLEFT", 0, 0)
  hold.line:SetPoint("TOPRIGHT", 0, 0)
  hold.line:SetHeight(2)
  local ar, ag, ab = UI.RGB("accent")
  fill(hold.line, ar, ag, ab, 0.45)

  local scroll = CreateFrame("ScrollFrame", nextName("GmUIMultiScroll"), hold, "UIPanelScrollFrameTemplate")
  scroll:SetPoint("TOPLEFT", 6, -6)
  scroll:SetPoint("BOTTOMRIGHT", -26, 6)

  local box = CreateFrame("EditBox", nextName("GmUIMultiBox"), scroll)
  box:SetMultiLine(true)
  box:SetAutoFocus(false)
  box:SetFontObject(GameFontHighlightSmall)
  box:SetWidth((w or 400) - 36)
  box:SetHeight(h or 100)
  box:SetTextInsets(4, 4, 4, 4)
  box:SetTextColor(0.92, 0.94, 0.96)
  if get then box:SetText(get() or "") end
  box:SetScript("OnEscapePressed", function(self) self:ClearFocus() end)
  box:SetScript("OnTextChanged", function(self, user)
    if user and set then set(self:GetText() or "") end
  end)
  box:SetScript("OnEditFocusLost", function(self)
    if set then set(self:GetText() or "") end
  end)
  scroll:SetScrollChild(box)
  hold.box = box
  hold.scroll = scroll
  tip(hold, tipText)
  return hold
end

function UI.Slider(parent, w, minV, maxV, step, get, set, tipText)
  local wrap = CreateFrame("Frame", nil, parent)
  wrap:SetWidth((w or 180) + 48)
  wrap:SetHeight(28)

  local s = CreateFrame("Slider", nextName("GmUISlider"), wrap)
  s:SetWidth(w or 180)
  s:SetHeight(14)
  s:SetPoint("LEFT", 0, 0)
  s:SetOrientation("HORIZONTAL")
  s:SetMinMaxValues(minV or 0, maxV or 1)
  s:SetValueStep(step or 0.01)
  s.bg = s:CreateTexture(nil, "BACKGROUND")
  s.bg:SetAllPoints()
  fill(s.bg, 0.08, 0.09, 0.11, 0.9)
  s:SetThumbTexture("Interface\\Buttons\\UI-SliderBar-Button-Horizontal")
  local val = get and tonumber(get()) or minV or 0
  s:SetValue(val)

  local lbl = UI.Label(wrap, string.format("%.2f", val), "GameFontHighlightSmall", "accent")
  lbl:SetPoint("LEFT", s, "RIGHT", 8, 0)
  s:SetScript("OnValueChanged", function(self, v)
    if step and step >= 1 then v = math.floor(v + 0.5) end
    lbl:SetText(string.format(step and step >= 1 and "%.0f" or "%.2f", v))
    if set then set(v) end
  end)
  tip(s, tipText)
  wrap.slider = s
  wrap.label = lbl
  wrap.Refresh = function()
    if get then s:SetValue(tonumber(get()) or minV or 0) end
  end
  return wrap
end

function UI.Scroll(parent, w, h, name)
  -- Returns a hold Frame (layout root). Content MUST parent to hold.child.
  -- hold proxies ScrollFrame APIs so callers that treat the return as a
  -- ScrollFrame (SetScrollChild / GetVerticalScrollRange / …) stay correct.
  -- Optional DEV warn: if CreateFrame(..., hold) instead of hold.child, hold
  -- gains children beyond {scroll, track} — chat warn once per hold.
  local hold = CreateFrame("Frame", nil, parent)
  hold:SetWidth(w or 200)
  hold:SetHeight(h or 200)

  local scroll = CreateFrame("ScrollFrame", name or nextName("GmUIScroll"), hold)
  scroll:SetPoint("TOPLEFT", 0, 0)
  scroll:SetPoint("BOTTOMRIGHT", -16, 0)

  local child = CreateFrame("Frame", nil, scroll)
  child:SetWidth(math.max(40, (w or 200) - 18))
  child:SetHeight(h or 200)
  scroll:SetScrollChild(child)

  -- right track
  local track = CreateFrame("Frame", nil, hold)
  track:SetWidth(14)
  track:SetPoint("TOPRIGHT", 0, 0)
  track:SetPoint("BOTTOMRIGHT", 0, 0)
  track.bg = track:CreateTexture(nil, "BACKGROUND")
  track.bg:SetAllPoints()
  fill(track.bg, 0.05, 0.06, 0.08, 0.92)
  track.edge = track:CreateTexture(nil, "BORDER")
  track.edge:SetPoint("TOPLEFT", 0, 0)
  track.edge:SetPoint("BOTTOMLEFT", 0, 0)
  track.edge:SetWidth(1)
  fill(track.edge, 1, 1, 1, 0.08)

  local thumb = CreateFrame("Button", nil, track)
  thumb:SetWidth(12)
  thumb:SetHeight(40)
  thumb:SetPoint("TOP", 0, -2)
  thumb.bg = thumb:CreateTexture(nil, "ARTWORK")
  thumb.bg:SetAllPoints()
  local ar, ag, ab = UI.RGB("accent")
  fill(thumb.bg, ar, ag, ab, 0.55)
  thumb:EnableMouse(true)
  thumb:RegisterForDrag("LeftButton")

  local function warnMisparented()
    if hold._gmuiMisparentWarned then return end
    local kids = { hold:GetChildren() }
    for i = 1, #kids do
      local c = kids[i]
      if c and c ~= scroll and c ~= track then
        hold._gmuiMisparentWarned = true
        if DEFAULT_CHAT_FRAME then
          DEFAULT_CHAT_FRAME:AddMessage(
            "|cfffaa61a[GmUI]|r UI.Scroll: widget parented to hold — use hold.child")
        end
        return
      end
    end
  end

  local function syncThumb()
    warnMisparented()
    local max = scroll:GetVerticalScrollRange() or 0
    local th = track:GetHeight() or 1
    if max <= 0 then
      thumb:Hide()
      return
    end
    thumb:Show()
    local view = scroll:GetHeight() or 1
    local ratio = view / (view + max)
    if ratio < 0.12 then ratio = 0.12 end
    if ratio > 1 then ratio = 1 end
    local thumbH = math.max(24, th * ratio)
    thumb:SetHeight(thumbH)
    local cur = scroll:GetVerticalScroll() or 0
    local travel = th - thumbH - 4
    local y = -2 - (max > 0 and (cur / max) * travel or 0)
    thumb:ClearAllPoints()
    thumb:SetPoint("TOP", track, "TOP", 0, y)
  end

  thumb:SetScript("OnDragStart", function(self)
    self:SetScript("OnUpdate", function()
      local max = scroll:GetVerticalScrollRange() or 0
      if max <= 0 then return end
      local _, cy = GetCursorPosition()
      local scale = track:GetEffectiveScale()
      local top = track:GetTop() * scale
      local th = track:GetHeight() * scale
      local thumbH = self:GetHeight() * scale
      local rel = (top - cy) / math.max(1, th - thumbH)
      if rel < 0 then rel = 0 elseif rel > 1 then rel = 1 end
      scroll:SetVerticalScroll(rel * max)
      syncThumb()
    end)
  end)
  thumb:SetScript("OnDragStop", function(self)
    self:SetScript("OnUpdate", nil)
  end)

  local function onWheel(_, delta)
    local cur = scroll:GetVerticalScroll() or 0
    local max = scroll:GetVerticalScrollRange() or 0
    local step = 36
    local next = cur - delta * step
    if next < 0 then next = 0 elseif next > max then next = max end
    scroll:SetVerticalScroll(next)
    syncThumb()
  end
  scroll:EnableMouseWheel(true)
  scroll:SetScript("OnMouseWheel", onWheel)
  -- Wheel over the track/thumb (outside the ScrollFrame) must also scroll.
  hold:EnableMouseWheel(true)
  hold:SetScript("OnMouseWheel", onWheel)
  track:EnableMouseWheel(true)
  track:SetScript("OnMouseWheel", onWheel)
  scroll:SetScript("OnScrollRangeChanged", syncThumb)
  scroll:SetScript("OnVerticalScroll", syncThumb)
  hold:SetScript("OnSizeChanged", function()
    warnMisparented()
    local c = hold.child
    if c then c:SetWidth(math.max(40, hold:GetWidth() - 18)) end
    syncThumb()
  end)

  hold.scroll = scroll
  hold.child = child
  hold.track = track
  hold.thumb = thumb
  hold._gmuiScroll = true

  -- Proxy ScrollFrame methods onto hold (callers often treat UI.Scroll() as a ScrollFrame).
  hold.SetVerticalScroll = function(_, v) scroll:SetVerticalScroll(v or 0); syncThumb() end
  hold.GetVerticalScroll = function() return scroll:GetVerticalScroll() end
  hold.GetVerticalScrollRange = function() return scroll:GetVerticalScrollRange() end
  hold.SetHorizontalScroll = function(_, v) scroll:SetHorizontalScroll(v or 0) end
  hold.GetHorizontalScroll = function() return scroll:GetHorizontalScroll() end
  hold.GetHorizontalScrollRange = function() return scroll:GetHorizontalScrollRange() end
  hold.SetScrollChild = function(_, newChild)
    if not newChild then return end
    scroll:SetScrollChild(newChild)
    hold.child = newChild
    syncThumb()
  end
  hold.GetScrollChild = function() return scroll:GetScrollChild() or hold.child end
  hold.UpdateScrollChildRect = function()
    if scroll.UpdateScrollChildRect then scroll:UpdateScrollChildRect() end
    syncThumb()
  end
  hold.SyncThumb = syncThumb
  -- Grow content height and refresh range (parents widgets to hold.child).
  hold.SetChildHeight = function(_, hgt)
    local c = hold.child
    if not c then return end
    c:SetHeight(math.max(1, tonumber(hgt) or 1))
    syncThumb()
  end

  return hold
end

local function normDropdownItems(items)
  local out = {}
  for i = 1, #(items or {}) do
    local it = items[i]
    if type(it) == "table" then
      out[#out + 1] = { id = it.id, label = it.label or tostring(it.id) }
    else
      out[#out + 1] = { id = it, label = tostring(it) }
    end
  end
  return out
end

local function dropdownLabel(norm, id)
  for i = 1, #norm do
    if norm[i].id == id then return norm[i].label end
  end
  return tostring(id or "")
end

function UI.Dropdown(parent, items, get, set, tipText)
  local norm = normDropdownItems(items)
  local br, bg, bb = UI.RGB("bgInput")
  local tr, tg, tb = UI.RGB("text")
  local ar, ag, ab = UI.RGB("accent")

  local btn = CreateFrame("Button", nextName("GmUIDrop"), parent)
  btn:SetHeight(24)
  btn:SetWidth(160)

  btn.bg = btn:CreateTexture(nil, "BACKGROUND")
  btn.bg:SetAllPoints()
  fill(btn.bg, br, bg, bb, 0.95)

  btn.line = btn:CreateTexture(nil, "ARTWORK")
  btn.line:SetPoint("BOTTOMLEFT", 0, 0)
  btn.line:SetPoint("BOTTOMRIGHT", 0, 0)
  btn.line:SetHeight(2)
  fill(btn.line, ar, ag, ab, 0.45)

  btn.fs = btn:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  btn.fs:SetPoint("LEFT", 8, 0)
  btn.fs:SetPoint("RIGHT", -18, 0)
  btn.fs:SetJustifyH("LEFT")
  btn.fs:SetTextColor(tr, tg, tb)

  btn.arrow = btn:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  btn.arrow:SetPoint("RIGHT", -6, 0)
  btn.arrow:SetText("v")
  btn.arrow:SetTextColor(ar, ag, ab, 0.85)

  local menu
  local menuRows = {}

  local function closeMenu()
    if menu then menu:Hide() end
  end

  local function syncLabel()
    local id = get and get() or (norm[1] and norm[1].id)
    btn.fs:SetText(dropdownLabel(norm, id))
  end

  local function rebuildMenu()
    if not menu then
      menu = CreateFrame("Frame", nextName("GmUIDropMenu"), UIParent)
      menu:SetFrameStrata("DIALOG")
      menu:SetFrameLevel(100)
      menu:EnableMouse(true)
      menu.bg = menu:CreateTexture(nil, "BACKGROUND")
      menu.bg:SetAllPoints()
      fill(menu.bg, br, bg, bb, 0.98)
      menu.edge = menu:CreateTexture(nil, "BORDER")
      menu.edge:SetPoint("TOPLEFT", 0, 0)
      menu.edge:SetPoint("BOTTOMRIGHT", 0, 0)
      fill(menu.edge, ar, ag, ab, 0.35)
    end

    for i = 1, #menuRows do
      local row = menuRows[i]
      if row then
        row:Hide()
        row:SetParent(menu)
      end
    end
    menuRows = {}

    local rowH = 22
    local mh = math.max(1, #norm) * rowH + 4
    menu:SetWidth(btn:GetWidth())
    menu:SetHeight(mh)

    for i = 1, #norm do
      local it = norm[i]
      local row = CreateFrame("Button", nil, menu)
      row:SetHeight(rowH)
      row:SetPoint("TOPLEFT", 2, -2 - (i - 1) * rowH)
      row:SetPoint("TOPRIGHT", -2, -2 - (i - 1) * rowH)
      row.bg = row:CreateTexture(nil, "BACKGROUND")
      row.bg:SetAllPoints()
      fill(row.bg, 0.10, 0.12, 0.15, 0.0)
      row.fs = row:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
      row.fs:SetPoint("LEFT", 8, 0)
      row.fs:SetText(it.label)
      row.fs:SetTextColor(tr, tg, tb)
      row:SetScript("OnEnter", function(self)
        fill(self.bg, ar * 0.22, ag * 0.22, ab * 0.22, 0.95)
        self.fs:SetTextColor(0.96, 0.97, 0.99)
      end)
      row:SetScript("OnLeave", function(self)
        fill(self.bg, 0.10, 0.12, 0.15, 0.0)
        self.fs:SetTextColor(tr, tg, tb)
      end)
      row:SetScript("OnClick", function()
        if set then set(it.id) end
        syncLabel()
        closeMenu()
      end)
      menuRows[#menuRows + 1] = row
    end
  end

  local function openMenu()
    rebuildMenu()
    menu:ClearAllPoints()
    menu:SetPoint("TOPLEFT", btn, "BOTTOMLEFT", 0, -2)
    menu:Show()
    menu:SetScript("OnHide", function() end)
  end

  btn:SetScript("OnClick", function()
    if menu and menu:IsShown() then closeMenu() else openMenu() end
  end)
  btn:SetScript("OnEnter", function(self)
    fill(self.bg, br * 1.08, bg * 1.08, bb * 1.08, 0.98)
    fill(self.line, ar, ag, ab, 0.75)
  end)
  btn:SetScript("OnLeave", function(self)
    fill(self.bg, br, bg, bb, 0.95)
    fill(self.line, ar, ag, ab, 0.45)
  end)

  btn._closeMenu = closeMenu
  syncLabel()
  tip(btn, tipText)

  btn.Refresh = function(self)
    syncLabel()
  end
  btn.SetItems = function(self, newItems)
    norm = normDropdownItems(newItems)
    closeMenu()
    syncLabel()
  end
  btn.CloseMenu = closeMenu

  return btn
end

local tableDump
local function tableDumpParent()
  if not tableDump then
    tableDump = CreateFrame("Frame")
    tableDump:Hide()
  end
  return tableDump
end

function UI.Table(parent, columns, rows, opts)
  opts = opts or {}
  rows = rows or {}
  local rowH = opts.rowHeight or 22
  local headerH = 24
  local totalW = 0
  for i = 1, #(columns or {}) do
    totalW = totalW + (columns[i].width or 80)
  end
  local w = opts.width or totalW
  local h = opts.height or 200

  local wrap = CreateFrame("Frame", nil, parent)
  wrap:SetWidth(w)
  wrap:SetHeight(h)
  wrap._columns = columns
  wrap._rows = rows
  wrap._opts = opts
  wrap._rowFrames = {}

  local hdr = CreateFrame("Frame", nil, wrap)
  hdr:SetPoint("TOPLEFT", 0, 0)
  hdr:SetPoint("TOPRIGHT", 0, 0)
  hdr:SetHeight(headerH)
  hdr.bg = hdr:CreateTexture(nil, "BACKGROUND")
  hdr.bg:SetAllPoints()
  local ar, ag, ab = UI.RGB("accent")
  fill(hdr.bg, ar * 0.14, ag * 0.14, ab * 0.14, 0.92)
  hdr.line = hdr:CreateTexture(nil, "ARTWORK")
  hdr.line:SetPoint("BOTTOMLEFT", 0, 0)
  hdr.line:SetPoint("BOTTOMRIGHT", 0, 0)
  hdr.line:SetHeight(1)
  fill(hdr.line, ar, ag, ab, 0.45)

  local x = 6
  for i = 1, #(columns or {}) do
    local col = columns[i]
    local fs = UI.Label(hdr, col.label or col.id, "GameFontHighlightSmall", "textDim")
    fs:SetPoint("TOPLEFT", x, -5)
    fs:SetWidth(col.width or 80)
    x = x + (col.width or 80)
  end

  local scroll = UI.Scroll(wrap, w, h - headerH - 2)
  scroll:SetPoint("TOPLEFT", 0, -headerH - 2)
  wrap.scroll = scroll

  local function clearRows()
    local dump = tableDumpParent()
    for i = 1, #wrap._rowFrames do
      local row = wrap._rowFrames[i]
      if row then
        row:Hide()
        row:ClearAllPoints()
        row:SetParent(dump)
      end
    end
    wrap._rowFrames = {}
  end

  local function paintRows()
    clearRows()
    local child = scroll.child
    if not child then return end
    local cols = wrap._columns or {}
    local data = wrap._rows or {}
    local rar, rag, rab = UI.RGB("rowAlt")
    local contentH = math.max(1, #data * rowH)

    for ri = 1, #data do
      local rowData = data[ri]
      local row = CreateFrame("Button", nil, child)
      row:SetHeight(rowH)
      row:SetPoint("TOPLEFT", 0, -(ri - 1) * rowH)
      row:SetPoint("TOPRIGHT", 0, -(ri - 1) * rowH)
      row.bg = row:CreateTexture(nil, "BACKGROUND")
      row.bg:SetAllPoints()
      if ri % 2 == 0 then
        fill(row.bg, rar * 0.04, rag * 0.04, rab * 0.04, 0.40)
      else
        local rar, rag, rab = UI.RGB("bg")
        fill(row.bg, rar, rag, rab, 0.35)
      end

      local cx = 6
      for ci = 1, #cols do
        local col = cols[ci]
        local val = rowData[col.id]
        if val == nil then val = "" end
        local fs = UI.Label(row, tostring(val), "GameFontHighlightSmall", "text")
        fs:SetPoint("TOPLEFT", cx, -4)
        fs:SetWidth((col.width or 80) - 4)
        cx = cx + (col.width or 80)
      end

      row:SetScript("OnEnter", function(self)
        fill(self.bg, ar * 0.18, ag * 0.18, ab * 0.18, 0.55)
      end)
      row:SetScript("OnLeave", function(self)
        if ri % 2 == 0 then
          fill(self.bg, rar * 0.04, rag * 0.04, rab * 0.04, 0.40)
        else
          local rar, rag, rab = UI.RGB("bg")
          fill(self.bg, rar, rag, rab, 0.35)
        end
      end)
      row:SetScript("OnClick", function()
        wrap._selected = rowData
        wrap._selectedIndex = ri
        if opts.onSelect then opts.onSelect(rowData, ri) end
      end)
      wrap._rowFrames[#wrap._rowFrames + 1] = row
    end

    scroll:SetChildHeight(contentH)
    scroll:SyncThumb()
  end

  wrap.Rebuild = paintRows
  wrap.SetRows = function(self, newRows)
    self._rows = newRows or {}
    paintRows()
  end
  wrap.SetColumns = function(self, newCols)
    self._columns = newCols or {}
    paintRows()
  end
  wrap.GetSelected = function() return wrap._selected end

  paintRows()
  return wrap
end

function UI.StatusBar(parent, getFrac, colorKey)
  local bar = CreateFrame("Frame", nil, parent)
  bar:SetHeight(6)
  bar:SetWidth(120)

  bar.bg = bar:CreateTexture(nil, "BACKGROUND")
  bar.bg:SetAllPoints()
  fill(bar.bg, 0.08, 0.09, 0.11, 0.92)

  bar.fill = bar:CreateTexture(nil, "ARTWORK")
  bar.fill:SetPoint("TOPLEFT", 0, 0)
  bar.fill:SetPoint("BOTTOMLEFT", 0, 0)
  local cr, cg, cb = UI.RGB(colorKey or "accent")
  fill(bar.fill, cr, cg, cb, 0.88)

  bar.Refresh = function(self)
    local f = getFrac and tonumber(getFrac()) or 0
    if f < 0 then f = 0 elseif f > 1 then f = 1 end
    local bw = self:GetWidth() or 1
    self.fill:SetWidth(math.max(0, bw * f))
  end
  bar:Refresh()
  return bar
end

function UI.Chip(parent, text, colorId)
  local col
  if colorId and UI.TOOL_COLORS[colorId] then
    col = UI.ToolColor(colorId)
  else
    local r, g, b = UI.RGB(colorId or "accent")
    col = { r, g, b }
  end

  local c = CreateFrame("Frame", nil, parent)
  c:SetHeight(18)
  c.bg = c:CreateTexture(nil, "BACKGROUND")
  c.bg:SetAllPoints()
  fill(c.bg, col[1] * 0.22, col[2] * 0.22, col[3] * 0.22, 0.95)
  c.edge = c:CreateTexture(nil, "ARTWORK")
  c.edge:SetPoint("TOPLEFT", 0, 0)
  c.edge:SetPoint("BOTTOMLEFT", 0, 0)
  c.edge:SetWidth(2)
  fill(c.edge, col[1], col[2], col[3], 1)

  c.fs = c:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
  c.fs:SetPoint("LEFT", 6, 0)
  c.fs:SetPoint("RIGHT", -6, 0)
  c.fs:SetTextColor(0.93, 0.95, 0.97)
  c.fs:SetText(text or "")

  local tw = (c.fs:GetStringWidth() or 0) + 14
  c:SetWidth(math.max(32, tw))

  c.SetText = function(self, t)
    self.fs:SetText(t or "")
    local nw = (self.fs:GetStringWidth() or 0) + 14
    self:SetWidth(math.max(32, nw))
  end
  c.SetColor = function(self, newColorId)
    local nc
    if newColorId and UI.TOOL_COLORS[newColorId] then
      nc = UI.ToolColor(newColorId)
    else
      local r, g, b = UI.RGB(newColorId or "accent")
      nc = { r, g, b }
    end
    fill(self.bg, nc[1] * 0.22, nc[2] * 0.22, nc[3] * 0.22, 0.95)
    fill(self.edge, nc[1], nc[2], nc[3], 1)
  end
  return c
end

function UI.Panel(parent)
  local p = CreateFrame("Frame", nil, parent)
  p.bg = p:CreateTexture(nil, "BACKGROUND")
  p.bg:SetAllPoints()
  local r, g, b = UI.RGB("bgPanel")
  fill(p.bg, r, g, b, UI.T("panelAlpha") or 0.72)
  return p
end

function UI.Divider(parent, w)
  local t = parent:CreateTexture(nil, "ARTWORK")
  t:SetWidth(w or 200)
  t:SetHeight(1)
  fill(t, 1, 1, 1, 0.08)
  return t
end

function UI.Row(parent, y, height)
  return y - (height or 28)
end

UI.Form = {}

function UI.Form.Begin(parent, width)
  return {
    parent = parent,
    width = width or 520,
    y = -4,
    bits = {},
  }
end

local function track(ctx, w)
  ctx.bits[#ctx.bits + 1] = w
  return w
end

local formDump
local function formDumpParent()
  if not formDump then
    formDump = CreateFrame("Frame")
    formDump:Hide()
  end
  return formDump
end

function UI.Form.Clear(ctx)
  local dump = formDumpParent()
  for i = 1, #(ctx.bits or {}) do
    local w = ctx.bits[i]
    if w then
      if w.Hide then w:Hide() end
      if w.ClearAllPoints then w:ClearAllPoints() end
      if w.SetParent then w:SetParent(dump) end
    end
  end
  ctx.bits = {}
  ctx.y = -4
end

function UI.Form.Section(ctx, title)
  local fs = track(ctx, UI.Header(ctx.parent, title))
  fs:SetPoint("TOPLEFT", 0, ctx.y)
  ctx.y = ctx.y - 22
  local d = track(ctx, UI.Divider(ctx.parent, ctx.width))
  d:SetPoint("TOPLEFT", 0, ctx.y)
  ctx.y = ctx.y - 10
end

function UI.Form.Note(ctx, text)
  local fs = track(ctx, UI.Muted(ctx.parent, text))
  fs:SetPoint("TOPLEFT", 0, ctx.y)
  fs:SetWidth(ctx.width)
  fs:SetWordWrap(true)
  ctx.y = ctx.y - 18
end

function UI.Form.Gap(ctx, h)
  ctx.y = ctx.y - (h or 8)
end

function UI.Form.Field(ctx, label, width, get, set, tipText)
  local fs = track(ctx, UI.Label(ctx.parent, label, "GameFontHighlightSmall", "textDim"))
  fs:SetPoint("TOPLEFT", 0, ctx.y)
  ctx.y = ctx.y - 16
  local e = track(ctx, UI.Edit(ctx.parent, width or ctx.width, get, set, tipText))
  e:SetPoint("TOPLEFT", 0, ctx.y)
  ctx.y = ctx.y - 30
  return e
end

function UI.Form.FieldRow(ctx, fields)
  local x = 0
  local rowTop = ctx.y
  local maxH = 0
  for i = 1, #fields do
    local f = fields[i]
    local fs = track(ctx, UI.Label(ctx.parent, f.label, "GameFontHighlightSmall", "textDim"))
    fs:SetPoint("TOPLEFT", x, rowTop)
    local e = track(ctx, UI.Edit(ctx.parent, f.w or 100, f.get, f.set, f.tip))
    e:SetPoint("TOPLEFT", x, rowTop - 16)
    x = x + (f.w or 100) + 12
    maxH = 46
  end
  ctx.y = rowTop - maxH
end

function UI.Form.Check(ctx, text, get, set, tipText)
  local c = track(ctx, UI.Check(ctx.parent, text, get, set, tipText))
  c:SetPoint("TOPLEFT", -4, ctx.y)
  ctx.y = ctx.y - 28
  return c
end

function UI.Form.TextArea(ctx, label, h, get, set, tipText)
  if label and label ~= "" then
    local fs = track(ctx, UI.Label(ctx.parent, label, "GameFontHighlightSmall", "textDim"))
    fs:SetPoint("TOPLEFT", 0, ctx.y)
    ctx.y = ctx.y - 16
  end
  local m = track(ctx, UI.Multi(ctx.parent, ctx.width, h or 90, get, set, tipText))
  m:SetPoint("TOPLEFT", 0, ctx.y)
  ctx.y = ctx.y - (h or 90) - 10
  return m
end

function UI.Form.ButtonRow(ctx, buttons)
  local x = 0
  for i = 1, #buttons do
    local b = buttons[i]
    local btn = track(ctx, UI.Button(ctx.parent, b.text, b.w or 88, 26, b.onClick, b.tip, b.color))
    btn:SetPoint("TOPLEFT", x, ctx.y)
    x = x + (b.w or 88) + 6
  end
  ctx.y = ctx.y - 34
end

function UI.Form.Height(ctx)
  return math.max(120, -ctx.y + 20)
end

function UI.TabBar(parent, tabs, onSelect)
  local bar = CreateFrame("Frame", nil, parent)
  bar:SetHeight(28)
  bar.buttons = {}
  bar.selected = tabs[1] and tabs[1].id
  local x = 0

  local function applyTabActive(btn, on, tabColorId)
    local col
    if on then
      if tabColorId then
        col = UI.ToolColor(tabColorId)
      else
        local r, g, b = UI.RGB("tabActive")
        col = { r, g, b }
      end
      fill(btn.bg, col[1] * 0.34, col[2] * 0.34, col[3] * 0.34, 0.98)
      fill(btn.accent, col[1], col[2], col[3], 1)
      fill(btn.hi, col[1], col[2], col[3], 0.48)
      btn.fs:SetTextColor(0.97, 0.98, 1.00)
      if GmUI and GmUI.Gloss and GmUI.Gloss.Apply then GmUI.Gloss.Apply(btn, "panel") end
    else
      local ir, ig, ib = UI.RGB("tabIdle")
      local ir, ig, ib = UI.RGB("tabIdle")
      fill(btn.bg, ir, ig, ib, 0.94)
      fill(btn.accent, ir, ig, ib, 0.40)
      fill(btn.hi, 1, 1, 1, 0.06)
      btn.fs:SetTextColor(0.70, 0.74, 0.78)
    end
  end

  for i = 1, #tabs do
    local t = tabs[i]
    local b = UI.Button(bar, t.label, t.w or 78, 26, function()
      if bar.selected == t.id then
        if onSelect then onSelect(t.id) end
        return
      end
      bar.selected = t.id
      for j = 1, #bar.buttons do
        applyTabActive(bar.buttons[j], bar.buttons[j]._tabId == t.id, bar.buttons[j]._tabColorId)
      end
      if GmUI and GmUI.Gloss and GmUI.Gloss.Shimmer then
        GmUI.Gloss.Shimmer(b, { color = UI.ToolColor(t.color or "accent"), speed = 2.0 })
      end
      if onSelect then onSelect(t.id) end
    end, t.tip, t.color)
    b._tabId = t.id
    b._tabColorId = t.color
    b.SetActive = function(self, on)
      applyTabActive(self, on and true or false, self._tabColorId)
    end
    b:SetPoint("LEFT", x, 0)
    b:SetActive(i == 1)
    bar.buttons[#bar.buttons + 1] = b
    x = x + (t.w or 78) + 4
  end
  bar:SetWidth(x)
  -- Visual-only sync. Never invoke onSelect — callers that also live in onSelect
  -- (e.g. GatherBot ShowTab → Select → ShowTab) would stack-overflow otherwise.
  bar.Select = function(self, id)
    self.selected = id
    for j = 1, #self.buttons do
      applyTabActive(self.buttons[j], self.buttons[j]._tabId == id, self.buttons[j]._tabColorId)
    end
  end
  return bar
end

--- Multi-page tab host used by GmExplore (and similar).
--- Usage: local tabs = UI.Tabs(parent, {{id="run",label="Run"},{id="opts",label="Options"}})
---        local body = tabs:GetBody("run")
function UI.Tabs(parent, tabs)
  local host = CreateFrame("Frame", nil, parent)
  local bodies = {}
  local bar

  local function showBody(id)
    for k, body in pairs(bodies) do
      if k == id then body:Show() else body:Hide() end
    end
    if bar then bar:Select(id) end
    host.selected = id
  end

  bar = UI.TabBar(host, tabs, function(id) showBody(id) end)
  bar:SetPoint("TOPLEFT", 0, 0)

  for i = 1, #tabs do
    local t = tabs[i]
    local body = CreateFrame("Frame", nil, host)
    body:SetPoint("TOPLEFT", 0, -32)
    body:SetPoint("BOTTOMRIGHT", 0, 0)
    if i ~= 1 then body:Hide() end
    bodies[t.id] = body
  end

  host.GetBody = function(_, id)
    return bodies[id]
  end
  host.ShowTab = function(_, id)
    showBody(id)
  end
  host.selected = tabs[1] and tabs[1].id
  return host
end
