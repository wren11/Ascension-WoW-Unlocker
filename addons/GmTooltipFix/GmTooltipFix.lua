--[[
  GmTooltipFix — Ascension FrameXML safety shim.

  Ascension Interface\FrameXML\GameTooltipMods.lua:~109 does:
      lineText = left:GetText()
      lineText:find(...)   -- crashes when GetText() is nil
  Common on custom spells (SetSpell / SetAction / spellbook hover).

  We cannot edit the MPQ FrameXML. This addon:
    1) Makes tooltip-line GetText() return "" instead of nil (metatable + per-line)
    2) pcall-wraps GameTooltip Set* so a bad hook cannot brick the call
    3) Filters the known Ascension error through seterrorhandler (last resort)
]]

local ADDON = "GmTooltipFix"
local VERSION = "1.1.2"
local MAX_LINES = 80

local function isTooltipLineName(name)
  if type(name) ~= "string" then return false end
  return name:find("TextLeft", 1, true) or name:find("TextRight", 1, true)
      or name:find("Tooltip", 1, true)
end

local function parentLooksLikeTooltip(fs)
  local p = fs and fs.GetParent and fs:GetParent()
  while p do
    local n = p.GetName and p:GetName()
    if type(n) == "string" and n:find("Tooltip", 1, true) then return true end
    if p == GameTooltip or p == ItemRefTooltip then return true end
    p = p.GetParent and p:GetParent()
  end
  return false
end

local function shouldCoerceNil(fs)
  if not fs then return false end
  -- Region without GetName still coerce if parent chain looks like a tooltip.
  local okName, n = pcall(function()
    return fs.GetName and fs:GetName()
  end)
  if okName and isTooltipLineName(n) then return true end
  local okPar, looks = pcall(parentLooksLikeTooltip, fs)
  return okPar and looks
end

--- Safe GetText call: never throws; never returns nil when coerce=true.
local function safeGetText(orig, self, coerce, ...)
  if type(orig) ~= "function" then
    if coerce then return "" end
    return nil
  end
  local ok, t = pcall(orig, self, ...)
  if not ok then
    if coerce then return "" end
    return nil
  end
  if t == nil and coerce then return "" end
  return t
end

local function patchFontString(fs)
  if not fs or fs.__gmTooltipFixGetText then return end
  local orig = fs.GetText
  if type(orig) ~= "function" then return end
  fs.__gmTooltipFixGetText = true
  fs.GetText = function(self)
    -- Per-line patches are always on tooltip lines → always coerce nil/errors.
    return safeGetText(orig, self, true)
  end
end

local function patchTooltip(tt)
  if not tt then return end
  local name = tt.GetName and tt:GetName()
  if not name then return end
  for i = 1, MAX_LINES do
    patchFontString(_G[name .. "TextLeft" .. i])
    patchFontString(_G[name .. "TextRight" .. i])
  end
  -- Also patch any FontStrings already hanging off the tooltip that lack
  -- the standard TextLeftN name (Ascension custom lines / embeds).
  if tt.GetRegions then
    local ok, regions = pcall(function() return { tt:GetRegions() } end)
    if ok and regions then
      for i = 1, #regions do
        local r = regions[i]
        if r and r.GetObjectType and r:GetObjectType() == "FontString" then
          patchFontString(r)
        end
      end
    end
  end
end

local metaPatched = false
local function patchFontStringMeta()
  if metaPatched then return true end
  local sample = _G.GameTooltipTextLeft1 or _G.GameTooltipTextLeft2
      or _G.ItemRefTooltipTextLeft1
  if not sample then return false end
  local mt = getmetatable(sample)
  if not mt or type(mt.__index) ~= "table" then return false end
  local idx = mt.__index
  if type(idx.GetText) ~= "function" then return false end
  if idx.__gmTooltipFixMeta then
    metaPatched = true
    return true
  end
  local orig = idx.GetText
  idx.__gmTooltipFixMeta = true
  idx.GetText = function(self, ...)
    local coerce = shouldCoerceNil(self)
    return safeGetText(orig, self, coerce, ...)
  end
  metaPatched = true
  return true
end

local function patchKnownTooltips()
  patchFontStringMeta()
  patchTooltip(GameTooltip)
  patchTooltip(ItemRefTooltip)
  patchTooltip(_G.ShoppingTooltip1)
  patchTooltip(_G.ShoppingTooltip2)
  patchTooltip(_G.ShoppingTooltip3)
  patchTooltip(_G.WorldMapTooltip)
  patchTooltip(_G.AtlasLootTooltip)
  patchTooltip(_G.EnchantTooltip)
end

