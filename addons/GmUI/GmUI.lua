local UI = GmUI

function UI.RegisterDefaults()
  UI.RegisterTool({
    id = "toolbox", order = 10, label = "Toolbox", color = "toolbox",
    addon = "GmToolbox",
    tip = "ExtProxy actions + client hacks",
    toggle = function()
      if GmToolbox and GmToolbox.Toggle then GmToolbox.Toggle()
      else SlashCmdList["GMTOOLBOX"]("") end
    end,
  })
  UI.RegisterTool({
    id = "teleport", order = 20, label = "Teleport", color = "teleport",
    addon = "GmTeleport",
    tip = "GmTeleport pin / go · /tpface",
    toggle = function()
      if SlashCmdList["GMTELEPORT"] then
        SlashCmdList["GMTELEPORT"]("")
      elseif type(GmTeleport_Toggle) == "function" then
        GmTeleport_Toggle()
      elseif UI.Chat then
        UI.Chat("|cffff4444GmTeleport not loaded|r — enable it in AddOns (character list), then /reload")
      end
    end,
  })
  UI.RegisterTool({
    id = "combat", order = 30, label = "Combat", color = "combat",
    addon = "GmCombat",
    tip = "GmCombat face / kill / loot",
    toggle = function()
      if SlashCmdList["GMCOMBAT"] then SlashCmdList["GMCOMBAT"]("") end
    end,
  })
  UI.RegisterTool({
    id = "explore", order = 40, label = "Explore", color = "explore",
    addon = "GmExplore",
    tip = "GmExplore tour",
    toggle = function()
      if type(GmExplore) == "table" and type(GmExplore.ToggleGui) == "function" then
        local ok, err = pcall(GmExplore.ToggleGui)
        if not ok and UI.Chat then
          UI.Chat("|cffff4444Explore GUI error|r — " .. tostring(err))
        end
      elseif SlashCmdList["GMEXPLORE"] then
        SlashCmdList["GMEXPLORE"]("gui")
      elseif UI.Chat then
        UI.Chat("|cffff4444GmExplore not loaded|r — enable it in AddOns, then /reload")
      end
    end,
  })
  UI.RegisterTool({
    id = "maptp", order = 50, label = "MapTP", color = "maptp",
    addon = "GmMapTeleport",
    tip = "World map teleport — open menu / toggle",
    toggle = function()
      if UI.ToggleMapTpPanel then UI.ToggleMapTpPanel()
      elseif GmMapTeleport and GmMapTeleport.OpenMenu then GmMapTeleport.OpenMenu()
      elseif SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("menu") end
    end,
  })
  UI.RegisterTool({
    id = "lab", order = 60, label = "Lab", color = "lab",
    addon = "GmLab",
    tip = "Non-GM ExtProxy probes",
    toggle = function()
      if SlashCmdList["GMLAB"] then SlashCmdList["GMLAB"]("") end
    end,
  })
  UI.RegisterTool({
    id = "hunt", order = 70, label = "Hunt", color = "hunt",
    addon = "HuntingBot",
    tip = "HuntingBot control panel",
    toggle = function()
      if HuntingBot_ToggleUI then HuntingBot_ToggleUI()
      elseif SlashCmdList["HUNTINGBOT"] then SlashCmdList["HUNTINGBOT"]("gui") end
    end,
  })
  UI.RegisterTool({
    id = "botbuilder", order = 80, label = "Bots", color = "botbuilder",
    addon = "BotBuilder",
    tip = "BotBuilder IDE (WeakAuras-style)",
    toggle = function()
      if BotBuilder and BotBuilder.Toggle then BotBuilder.Toggle()
      elseif SlashCmdList["BOTBUILDER"] then SlashCmdList["BOTBUILDER"]("gui") end
    end,
  })
  UI.RegisterTool({
    id = "actionflow", order = 82, label = "Flow", color = "actionflow",
    addon = "ActionFlow",
    tip = "ActionFlow node canvas — /af",
    toggle = function()
      if ActionFlow and ActionFlow.Toggle then ActionFlow.Toggle()
      elseif SlashCmdList["ACTIONFLOW"] then SlashCmdList["ACTIONFLOW"]("gui") end
    end,
  })
  UI.RegisterTool({
    id = "gather", order = 75, label = "Gather", color = "gather",
    addon = "GatherBot",
    tip = "GatherBot control panel",
    toggle = function()
      if GatherBot_ToggleUI then GatherBot_ToggleUI()
      elseif SlashCmdList["GATHERBOT"] then SlashCmdList["GATHERBOT"]("gui") end
    end,
  })
  -- API chip is owned by GmApiBrowser (RegisterTool id=apibrowser) to avoid a duplicate.
  UI.RegisterTool({
    id = "wsg", order = 90, label = "CTF", color = "wsg",
    deps = { "CtfCap", "WsgCap" },
    tip = "CTF flag bots (WSG / Twin Peaks / EotS)",
    toggle = function()
      if UI.ToggleWsgPanel then UI.ToggleWsgPanel()
      elseif SlashCmdList["CTFCAP"] then SlashCmdList["CTFCAP"]("status")
      elseif SlashCmdList["WSGCAP"] then SlashCmdList["WSGCAP"]("status") end
    end,
  })
  UI.RegisterTool({
    id = "bgafk", order = 95, label = "BG AFK", color = "bgafk",
    addon = "BgAfk",
    tip = "Random BG AFK farm (sky TP + requeue)",
    toggle = function()
      if UI.ToggleBgAfkPanel then UI.ToggleBgAfkPanel()
      elseif SlashCmdList["BGAFK"] then SlashCmdList["BGAFK"]("status") end
    end,
  })
  UI.RegisterTool({
    id = "kx", order = 96, label = "KX", color = "kx",
    addon = "KnightOfXoroth",
    tip = "Knight of Xoroth BG DPS (included with Core)",
    toggle = function()
      if KnightOfXoroth_ToggleUI then KnightOfXoroth_ToggleUI()
      elseif UI.ToggleKxPanel then UI.ToggleKxPanel()
      elseif SlashCmdList["KNIGHTOFXOROTH"] then SlashCmdList["KNIGHTOFXOROTH"]("gui") end
    end,
  })
  UI.RegisterTool({
    id = "nearby", order = 97, label = "Nearby", color = "nearby",
    addon = "GmNearby",
    tip = "Whisper OM players we have not messaged. Any faction. List grows.",
    toggle = function()
      if GmNearby and GmNearby.Toggle then GmNearby.Toggle()
      elseif SlashCmdList["GMNEARBY"] then SlashCmdList["GMNEARBY"]("gui")
      elseif UI.Chat then
        UI.Chat("|cffff4444GmNearby not loaded|r — enable it in AddOns, then /reload")
      end
    end,
  })
  UI.RegisterTool({
    id = "loot", order = 98, label = "Loot", color = "loot",
    addon = "LootCollector",
    tip = "LootCollector Hunt — teleport, under-map loot, gold-farm loop",
    toggle = function()
      local Hunt = LootCollector and LootCollector.GetModule and LootCollector:GetModule("Hunt", true)
      if Hunt and Hunt.Toggle then
        Hunt:Toggle()
      elseif SlashCmdList["LCHUNT"] then
        SlashCmdList["LCHUNT"]("hud")
        local Viewer = LootCollector and LootCollector.GetModule and LootCollector:GetModule("Viewer", true)
        if Viewer and Viewer.Toggle then Viewer:Toggle() end
      elseif UI.Chat then
        UI.Chat("|cffff4444LootCollector not loaded|r — enable it in AddOns, then /reload")
      end
    end,
  })
