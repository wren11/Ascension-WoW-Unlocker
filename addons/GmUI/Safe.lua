local UI = GmUI

-- Floor for all periodic intervals (central Scheduler ticks at 0.30s).
UI.MIN_INTERVAL = 0.30
UI.ENGINE_INTERVAL = 0.30
UI.UI_INTERVAL = 0.50

UI.OUR_ADDONS = {
  BotBuilder = true, GmUI = true, GmToolbox = true, GmTeleport = true,
  GmCombat = true, GmExplore = true, GmLab = true, GmMapTeleport = true,
  HuntingBot = true, GatherBot = true, GmGatherPins = true, WsgCap = true, CtfCap = true, BgAfk = true,
  KnightOfXoroth = true, GmApiBrowser = true, GmCmds = true, GmShared = true,
  GmChatCapture = true, GmTooltipFix = true, PlayerProfileExport = true,
  GmNearby = true,
}

local function isOurs(name)
  if not name then return false end
  name = tostring(name)
  if UI.OUR_ADDONS[name] then return true end
  if string.sub(name, 1, 2) == "Gm" then return true end
  return false
end

function UI.ClampInterval(sec, fallback)
  local v = tonumber(sec) or tonumber(fallback) or UI.MIN_INTERVAL
  if v < UI.MIN_INTERVAL then v = UI.MIN_INTERVAL end
  return v
end

function UI.InstallTaintMute()
  if UI._taintMute then return end
  UI._taintMute = true
  if type(StaticPopup_Show) == "function" then
    local orig = StaticPopup_Show
    StaticPopup_Show = function(which, text1, text2, data, ...)
      if which == "ADDON_ACTION_FORBIDDEN" or which == "ADDON_ACTION_BLOCKED" then
        if isOurs(text1) or isOurs(text2) then
          return nil
        end
      end
      return orig(which, text1, text2, data, ...)
    end
  end
  if UIErrorsFrame and UIErrorsFrame.AddMessage then
    local origAdd = UIErrorsFrame.AddMessage
    UIErrorsFrame.AddMessage = function(self, msg, ...)
      if type(msg) == "string" and string.find(msg, "blocked from an action", 1, true) then
        return
      end
      if type(msg) == "string" and string.find(msg, "Interface action failed", 1, true) then
        return
      end
      return origAdd(self, msg, ...)
    end
  end
end

function UI.ClearTaint()
  if type(GmClearTaint) == "function" then pcall(GmClearTaint) end
  if type(ForceClearTaint) == "function" then pcall(ForceClearTaint) end
end

function UI.SafeCall(fn, ...)
  if type(fn) ~= "function" then return nil, "nil" end
  UI.ClearTaint()
  if type(GmHwEvent) == "function" then pcall(GmHwEvent, 1) end
  local ok, a, b, c, d = pcall(fn, ...)
  if not ok then return nil, a end
  return a, b, c, d
end

--- Engine pump — public API unchanged; timing owned by UI.Scheduler.
function UI.MakePump(period, tickFn)
  return UI.Scheduler.RegisterPump(period, tickFn)
end

--- Shown-frame UI refresh — public API unchanged; no per-frame OnUpdate.
function UI.OnShownUpdate(frame, period, fn)
  UI.Scheduler.RegisterShownUpdate(frame, period, fn)
end
