--[[
  Compact window scale — independent of UseUIScale / UIParent.

  Addon windows are authored in pixels as if UIParent scale were 1.0.
  In-game UI scale then blows them up (or UseUIScale-off makes them huge).
  FrameScale = compact / UIParent:GetEffectiveScale() keeps GM windows at a
  fixed on-screen size. Default 0.60 of a 1.0 UIParent world.

  Map pins / world-map overlays must NOT use this (they have to match the map).
]]

local UI = GmUI

UI.COMPACT_DEFAULT = 0.60
UI.COMPACT_MIN = 0.42
UI.COMPACT_MAX = 0.90

local tracked = {}

function UI.CompactScale()
  local d = UI.DB()
  local v = tonumber(d.compactScale)
  if not v then
    v = UI.COMPACT_DEFAULT
    d.compactScale = v
  end
  if v < UI.COMPACT_MIN then v = UI.COMPACT_MIN end
  if v > UI.COMPACT_MAX then v = UI.COMPACT_MAX end
  return v
end

function UI.FrameScale(extra)
  extra = tonumber(extra) or 1
  if extra < 0.40 then extra = 0.40 end
  if extra > 2.00 then extra = 2.00 end
  local parent = 1
  if UIParent and UIParent.GetEffectiveScale then
    parent = tonumber(UIParent:GetEffectiveScale()) or 1
  end
  if parent < 0.05 then parent = 1 end
  return (UI.CompactScale() / parent) * extra
end

function UI.ScaleFrame(frame, extra)
  if not frame or not frame.SetScale then return end
  extra = tonumber(extra) or frame._gmuiScaleMul or 1
  if extra < 0.40 then extra = 0.40 end
  if extra > 2.00 then extra = 2.00 end
  frame._gmuiScaleMul = extra
  tracked[frame] = true
  frame:SetScale(UI.FrameScale(extra))
end

function UI.ApplyAllScales()
  local dead = {}
  for f in pairs(tracked) do
    if f and f.SetScale then
      UI.ScaleFrame(f, f._gmuiScaleMul or 1)
    else
      dead[#dead + 1] = f
    end
  end
  for i = 1, #dead do tracked[dead[i]] = nil end
end

function UI.ScaleHud(frame, extra)
  return UI.ScaleFrame(frame, extra)
end

function UI.SetCompactScale(v, quiet)
  v = tonumber(v)
  if not v then return UI.CompactScale() end
  if v < UI.COMPACT_MIN then v = UI.COMPACT_MIN end
  if v > UI.COMPACT_MAX then v = UI.COMPACT_MAX end
  UI.DB().compactScale = v
  UI.ApplyAllScales()
  if not quiet and UI.Chat then
    UI.Chat(string.format("compact UI scale %.0f%% (own scale, not game UI scale)", v * 100))
  end
  return v
end

local ev = CreateFrame("Frame")
ev:RegisterEvent("PLAYER_LOGIN")
ev:RegisterEvent("PLAYER_ENTERING_WORLD")
ev:RegisterEvent("DISPLAY_SIZE_CHANGED")
ev:RegisterEvent("CVAR_UPDATE")
pcall(function() ev:RegisterEvent("UI_SCALE_CHANGED") end)
ev:SetScript("OnEvent", function(_, event, a1)
  if event == "CVAR_UPDATE" then
    local n = string.upper(tostring(a1 or ""))
    if n ~= "UISCALE" and n ~= "USEUISCALE" and n ~= "USE_UISCALE" then
      return
    end
  end
  if UI.ApplyAllScales then UI.ApplyAllScales() end
end)