end

function UI.AdoptAll()
  local adopt = {
    { "GmToolboxFrame", "toolbox", "GmToolbox", "toolbox" },
    { "GmLabFrame", "lab", "GmLab", "lab" },
    { "GmExploreFrame", "explore", "GmExplore", "explore" },
    { "HuntingBotUI", "hunt", "HuntingBot", "hunt" },
    { "BotBuilderFrame", "botbuilder", "BotBuilder", "botbuilder" },
    { "LootCollectorViewerWindow", "loot", "LootCollector", "loot" },
  }
  for i = 1, #adopt do
    local name, id, title, color = adopt[i][1], adopt[i][2], adopt[i][3], adopt[i][4]
    local f = _G[name]
    if f and not f._gmuiAdopted then
      UI.Adopt(f, { id = id, title = title, color = color })
    end
  end
  for name, frame in pairs(_G) do
    if type(name) == "string" and type(frame) == "table" and frame.GetObjectType
        and frame:GetObjectType() == "Frame" then
      if string.find(name, "GmTeleport", 1, true) and not frame._gmuiAdopted and frame.SetMovable then
        if frame:GetName() and frame:GetWidth() > 100 then
          UI.Adopt(frame, { id = "teleport", title = "GmTeleport", color = "teleport" })
        end
      end
    end
  end