local SET_METHODS = {
  "SetAction", "SetSpell", "SetSpellByID", "SetHyperlink",
  "SetUnit", "SetUnitBuff", "SetUnitDebuff", "SetUnitAura",
  "SetPetAction", "SetShapeshift", "SetTalent",
  "SetAuctionItem", "SetBagItem", "SetInventoryItem", "SetLootItem",
  "SetMerchantItem", "SetQuestItem", "SetQuestLogItem",
  "SetTradeSkillItem", "SetCraftItem", "SetTrainerService",
  "SetInboxItem", "SetSendMailItem", "SetTradePlayerItem", "SetTradeTargetItem",
  "SetSocketGem", "SetExistingSocketGem", "SetGlyph",
  "SetLFDCompareTooltip", "SetCurrencyToken", "SetBackpackToken",
}

local function wrapSetMethod(tt, method)
  if not tt then return end
  local orig = tt[method]
  if type(orig) ~= "function" or tt["__gmTooltipFix_" .. method] then return end
  tt["__gmTooltipFix_" .. method] = true
  tt[method] = function(self, ...)
    patchTooltip(self)
    patchFontStringMeta()
    local ok, a, b, c, d = pcall(orig, self, ...)
    if not ok then
      -- One retry after forcing line patches (Ascension may create lines mid-call).
      patchTooltip(self)
      patchFontStringMeta()
      ok, a, b, c, d = pcall(orig, self, ...)
      if not ok then return end
    end
    patchTooltip(self)
    return a, b, c, d
  end
end

local function wrapAll(tt)
  if not tt or tt.__gmTooltipFixWrapped then return end
  tt.__gmTooltipFixWrapped = true
  for i = 1, #SET_METHODS do
    wrapSetMethod(tt, SET_METHODS[i])
  end
end

-- Swallow only the known Ascension nil-lineText crash so the red error frame stays quiet.
local function installErrorFilter()
  if geterrorhandler and seterrorhandler and not _G.__gmTooltipFixErr then
    _G.__gmTooltipFixErr = true
    local prev = geterrorhandler()
    seterrorhandler(function(msg)
      if type(msg) == "string"
          and string.find(msg, "GameTooltipMods", 1, true)
          and (string.find(msg, "lineText", 1, true)
            or string.find(msg, "a nil value", 1, true)
            or string.find(msg, "GetText", 1, true)
            or string.find(msg, "attempt to index", 1, true)) then
        patchKnownTooltips()
        return
      end
      if type(prev) == "function" then return prev(msg) end
    end)
  end
end

local function hookTooltipScripts(tt)
  if not tt or tt.__gmTooltipFixOnShow then return end
  tt.__gmTooltipFixOnShow = true
  if tt.HookScript then
    pcall(function()
      tt:HookScript("OnShow", function(self) patchTooltip(self) end)
    end)
    pcall(function()
      tt:HookScript("OnTooltipSetSpell", function(self) patchTooltip(self) end)
    end)
    pcall(function()
      tt:HookScript("OnTooltipSetItem", function(self) patchTooltip(self) end)
    end)
    pcall(function()
      tt:HookScript("OnHide", function(self) patchTooltip(self) end)
    end)
  end
end

local boot = CreateFrame("Frame")
boot:RegisterEvent("ADDON_LOADED")
boot:RegisterEvent("PLAYER_LOGIN")
boot:RegisterEvent("PLAYER_ENTERING_WORLD")
boot:SetScript("OnEvent", function(_, event, name)
  if event == "ADDON_LOADED" and name ~= ADDON then return end
  patchKnownTooltips()
  wrapAll(GameTooltip)
  wrapAll(ItemRefTooltip)
  installErrorFilter()
  if event == "PLAYER_LOGIN" or event == "PLAYER_ENTERING_WORLD" then
    patchKnownTooltips()
    hookTooltipScripts(GameTooltip)
    hookTooltipScripts(ItemRefTooltip)
  end
end)

-- Keep patching new lines while any game tooltip is visible; retry meta until it sticks.
local pulse = CreateFrame("Frame")
local acc = 0
pulse:SetScript("OnUpdate", function(_, dt)
  acc = acc + (dt or 0)
  if acc < 0.20 then return end
  acc = 0
  if not metaPatched then patchFontStringMeta() end
  if GameTooltip and GameTooltip:IsShown() then
    patchTooltip(GameTooltip)
  end
  if ItemRefTooltip and ItemRefTooltip:IsShown() then
    patchTooltip(ItemRefTooltip)
  end
end)

patchKnownTooltips()
wrapAll(GameTooltip)
wrapAll(ItemRefTooltip)
installErrorFilter()

if DEFAULT_CHAT_FRAME then
  local once = CreateFrame("Frame")
  once:RegisterEvent("PLAYER_LOGIN")
  once:SetScript("OnEvent", function(self)
    DEFAULT_CHAT_FRAME:AddMessage(
      "|cff00b4d8[GmTooltipFix]|r v" .. VERSION .. " — Ascension tooltip nil-guard active")
    self:UnregisterAllEvents()
  end)
end
