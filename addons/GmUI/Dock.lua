local UI = GmUI

UI._docks = UI._docks or {}

local function ensureHost()
  if UI._dockHost then return UI._dockHost end
  local host = UI.CreateWindow({
    id = "dockhost",
    title = "Gm Dock",
    color = "dock",
    width = 640,
    height = 520,
    resizable = true,
    dockable = false,
    x = -40,
    y = 20,
  })
  host.titleFs:SetText(UI.ColorText("textDim", "Dock Panel") .. "  |cff8c96a5(drop tools here)|r")

  local tabRow = CreateFrame("Frame", nil, host.body)
  tabRow:SetPoint("TOPLEFT", 0, 0)
  tabRow:SetPoint("TOPRIGHT", 0, 0)
  tabRow:SetHeight(26)
  host.tabRow = tabRow
  host.tabs = {}
  host.pages = {}
  host.active = nil

  local pageRoot = CreateFrame("Frame", nil, host.body)
  pageRoot:SetPoint("TOPLEFT", 0, -30)
  pageRoot:SetPoint("BOTTOMRIGHT", 0, 0)
  host.pageRoot = pageRoot

  host.RebuildTabs = function(self)
    if self.tabButtons then
      for i = 1, #self.tabButtons do self.tabButtons[i]:Hide() end
    end
    self.tabButtons = self.tabButtons or {}
    local x = 0
    local i = 0
    for id, page in pairs(self.pages) do
      i = i + 1
      local b = self.tabButtons[i]
      local win = UI.GetWindow(id)
      local col = win and UI.ToolColor(win._color or "dock") or UI.ToolColor("dock")
      local label = (win and win._title) or id
      if not b then
        b = UI.Button(self.tabRow, label, 88, 22, nil, "Show tab", nil)
        self.tabButtons[i] = b
      end
      b:Show()
      b:SetLabel(label)
      b._col = col
      if b.bg then
        b.bg:SetTexture(col[1] * 0.25, col[2] * 0.25, col[3] * 0.25, 0.9)
      end
      local stripe = b.accent or b.line
      if stripe then
        stripe:SetTexture(col[1], col[2], col[3], 0.95)
      end
      b:SetPoint("LEFT", x, 0)
      b:SetScript("OnClick", function()
        UI.FocusDockTab(id)
      end)
      x = x + 92
    end
  end

  host.ShowPage = function(self, id)
    for pid, page in pairs(self.pages) do
      if pid == id then page:Show() else page:Hide() end
    end
    self.active = id
    self:SetTitle("Dock — " .. tostring((UI.GetWindow(id) and UI.GetWindow(id)._title) or id))
    self:RebuildTabs()
  end

  UI._dockHost = host
  UI._docks.main = host
  return host
end

function UI.DockWindow(win)
  if not win or not win._gmuiId then return end
  local id = win._gmuiId
  if id == "dockhost" or win._noDock then return end
  local host = ensureHost()
  if win == host then return end
  if win._docked then return end
  local walk = host.pageRoot
  while walk do
    if walk == win then return end
    walk = walk.GetParent and walk:GetParent()
  end

  local page = CreateFrame("Frame", nil, host.pageRoot)
  page:SetAllPoints()
  page:Hide()

  win._dockParent = win:GetParent()
  win._docked = true
  win._dockId = "main"
  win:SetParent(page)
  win:ClearAllPoints()
  win:SetAllPoints(page)
  if win.titleBar then win.titleBar:Hide() end
  if win._gmuiTitleBar then win._gmuiTitleBar:Hide() end
  win:Show()
  page.win = win

  host.pages[id] = page
  host:Show()
  host:ShowPage(id)
  UI.DB().windows[id] = UI.DB().windows[id] or {}
  UI.DB().windows[id].docked = true
  UI.Chat("docked " .. tostring(win._title or id))
end

function UI.UndockWindow(win)
  if not win or not win._docked then return end
  local host = UI._dockHost
  local id = win._gmuiId
  local page = host and host.pages[id]
  win._docked = false
  win._dockId = nil
  win:SetParent(UIParent)
  win:ClearAllPoints()
  win:SetPoint("CENTER", UIParent, "CENTER", 40, 20)
  if win.titleBar then win.titleBar:Show() end
  if win._gmuiTitleBar then win._gmuiTitleBar:Show() end
  win:SetWidth(win:GetWidth())
  if page then
    page:Hide()
    page:ClearAllPoints()
    host.pages[id] = nil
    page.win = nil
  end
  if host then
    host.pages[id] = nil
    if host.active == id then
      local nextId = next(host.pages)
      if nextId then host:ShowPage(nextId) else host:Hide() end
    else
      host:RebuildTabs()
    end
  end
  UI.DB().windows[id] = UI.DB().windows[id] or {}
  UI.DB().windows[id].docked = false
  win:Show()
  UI.Chat("undocked " .. tostring(win._title or id))
end

function UI.FocusDockTab(id)
  local host = ensureHost()
  if not host.pages[id] then
    local win = UI.GetWindow(id)
    if win then UI.DockWindow(win) end
    return
  end
  host:Show()
  host:ShowPage(id)
end

function UI.ShowDock()
  ensureHost():Show()
end

function UI.RestoreDocks()
  for id, wcfg in pairs(UI.DB().windows) do
    if id == "dockhost" then
      wcfg.docked = false
    elseif wcfg.docked then
      local win = UI.GetWindow(id)
      if win and not win._noDock then UI.DockWindow(win) end
    end
  end
end