end

function UI.ToggleSettings()
  if UI._settings and UI._settings:IsShown() then
    UI._settings:Hide()
    return
  end
  if not UI._settings then
    local f = UI.CreateWindow({
      id = "settings",
      title = "GmUI Settings",
      color = "settings",
      width = 380,
      height = 500,
      resizable = true,
    })
    local y = -4
    local function row()
      y = y - 28
      return y + 28
    end

    UI.Header(f.body, "Appearance"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)

    UI.Label(f.body, "Compact addon scale (own scale — not game UI scale)"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 18)
    local slScale = UI.Slider(f.body, 240, 0.42, 0.90, 0.02,
      function() return UI.CompactScale and UI.CompactScale() or 0.60 end,
      function(v)
        if UI.SetCompactScale then UI.SetCompactScale(v, true) end
      end,
      "GM windows stay this size even if you change WoW UI Scale")
    slScale:SetPoint("TOPLEFT", 0, y)
    slScale.label:SetPoint("LEFT", slScale, "RIGHT", 8, 0)
    y = UI.Row(f.body, y, 28)

    UI.Label(f.body, "Window background opacity (lower = more transparent)"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 18)
    local sl = UI.Slider(f.body, 240, 0.08, 0.85, 0.01,
      function() return UI.T("windowAlpha") end,
      function(v)
        UI.DB().theme.windowAlpha = v
        UI.ForEachWindow(function(_, w)
          if w._gmuiBg then
            local r, g, b = UI.RGB("bg")
            w._gmuiBg:SetTexture(r, g, b, v)
          end
        end)
      end,
      "Background fill alpha")
    sl:SetPoint("TOPLEFT", 0, y)
    sl.label:SetPoint("LEFT", sl, "RIGHT", 8, 0)
    y = UI.Row(f.body, y, 28)

    UI.Label(f.body, "Snap distance (pixels)"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 18)
    local sl2 = UI.Slider(f.body, 240, 8, 64, 1,
      function() return UI.T("snapPx") end,
      function(v) UI.DB().theme.snapPx = v end)
    sl2:SetPoint("TOPLEFT", 0, y)
    sl2.label:SetPoint("LEFT", sl2, "RIGHT", 8, 0)
    y = UI.Row(f.body, y, 32)

    UI.Label(f.body, "Toolbar is reserved at the top — game UI sits below it.",
      "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)

    local hideTb = UI.Check(f.body, "Hide toolbar",
      function() return UI.DB().toolbar.hidden end,
      function(v)
        UI.DB().toolbar.hidden = v
        if v then UI.Toolbar.Hide() else UI.Toolbar.Show() end
      end)
    hideTb:SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 28)

    UI.Header(f.body, "Visual effects"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)

    local glossChk = UI.Check(f.body, "Glossy panels (wet-panel depth)",
      function() return UI.DB().theme.gloss end,
      function(v)
        UI.Gloss.SetEnabled(v)
        UI.ApplyGloss(f, "window")
      end, "Glossy highlight/shadow finish on windows, buttons, the toolbar")
    glossChk:SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 26)

    local shimmerChk = UI.Check(f.body, "Shimmer sweep (active tabs)",
      function() return UI.DB().theme.shimmer end,
      function(v)
        UI.DB().theme.shimmer = v and true or false
        if not v and UI.Gloss then
          -- clearing live shimmers is best-effort; they re-attach on next select
        end
      end, "Animated highlight band on the active tool chip / tab")
    shimmerChk:SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 26)

    local sparkChk = UI.Check(f.body, "Traveling spark (top bar)",
      function() return UI.DB().theme.spark end,
      function(v)
        UI.DB().theme.spark = v and true or false
        if UI._toolbar then
          if v and UI.Gloss and UI.Gloss.Spark then
            UI.Gloss.Spark(UI._toolbar, { color = { 1.0, 0.85, 0.40 } })
          elseif UI.Gloss and UI.Gloss.RemoveSpark then
            UI.Gloss.RemoveSpark(UI._toolbar)
          end
        end
      end, "Bright comet that glides along the open-addon bar")
    sparkChk:SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 32)

    UI.Header(f.body, "Toolbar tools"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)

    local scroll = UI.Scroll(f.body, 340, 160, "GmUISettingsToolScroll")
    scroll:SetPoint("TOPLEFT", 0, y)
    local cy = 0
    local tools = {}
    for _, t in pairs(UI._tools) do
      if UI.ToolDepsOk(t) then tools[#tools + 1] = t end
    end
    table.sort(tools, function(a, b) return a.order < b.order end)
    for i = 1, #tools do
      local t = tools[i]
      local c = UI.Check(scroll.child, t.label .. "  (" .. t.id .. ")",
        function()
          local h = UI.DB().tools[t.id]
          return not (h and h.hidden)
        end,
        function(v)
          UI.DB().tools[t.id] = UI.DB().tools[t.id] or {}
          UI.DB().tools[t.id].hidden = not v
          UI.Toolbar.Rebuild()
        end)
      c:SetPoint("TOPLEFT", 0, -cy)
      cy = cy + 24
    end
    if scroll.SetChildHeight then
      scroll:SetChildHeight(math.max(160, cy + 4))
    else
      scroll.child:SetHeight(math.max(160, cy + 4))
    end
    y = y - 170

    UI.Label(f.body, "Right-click any window title to dock, snap, or set opacity.",
      "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)

    UI._settings = f
  end
  UI._settings:Show()
end

function UI.ToggleMapTpPanel()
  if UI._maptp and UI._maptp:IsShown() then UI._maptp:Hide() return end
  if not UI._maptp then
    local f = UI.CreateWindow({
      id = "maptp", title = "Map Teleport", color = "maptp", width = 340, height = 280,
    })
    local y = -4
    UI.Muted(f.body, "Click the world map to teleport when armed.")
      :SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)
    local status = UI.Label(f.body, "Status: idle", "GameFontHighlightSmall", "textGold")
    status:SetPoint("TOPLEFT", 0, y)
    f._status = status
    y = UI.Row(f.body, y, 26)
    if UI.Check then
      UI.Check(f.body, "Map click teleport armed", function()
        return GmMapTeleport and GmMapTeleport.IsEnabled and GmMapTeleport.IsEnabled()
      end, function(on)
        if SlashCmdList["GMMAPTELEPORT"] then
          SlashCmdList["GMMAPTELEPORT"](on and "on" or "off")
        end
        if f._status then
          f._status:SetText(on and "Status: ARMED — click the map" or "Status: idle")
        end
      end):SetPoint("TOPLEFT", 0, y)
      y = UI.Row(f.body, y, 30)
    end
    UI.Button(f.body, "Enable", 90, 26, function()
      if SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("on") end
      if f._status then f._status:SetText("Status: ARMED — click the map") end
    end, nil, "ok"):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Disable", 90, 26, function()
      if SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("off") end
      if f._status then f._status:SetText("Status: idle") end
    end, nil, "warn"):SetPoint("TOPLEFT", 98, y)
    UI.Button(f.body, "Menu", 90, 26, function()
      if GmMapTeleport and GmMapTeleport.OpenMenu then GmMapTeleport.OpenMenu()
      elseif SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("menu") end
    end, nil, "maptp"):SetPoint("TOPLEFT", 196, y)
    y = UI.Row(f.body, y, 34)
    UI.Button(f.body, "Again", 90, 26, function()
      if SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("again") end
    end):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Resync", 90, 26, function()
      if SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("resync") end
    end):SetPoint("TOPLEFT", 98, y)
    UI.Button(f.body, "Unlock", 90, 26, function()
      if SlashCmdList["GMMAPTELEPORT"] then SlashCmdList["GMMAPTELEPORT"]("unlock") end
    end):SetPoint("TOPLEFT", 196, y)
    y = UI.Row(f.body, y, 34)
    UI.Muted(f.body, "/maptp status | on | off | menu | again")
      :SetPoint("TOPLEFT", 0, y)
    UI._maptp = f
  end
  UI._maptp:Show()
