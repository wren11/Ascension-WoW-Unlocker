local UI = {}
_G.GmUI = UI

GmUIDB = GmUIDB or {}

UI.VERSION = "1.4.2"
-- WrenLabs — black / antique gold. Matches website + launcher + GMToolBox.
UI.THEME_REVISION = 5

local DEFAULT_THEME = {
  accent      = { 0.788, 0.635, 0.153 },  -- #C9A227
  accent2     = { 0.910, 0.773, 0.278 },  -- #E8C547
  accent3     = { 0.545, 0.412, 0.078 },  -- #8B6914
  warn        = { 0.910, 0.773, 0.278 },
  danger      = { 0.769, 0.271, 0.235 },
  ok          = { 0.239, 0.604, 0.416 },
  text        = { 0.957, 0.945, 0.910 },  -- #F4F1E8
  textDim     = { 0.718, 0.667, 0.541 },
  textGold    = { 0.910, 0.773, 0.278 },
  bg          = { 0.020, 0.020, 0.020 },
  bgPanel     = { 0.071, 0.071, 0.071 },
  bgInput     = { 0.047, 0.047, 0.047 },
  border      = { 0.788, 0.635, 0.153 },
  tabActive   = { 0.788, 0.635, 0.153 },
  tabIdle     = { 0.047, 0.047, 0.047 },
  toolbarBg   = { 0.020, 0.020, 0.020 },
  silver      = { 0.718, 0.667, 0.541 },
  rowAlt      = { 0.047, 0.047, 0.047 },
  alpha       = 0.96,
  windowAlpha = 0.94,
  panelAlpha  = 0.96,
  snapPx      = 28,
  toolbarScale = 1,
  toolbarHeight = 32,
}

-- Class-rotation accents on gold chrome (21 Ascension classes palette).
UI.TOOL_COLORS = {
  toolbox   = { 0.788, 0.635, 0.153 },
  teleport  = { 0.145, 0.388, 0.922 },
  combat    = { 0.769, 0.271, 0.235 },
  explore   = { 0.851, 0.467, 0.024 },
  maptp     = { 0.078, 0.722, 0.651 },
  lab       = { 0.420, 0.180, 0.710 },
  hunt      = { 0.918, 0.345, 0.047 },
  gather    = { 0.239, 0.604, 0.416 },
  botbuilder= { 0.788, 0.635, 0.153 },
  actionflow= { 0.145, 0.388, 0.922 },
  wsg       = { 0.918, 0.345, 0.047 },
  bgafk     = { 0.420, 0.180, 0.710 },
  kx        = { 0.769, 0.271, 0.235 },
  nearby    = { 0.078, 0.722, 0.651 },
  loot      = { 0.831, 0.686, 0.216 },
  dock      = { 0.718, 0.667, 0.541 },
  settings  = { 0.718, 0.667, 0.541 },
  danger    = { 0.769, 0.271, 0.235 },
  warn      = { 0.910, 0.773, 0.278 },
  ok        = { 0.239, 0.604, 0.416 },
}

UI.THEME_ART = "Interface\\AddOns\\GmUI\\Media\\theme-overlord"

function UI.DB()
  local d = GmUIDB
  d.theme = d.theme or {}
  local rev = tonumber(d.themeRevision) or 0
  local force = rev < (UI.THEME_REVISION or 1)
  for k, v in pairs(DEFAULT_THEME) do
    if force or d.theme[k] == nil then
      if type(v) == "table" then
        d.theme[k] = { v[1], v[2], v[3] }
      else
        d.theme[k] = v
      end
    end
  end
  if force then
    d.themeRevision = UI.THEME_REVISION
  end
  d.windows = d.windows or {}
  d.docks = d.docks or {}
  d.toolbar = d.toolbar or { locked = false, y = 0, scale = 1, hidden = false }
  d.tools = d.tools or {}
  return d
end

function UI.T(key)
  return UI.DB().theme[key]
end

function UI.RGB(key, a)
  local c = UI.T(key) or DEFAULT_THEME[key] or { 1, 1, 1 }
  return c[1], c[2], c[3], a
end

function UI.Hex(r, g, b)
  return string.format("|cff%02x%02x%02x",
    math.floor((r or 1) * 255),
    math.floor((g or 1) * 255),
    math.floor((b or 1) * 255))
end

function UI.ColorText(key, text)
  local r, g, b = UI.RGB(key)
  return UI.Hex(r, g, b) .. tostring(text) .. "|r"
