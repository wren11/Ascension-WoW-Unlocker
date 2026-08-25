--[[
  GmSession — persisted bot run flags for watchdog / relog resume.
  Watchdog (GMToolBox) calls GmSession_Resume() after reconnect.
  Bots mark themselves on Start/Stop via light wrappers installed at PLAYER_LOGIN.
]]

GmSessionDB = GmSessionDB or {}
GmSession = GmSession or {}

local S = GmSession
local KNOWN = { "gather", "hbot", "bgafk", "botbuilder", "actionflow", "ctf", "combat", "explore", "wsg" }

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmSession]|r " .. tostring(msg))
  end
end

local function flags()
  GmSessionDB.flags = GmSessionDB.flags or {}
  return GmSessionDB.flags
end

function GmSession_Mark(name, running)
  name = string.lower(tostring(name or ""))
  if name == "" then return end
  local f = flags()
  if running then
    f[name] = true
  else
    f[name] = nil
  end
end

function GmSession_IsMarked(name)
  name = string.lower(tostring(name or ""))
  return flags()[name] and true or false
end

function GmSession_StatusLine()
  local parts = {}
  for i = 1, #KNOWN do
    local k = KNOWN[i]
    if flags()[k] then parts[#parts + 1] = k end
  end
  if #parts == 0 then return "(none marked)" end
  return table.concat(parts, ", ")
end

local RESUME = {
  gather = function()
    if SlashCmdList and SlashCmdList.GATHERBOT then
      SlashCmdList.GATHERBOT("start")
    elseif _G.GatherBot and _G.GatherBot.Start then
      _G.GatherBot.Start()
    end
  end,
  hbot = function()
    if SlashCmdList and SlashCmdList.HUNTINGBOT then
      SlashCmdList.HUNTINGBOT("start")
    elseif _G.HuntingBot and _G.HuntingBot.Start then
      _G.HuntingBot.Start()
    end
  end,
  bgafk = function()
    if SlashCmdList and SlashCmdList.BGAFK then
      SlashCmdList.BGAFK("start")
    elseif _G.BgAfk and _G.BgAfk.Start then
      _G.BgAfk.Start()
    end
  end,
  botbuilder = function()
    if _G.BotBuilderDB then _G.BotBuilderDB.engineOn = true end
    if _G.BotBuilder and _G.BotBuilder.StartEngine then
      _G.BotBuilder.StartEngine()
    elseif SlashCmdList and SlashCmdList.BOTBUILDER then
      SlashCmdList.BOTBUILDER("start")
    end
  end,
  actionflow = function()
    if _G.ActionFlowDB then _G.ActionFlowDB.engineOn = true end
    if _G.ActionFlow and _G.ActionFlow.Runtime and _G.ActionFlow.Runtime.Start then
      _G.ActionFlow.Runtime.Start()
    elseif SlashCmdList and SlashCmdList.ACTIONFLOW then
      SlashCmdList.ACTIONFLOW("start")
    end
  end,
  ctf = function()
    if SlashCmdList and SlashCmdList.CTFCAP then
      local prof = GmSessionDB.lastCtfProfile
      if prof and prof ~= "" then
        SlashCmdList.CTFCAP("start " .. prof)
      else
        SlashCmdList.CTFCAP("start")
      end
    elseif _G.CtfCap and _G.CtfCap.Start then
      _G.CtfCap.Start(GmSessionDB.lastCtfProfile)
    end
  end,
  wsg = function()
    if _G.WsgCap and _G.WsgCap.Start then
      _G.WsgCap.Start()
    elseif _G.CtfCap and _G.CtfCap.Start then
      _G.CtfCap.Start("wsg")
    end
  end,
  combat = function()
    if SlashCmdList and SlashCmdList.GMCOMBAT then
      SlashCmdList.GMCOMBAT("start")
    elseif _G.GmCombat and _G.GmCombat.Scheduler and _G.GmCombat.Scheduler.Start then
      _G.GmCombat.Scheduler.Start()
    end
  end,
  explore = function()
    if SlashCmdList and SlashCmdList.GMEXPLORE then
      SlashCmdList.GMEXPLORE("start")
    elseif _G.GmExplore and _G.GmExplore.Start then
      _G.GmExplore.Start()
    end
  end,
}

function GmSession_Resume()
  local n = 0
  for i = 1, #KNOWN do
    local k = KNOWN[i]
    if flags()[k] then
      local fn = RESUME[k]
      if fn then
        local ok, err = pcall(fn)
        if ok then n = n + 1
        else chat("|cffff4444resume " .. k .. "|r " .. tostring(err)) end
      end
    end
  end
  if n > 0 then
    chat("|cff2ecc71resumed|r " .. tostring(n) .. " marked bot(s)")
  end
  return n
end

local function wrapStartStop(mod, markName, startKey, stopKey)
  if type(mod) ~= "table" then return end
  local tag = "_gmSessionWrap_" .. markName
  if mod[tag] then return end
  local origStart = mod[startKey]
  local origStop = mod[stopKey]
  if type(origStart) == "function" then
    mod[startKey] = function(...)
      local r = origStart(...)
      if r ~= false then GmSession_Mark(markName, true) end
      return r
    end
  end
  if type(origStop) == "function" then
    mod[stopKey] = function(...)
      local r = origStop(...)
      GmSession_Mark(markName, false)
      return r
    end
  end
  mod[tag] = true
end

local function liveRunning(name)
  if name == "gather" then
    return _G.GatherBot and _G.GatherBot.IsRunning and _G.GatherBot.IsRunning()
  end
  if name == "hbot" then
    return _G.HuntingBot and _G.HuntingBot.IsRunning and _G.HuntingBot.IsRunning()
  end
  if name == "bgafk" then
    return _G.BgAfk and ((_G.BgAfk.IsRunning and _G.BgAfk.IsRunning())
      or (_G.BgAfk.S and _G.BgAfk.S.running))
  end
  if name == "botbuilder" then
    return (_G.BotBuilder and _G.BotBuilder.Scheduler and _G.BotBuilder.Scheduler.IsRunning
        and _G.BotBuilder.Scheduler.IsRunning())
      or (_G.BotBuilderDB and _G.BotBuilderDB.engineOn)
  end
  if name == "actionflow" then
    return (_G.ActionFlow and _G.ActionFlow.Runtime and _G.ActionFlow.Runtime.IsRunning
        and _G.ActionFlow.Runtime.IsRunning())
      or (_G.ActionFlowDB and _G.ActionFlowDB.engineOn)
  end
  if name == "ctf" then
    return _G.CtfCap and ((_G.CtfCap.IsRunning and _G.CtfCap.IsRunning())
      or (_G.CtfCap.S and _G.CtfCap.S.running))
  end
  if name == "wsg" then
    return _G.WsgCap and _G.WsgCap.IsRunning and _G.WsgCap.IsRunning()
  end
  if name == "combat" then
    return _G.GmCombat and _G.GmCombat.Scheduler and _G.GmCombat.Scheduler.IsRunning
      and _G.GmCombat.Scheduler.IsRunning()
  end
  if name == "explore" then
    return _G.GmExplore and _G.GmExplore.Scheduler and _G.GmExplore.Scheduler.IsRunning
      and _G.GmExplore.Scheduler.IsRunning()
  end
  return false
end

function GmSession_Pulse()
  for i = 1, #KNOWN do
    local k = KNOWN[i]
    local on = liveRunning(k)
    if on then GmSession_Mark(k, true)
    elseif flags()[k] and not on then
      -- keep mark through loading screens; only clear on explicit Stop
    end
  end
  local line = GmSession_StatusLine()
  if type(GmReportPlayer) == "function" and UnitName then
    local name = UnitName("player")
    if name then
      local extra = "wd:" .. tostring(line or "")
      if type(GmPlayerFlags) == "function" then
        extra = extra .. string.format("|pf:0x%08X", tonumber(GmPlayerFlags()) or 0)
      end
      if type(IsGameMaster) == "function" then
        local ok, v = pcall(IsGameMaster)
        extra = extra .. ((ok and v) and "|igm:1" or "|igm:0")
      end
      pcall(GmReportPlayer, UnitGUID("player") or "", name, -1, -1, -1, -1, extra)
    end
  end
  return line
end

local function installHooks()
  wrapStartStop(_G.GatherBot, "gather", "Start", "Stop")
  wrapStartStop(_G.HuntingBot, "hbot", "Start", "Stop")
  wrapStartStop(_G.BgAfk, "bgafk", "Start", "Stop")
  wrapStartStop(_G.CtfCap, "ctf", "Start", "Stop")
  wrapStartStop(_G.WsgCap, "wsg", "Start", "Stop")
  if _G.GmCombat and _G.GmCombat.Scheduler then
    wrapStartStop(_G.GmCombat.Scheduler, "combat", "Start", "Stop")
  end
  if _G.GmExplore and _G.GmExplore.Scheduler then
    wrapStartStop(_G.GmExplore.Scheduler, "explore", "Start", "Stop")
  elseif _G.GmExplore then
    wrapStartStop(_G.GmExplore, "explore", "Start", "Stop")
  end
  if _G.CtfCap and _G.CtfCap.UseProfile and not _G.CtfCap._gmSessionProfileWrap then
    local orig = _G.CtfCap.UseProfile
    _G.CtfCap.UseProfile = function(id, ...)
      GmSessionDB.lastCtfProfile = id
      return orig(id, ...)
    end
    _G.CtfCap._gmSessionProfileWrap = true
  end
  if _G.BotBuilder and _G.BotBuilder.StartEngine and not _G.BotBuilder._gmSessionWrap then
    local oStart = _G.BotBuilder.StartEngine
    local oStop = _G.BotBuilder.StopEngine
    _G.BotBuilder.StartEngine = function(...)
      local r = oStart(...)
      GmSession_Mark("botbuilder", true)
      return r
    end
    if type(oStop) == "function" then
      _G.BotBuilder.StopEngine = function(...)
        local r = oStop(...)
        GmSession_Mark("botbuilder", false)
        return r
      end
    end
    _G.BotBuilder._gmSessionWrap = true
  end
  if _G.ActionFlow and _G.ActionFlow.Runtime and not _G.ActionFlow._gmSessionWrap then
    wrapStartStop(_G.ActionFlow.Runtime, "actionflow", "Start", "Stop")
    _G.ActionFlow._gmSessionWrap = true
  end
end

SLASH_GMSESSION1 = "/gmsession"
SlashCmdList.GMSESSION = function(msg)
  msg = string.lower(tostring(msg or ""):match("^%s*(.-)%s*$") or "")
  local cmd, rest = string.match(msg, "^(%S*)%s*(.*)$")
  if cmd == "" or cmd == "help" then
    chat("usage: /gmsession status | resume | pulse | mark <" .. table.concat(KNOWN, "|") .. "> [on|off]")
    return
  end
  if cmd == "status" then
    chat("marked: " .. GmSession_StatusLine())
    return
  end
  if cmd == "resume" then
    GmSession_Resume()
    return
  end
  if cmd == "pulse" then
    chat("pulse: " .. tostring(GmSession_Pulse()))
    return
  end
  if cmd == "mark" then
    local name, onoff = string.match(rest, "^(%S+)%s*(.*)$")
    name = string.lower(tostring(name or ""))
    if name == "" then
      chat("mark which? " .. table.concat(KNOWN, ", "))
      return
    end
    local known = false
    for i = 1, #KNOWN do if KNOWN[i] == name then known = true break end end
    if not known then
      chat("|cffff4444unknown|r — " .. table.concat(KNOWN, ", "))
      return
    end
    onoff = string.lower(onoff or "")
    local run = (onoff == "" or onoff == "on" or onoff == "1" or onoff == "true")
    if onoff == "off" or onoff == "0" or onoff == "false" then run = false end
    GmSession_Mark(name, run)
    chat(string.format("mark %s → %s", name, run and "on" or "off"))
    return
  end
  chat("usage: /gmsession status | resume | mark <name> [on|off]")
end

local boot = CreateFrame("Frame")
boot:RegisterEvent("ADDON_LOADED")
boot:RegisterEvent("PLAYER_LOGIN")
boot:RegisterEvent("PLAYER_LOGOUT")
boot:RegisterEvent("PLAYER_LEAVING_WORLD")
boot:SetScript("OnEvent", function(self, event, arg1)
  if event == "ADDON_LOADED" then
    if arg1 == "GmShared" or arg1 == "HuntingBot" or arg1 == "GatherBot"
        or arg1 == "BgAfk" or arg1 == "CtfCap" or arg1 == "WsgCap"
        or arg1 == "BotBuilder" or arg1 == "ActionFlow" or arg1 == "GmCombat" or arg1 == "GmExplore"
        or arg1 == "LootCollector" then
      installHooks()
    end
    return
  end
  if event == "PLAYER_LOGIN" then
    installHooks()
    return
  end
  if event == "PLAYER_LOGOUT" or event == "PLAYER_LEAVING_WORLD" then
    pcall(GmSession_Pulse)
  end
end)

S.Pulse = GmSession_Pulse

S.Mark = GmSession_Mark
S.Resume = GmSession_Resume
S.StatusLine = GmSession_StatusLine