end

function UI.ToggleWsgPanel()
  if UI._wsg and UI._wsg:IsShown() then UI._wsg:Hide() return end
  if not UI._wsg then
    local f = UI.CreateWindow({
      id = "wsg", title = "CTF Cap", color = "wsg", width = 320, height = 320,
    })
    local y = -4
    UI.Label(f.body, "Flag grab → teleport home (WSG / Twin Peaks / EotS)",
      "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)
    local status = UI.Label(f.body, "—", "GameFontHighlightSmall", "text")
    status:SetPoint("TOPLEFT", 0, y)
    f._status = status
    y = UI.Row(f.body, y, 26)

    local function ctf()
      return _G.CtfCap or _G.WsgCap
    end
    local function startProf(id)
      local M = ctf()
      if M and M.Start then M.Start(id) end
    end

    local profileItems = {
      { id = "wsg", label = "Warsong Gulch" },
      { id = "twinpeaks", label = "Twin Peaks" },
      { id = "eots", label = "Eye of the Storm" },
    }
    local function getProf()
      local M = ctf()
      if M and M.GetState then
        local _, _, _, _, _, pid = M.GetState()
        return pid or "wsg"
      end
      if CtfCapDB and CtfCapDB.profileId then return CtfCapDB.profileId end
      return "wsg"
    end
    local function setProf(id)
      CtfCapDB = CtfCapDB or {}
      CtfCapDB.profileId = id
      local M = ctf()
      if M and M.UseProfile then M.UseProfile(id) end
      if GmSessionDB then GmSessionDB.lastCtfProfile = id end
    end

    if UI.Dropdown then
      UI.Label(f.body, "Profile", "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)
      y = UI.Row(f.body, y, 16)
      local dd = UI.Dropdown(f.body, profileItems, getProf, setProf, "BG profile for queue + pins")
      dd:SetWidth(280)
      dd:SetPoint("TOPLEFT", 0, y)
      f._profileDd = dd
      y = UI.Row(f.body, y, 30)
    else
      UI.Button(f.body, "WSG", 90, 24, function() startProf("wsg") end,
        "Warsong Gulch", "wsg"):SetPoint("TOPLEFT", 0, y)
      UI.Button(f.body, "Twin Peaks", 100, 24, function() startProf("twinpeaks") end,
        "Twin Peaks CTF", "wsg"):SetPoint("TOPLEFT", 98, y)
      UI.Button(f.body, "EotS", 90, 24, function() startProf("eots") end,
        "Eye of the Storm", "wsg"):SetPoint("TOPLEFT", 206, y)
      y = UI.Row(f.body, y, 32)
    end

    UI.Button(f.body, "Start", 80, 24, function()
      local M = ctf()
      if M and M.Start then M.Start(getProf()) end
    end, "Start current profile", "wsg"):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Stop", 80, 24, function()
      local M = ctf()
      if M and M.Stop then M.Stop() end
    end, "Stop", "danger"):SetPoint("TOPLEFT", 88, y)
    UI.Button(f.body, "Queue", 80, 24, function()
      if SlashCmdList["CTFCAP"] then SlashCmdList["CTFCAP"]("queue")
      elseif SlashCmdList["WSGCAP"] then SlashCmdList["WSGCAP"]("queue") end
    end, "Queue current BG"):SetPoint("TOPLEFT", 176, y)
    y = UI.Row(f.body, y, 32)
    UI.Button(f.body, "Enemy / flag", 110, 24, function()
      local M = ctf()
      if M and M.GoEnemy then M.GoEnemy() end
    end):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Home base", 100, 24, function()
      local M = ctf()
      if M and M.GoHome then M.GoHome() end
    end):SetPoint("TOPLEFT", 118, y)
    y = UI.Row(f.body, y, 32)
    UI.Label(f.body, "/ctf start wsg|twinpeaks|eots · /wsg · /tpk · /eots",
      "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)

    if UI.OnShownUpdate then
      UI.OnShownUpdate(f, 1.0, function(self)
        local M = ctf()
        if M and M.GetState then
          local st, line, run, caps, fac, pid, title = M.GetState()
          self._status:SetText(string.format("%s | %s | %s | run=%s caps=%s %s",
            tostring(title or pid or "?"), tostring(st), tostring(line),
            tostring(run), tostring(caps), tostring(fac or "")))
        end
        if self._profileDd and self._profileDd.Refresh then
          self._profileDd:Refresh()
        end
      end)
    end
    UI._wsg = f
  end
  UI._wsg:Show()
end

function UI.ToggleBgAfkPanel()
  if UI._bgafk and UI._bgafk:IsShown() then UI._bgafk:Hide() return end
  if not UI._bgafk then
    local f = UI.CreateWindow({
      id = "bgafk", title = "BG AFK", color = "bgafk", width = 320, height = 340,
    })
    local y = -4
    UI.Label(f.body, "Roles → solo/group queue → corner-sky → leave → requeue",
      "GameFontHighlightSmall", "textDim"):SetPoint("TOPLEFT", 0, y)
    y = UI.Row(f.body, y, 22)
    local status = UI.Label(f.body, "—", "GameFontHighlightSmall", "text")
    status:SetPoint("TOPLEFT", 0, y)
    f._status = status
    y = UI.Row(f.body, y, 26)
    local stats = UI.Label(f.body, "—", "GameFontHighlightSmall", "textGold")
    stats:SetPoint("TOPLEFT", 0, y)
    f._stats = stats
    y = UI.Row(f.body, y, 24)

    local function db()
      BgAfkDB = BgAfkDB or {}
      if BgAfk and BgAfk.F and BgAfk.F.defaults then return BgAfk.F.defaults() end
      return BgAfkDB
    end

    if UI.Check then
      UI.Check(f.body, "Auto queue",
        function() return db().autoQueue ~= false end,
        function(v) db().autoQueue = v end):SetPoint("TOPLEFT", 0, y)
      UI.Check(f.body, "Auto accept",
        function() return db().autoAccept ~= false end,
        function(v) db().autoAccept = v end):SetPoint("TOPLEFT", 150, y)
      y = UI.Row(f.body, y, 26)
      UI.Check(f.body, "Auto leave on finish",
        function() return db().autoLeave ~= false end,
        function(v) db().autoLeave = v end):SetPoint("TOPLEFT", 0, y)
      y = UI.Row(f.body, y, 26)
    end

    if UI.Dropdown then
      UI.Label(f.body, "Sky height (airZ)", "GameFontHighlightSmall", "textDim")
        :SetPoint("TOPLEFT", 0, y)
      y = UI.Row(f.body, y, 16)
      local dd = UI.Dropdown(f.body, {
        { id = 400, label = "400 yd" },
        { id = 600, label = "600 yd" },
        { id = 800, label = "800 yd (default)" },
        { id = 1200, label = "1200 yd" },
      }, function() return tonumber(db().airZ) or 800 end,
        function(v) db().airZ = tonumber(v) or 800 end,
        "Corner-sky park altitude")
      dd:SetWidth(160)
      dd:SetPoint("TOPLEFT", 0, y)
      y = UI.Row(f.body, y, 30)
    end

    y = UI.Row(f.body, y, 4)

    UI.Button(f.body, "Start", 80, 24, function()
      if BgAfk and BgAfk.Start then BgAfk.Start() end
    end, "Start AFK farm", "bgafk"):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Stop", 80, 24, function()
      if BgAfk and BgAfk.Stop then BgAfk.Stop() end
    end, "Stop", "danger"):SetPoint("TOPLEFT", 88, y)
    UI.Button(f.body, "Queue", 80, 24, function()
      if BgAfk and BgAfk.Queue then BgAfk.Queue() end
    end, "Queue random BG"):SetPoint("TOPLEFT", 176, y)
    y = UI.Row(f.body, y, 32)
    UI.Button(f.body, "Sky TP", 80, 24, function()
      if BgAfk and BgAfk.Sky then BgAfk.Sky() end
    end):SetPoint("TOPLEFT", 0, y)
    UI.Button(f.body, "Leave", 80, 24, function()
      if BgAfk and BgAfk.Leave then BgAfk.Leave() end
    end):SetPoint("TOPLEFT", 88, y)
    UI.Button(f.body, "Stats", 80, 24, function()
      if BgAfk and BgAfk.Stats then BgAfk.Stats() end
    end):SetPoint("TOPLEFT", 176, y)
    y = UI.Row(f.body, y, 32)
    UI.Label(f.body, "/bgafk start|stop|stats|roles|air 800", "GameFontHighlightSmall", "textDim")
      :SetPoint("TOPLEFT", 0, y)

    if UI.OnShownUpdate then
      UI.OnShownUpdate(f, 1.0, function(self)
        if BgAfk and BgAfk.GetState then
          local st, line, run, bgs, xp, xph = BgAfk.GetState()
          self._status:SetText(string.format("%s | %s | run=%s",
            tostring(st), tostring(line), tostring(run)))
          self._stats:SetText(string.format("BGs=%s  XP=+%s  XP/hr=%.0f",
            tostring(bgs or 0), tostring(xp or 0), tonumber(xph) or 0))
        end
      end)
    end
    UI._bgafk = f
  end
  UI._bgafk:Show()