end

function UI.ToolColor(id)
  return UI.TOOL_COLORS[id] or UI.T("accent")
end

function UI.PaintSolid(tex, key, a)
  local r, g, b = UI.RGB(key)
  tex:SetTexture(r, g, b, a or UI.T("windowAlpha") or 0.22)
end

function UI.ApplyBackdrop(frame, kind)
  kind = kind or "window"
  local a = (kind == "toolbar") and 0.78
      or (kind == "panel") and (UI.T("panelAlpha") or 0.72)
      or (UI.T("windowAlpha") or 0.70)
  if not frame._gmuiBg then
    frame._gmuiBg = frame:CreateTexture(nil, "BACKGROUND")
    frame._gmuiBg:SetAllPoints()
  end
  local bgKey = (kind == "toolbar") and "toolbarBg" or (kind == "panel" and "bgPanel" or "bg")
  UI.PaintSolid(frame._gmuiBg, bgKey, a)

  if not frame._gmuiEdge then
    frame._gmuiEdge = frame:CreateTexture(nil, "BORDER")
    frame._gmuiEdge:SetPoint("TOPLEFT", 0, 0)
    frame._gmuiEdge:SetPoint("TOPRIGHT", 0, 0)
    frame._gmuiEdge:SetHeight(2)
  end
  local er, eg, eb = UI.RGB("border")
  frame._gmuiEdge:SetTexture(er, eg, eb, 0.90)

  if not frame._gmuiEdgeB then
    frame._gmuiEdgeB = frame:CreateTexture(nil, "BORDER")
    frame._gmuiEdgeB:SetPoint("BOTTOMLEFT", 0, 0)
    frame._gmuiEdgeB:SetPoint("BOTTOMRIGHT", 0, 0)
    frame._gmuiEdgeB:SetHeight(1)
  end
  frame._gmuiEdgeB:SetTexture(er, eg, eb, 0.40)

  if not frame._gmuiEdgeL then
    frame._gmuiEdgeL = frame:CreateTexture(nil, "BORDER")
    frame._gmuiEdgeL:SetPoint("TOPLEFT", 0, 0)
    frame._gmuiEdgeL:SetPoint("BOTTOMLEFT", 0, 0)
    frame._gmuiEdgeL:SetWidth(1)
  end
  frame._gmuiEdgeL:SetTexture(er, eg, eb, 0.45)

  if not frame._gmuiEdgeR then
    frame._gmuiEdgeR = frame:CreateTexture(nil, "BORDER")
    frame._gmuiEdgeR:SetPoint("TOPRIGHT", 0, 0)
    frame._gmuiEdgeR:SetPoint("BOTTOMRIGHT", 0, 0)
    frame._gmuiEdgeR:SetWidth(1)
  end
  frame._gmuiEdgeR:SetTexture(er, eg, eb, 0.45)

  -- Crimson accent rail
  if not frame._gmuiRail then
    frame._gmuiRail = frame:CreateTexture(nil, "ARTWORK")
    frame._gmuiRail:SetPoint("TOPLEFT", 0, 0)
    frame._gmuiRail:SetPoint("BOTTOMLEFT", 0, 0)
    frame._gmuiRail:SetWidth(3)
  end
  local ar, ag, ab = UI.RGB("accent")
  frame._gmuiRail:SetTexture(ar, ag, ab, 0.90)

  frame:SetAlpha(1)
end

function UI.ApplyThemeArt(parent, size)
  if not parent then return nil end
  size = size or 36
  if parent._gmuiArt then return parent._gmuiArt end
  local t = parent:CreateTexture(nil, "OVERLAY")
  t:SetWidth(size)
  t:SetHeight(size)
  t:SetPoint("TOPRIGHT", parent, "TOPRIGHT", -6, -6)
  local ok = pcall(function()
    t:SetTexture(UI.THEME_ART)
  end)
  if not ok or not t:GetTexture() then
    local r, g, b = UI.RGB("accent")
    t:SetTexture(r, g, b, 0.55)
  end
  parent._gmuiArt = t
  return t
end

function UI.Chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cffe8c547[GmUI]|r " .. tostring(msg))
  end
end

function UI.ApplyGloss(frame, kind)
  if UI.Gloss and UI.Gloss.Apply then return UI.Gloss.Apply(frame, kind) end
  return frame
end