end

function UI.ToggleKxPanel()
  if KnightOfXoroth_ToggleUI then
    KnightOfXoroth_ToggleUI()
    return
  end
  if KX and KX.ToggleUI then
    KX:ToggleUI()
    return
  end
  if UI.Chat then
    UI.Chat("|cffff4444KnightOfXoroth not loaded|r — enable it in AddOns, then /reload")
  end
end

SLASH_GMUI1 = "/gmui"
SLASH_GMUI2 = "/gmtk"
SLASH_GMUI3 = "/gmtoolkit"
SlashCmdList["GMUI"] = function(msg)
  msg = string.lower(tostring(msg or ""):match("^%s*(.-)%s*$") or "")
  if msg == "" or msg == "bar" or msg == "toolbar" then
    UI.Toolbar.Toggle()
  elseif msg == "settings" or msg == "config" then
    UI.ToggleSettings()
  elseif msg:match("^scale") then
    local n = tonumber(msg:match("scale%s+([%d.]+)"))
    if n then
      if n > 1.5 then n = n / 100 end
      UI.SetCompactScale(n)
    else
      UI.Chat(string.format("compact scale %.0f%%  —  /gmui scale 0.60", (UI.CompactScale() or 0.6) * 100))
    end
  elseif msg == "dock" then
    UI.ShowDock()
  elseif msg == "adopt" then
    UI.AdoptAll()
    UI.Chat("adopted open frames")
  elseif msg == "show" then
    UI.Toolbar.Show()
  elseif msg == "hide" then
    UI.Toolbar.Hide()
  else
    UI.Chat("usage: /gmui [toolbar|settings|scale 0.60|dock|adopt|show|hide]")
  end
end

-- Addon-level events + deferred boot adopt live on the single Scheduler.
UI.Scheduler.On("ADDON_LOADED", function(_, name)
  if name == "GmUI" then
    UI.DB()
    UI.InstallTaintMute()
    UI.RegisterDefaults()
  else
    UI.AdoptAll()
    if UI.Toolbar and UI._toolbar then UI.Toolbar.Rebuild() end
  end
end)

UI.Scheduler.On("PLAYER_LOGIN", function()
  UI.DB()
  UI.InstallTaintMute()
  UI.RegisterDefaults()
  UI.Toolbar.Create()
  UI.AdoptAll()
  if UI.ApplyAllScales then UI.ApplyAllScales() end
  UI.Scheduler.After(1.5, function()
    UI.AdoptAll()
    UI.RestoreDocks()
    UI.Toolbar.Rebuild()
  end)
  UI.Chat("v" .. UI.VERSION .. " — toolbar ready  /gmui settings")
end)

_G.GmToolkit = UI
