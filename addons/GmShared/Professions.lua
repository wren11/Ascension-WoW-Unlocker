--[[
  GmProfessions — live spellbook + skill-line snapshot for gather/loot bots.

  3.3.5a: GetNumSkillLines / GetSkillLineInfo, plus spellbook name scan.
  Rank 0 means the profession is not learned. Bots must not teleport to
  nodes whose required skill is above the player's rank.
]]

GmProfessions = GmProfessions or {}
local P = GmProfessions
P.VERSION = "1.0.1"

local cache = { at = 0, mining = 0, herb = 0, skinning = 0, lockpicking = 0 }

local SKILL_BY_NAME = {
  -- Mining
  ["copper vein"] = 1, ["tin vein"] = 65, ["silver vein"] = 75,
  ["iron deposit"] = 125, ["gold vein"] = 155, ["mithril deposit"] = 175,
  ["truesilver deposit"] = 230, ["dark iron deposit"] = 230,
  ["small thorium vein"] = 230, ["thorium vein"] = 245,
  ["rich thorium vein"] = 255, ["fel iron deposit"] = 275,
  ["adamantite deposit"] = 325, ["rich adamantite deposit"] = 350,
  ["khorium vein"] = 375, ["cobalt deposit"] = 350,
  ["rich cobalt deposit"] = 375, ["saronite deposit"] = 400,
  ["rich saronite deposit"] = 425, ["titanium vein"] = 450,
  ["incendicite"] = 65, ["indurium"] = 150, ["nethercite"] = 275,
  -- Herbs
  ["peacebloom"] = 1, ["silverleaf"] = 1, ["bloodthistle"] = 1, ["earthroot"] = 15,
  ["mageroyal"] = 50, ["briarthorn"] = 70, ["stranglekelp"] = 85, ["bruiseweed"] = 85,
  ["wild steelbloom"] = 115, ["grave moss"] = 120, ["kingsblood"] = 125,
  ["liferoot"] = 150, ["fadeleaf"] = 160, ["goldthorn"] = 170,
  ["khadgar"] = 185, ["wintersbite"] = 195, ["firebloom"] = 205,
  ["purple lotus"] = 210, ["arthas"] = 220, ["sungrass"] = 230,
  ["blindweed"] = 235, ["ghost mushroom"] = 245, ["gromsblood"] = 250,
  ["golden sansam"] = 260, ["dreamfoil"] = 270, ["mountain silversage"] = 280,
  ["plaguebloom"] = 285, ["sorrowmoss"] = 285, ["icecap"] = 290, ["black lotus"] = 300,
  ["felweed"] = 300, ["dreaming glory"] = 315, ["ragveil"] = 325, ["terocone"] = 325,
  ["ancient lichen"] = 340, ["netherbloom"] = 350, ["nightmare vine"] = 365,
  ["mana thistle"] = 375, ["goldclover"] = 350, ["tiger lily"] = 375,
  ["talandra"] = 385, ["adder's tongue"] = 400, ["adders tongue"] = 400,
  ["lichbloom"] = 425, ["icethorn"] = 435, ["frost lotus"] = 450,
  ["netherdust"] = 1, ["flame cap"] = 335,
}

local function low(s)
  return string.lower(tostring(s or ""))
end

local function skillLineCount()
  if GetNumSkillLines then return GetNumSkillLines() or 0 end
  if GetNumSkillLines then return GetNumSkillLines() or 0 end
  return 0
end

local function skillLineInfo(i)
  if GetSkillLineInfo then return GetSkillLineInfo(i) end
  if GetSkillLineInfo then return GetSkillLineInfo(i) end
end

local function classifySkillName(name)
  local n = low(name)
  if n == "" then return nil end
  if n:find("mining", 1, true) then return "mining" end
  if n:find("herbalism", 1, true) or n:find("herb gathering", 1, true)
      or n == "herbalism" then
    return "herb"
  end
  if n:find("skinning", 1, true) then return "skinning" end
  if n:find("lockpicking", 1, true) or n:find("lock picking", 1, true) then
    return "lockpicking"
  end
  return nil
end

local function scanSkillLines(out)
  local n = skillLineCount()
  for i = 1, n do
    local name, header, _, rank = skillLineInfo(i)
    if name and not header then
      local kind = classifySkillName(name)
      if kind then
        out[kind] = math.max(out[kind] or 0, tonumber(rank) or 0)
      end
    end
  end
end

local function scanSpellbook(out)
  local book = BOOKTYPE_SPELL or "spell"
  local tabs = GetNumSpellTabs and GetNumSpellTabs() or 1
  for t = 1, tabs do
    local _, _, offset, numSpells = GetSpellTabInfo(t)
    offset = tonumber(offset) or 0
    numSpells = tonumber(numSpells) or 0
    for i = offset + 1, offset + numSpells do
      local name
      if GetSpellName then
        name = GetSpellName(i, book)
      elseif GetSpellBookItemName then
        name = GetSpellBookItemName(i, book)
      end
      if name then
        local n = low(name)
        if n:find("find minerals", 1, true) or n == "mining" then
          if (out.mining or 0) < 1 then out.mining = 1 end
        elseif n:find("find herbs", 1, true) or n:find("herb gathering", 1, true)
            or n == "herbalism" then
          if (out.herb or 0) < 1 then out.herb = 1 end
        elseif n == "skinning" or n:find("skinning", 1, true) then
          if (out.skinning or 0) < 1 then out.skinning = 1 end
        elseif n == "pick lock" or n:find("lockpicking", 1, true) then
          if (out.lockpicking or 0) < 1 then out.lockpicking = 1 end
        end
      end
    end
  end
end

function P.Invalidate()
  cache.at = 0
end

function P.Refresh(force)
  local t = GetTime and GetTime() or 0
  if not force and cache and (t - (cache.at or 0)) < 2 then
    return cache
  end
  local out = { at = t, mining = 0, herb = 0, skinning = 0, lockpicking = 0 }
  scanSkillLines(out)
  scanSpellbook(out)
  cache = out
  return cache
end

function P.Rank(kind)
  local s = P.Refresh()
  if kind == "mining" then return s.mining or 0 end
  if kind == "herb" or kind == "herbalism" then return s.herb or 0 end
  if kind == "skinning" then return s.skinning or 0 end
  if kind == "lockpicking" or kind == "lockpick" then return s.lockpicking or 0 end
  return 0
end

function P.Has(kind)
  return P.Rank(kind) > 0
end

function P.Can(kind, req)
  req = tonumber(req) or 1
  if req < 1 then req = 1 end
  return P.Rank(kind) >= req
end

function P.SkillForName(name, kind)
  local n = low(name)
  if n == "" then return (kind and 1) or nil end
  local best, bestLen
  for key, skill in pairs(SKILL_BY_NAME) do
    if n:find(key, 1, true) then
      local len = #key
      if not bestLen or len > bestLen then
        best, bestLen = skill, len
      end
    end
  end
  if best then return best end
  if kind then return 1 end
  return nil
end

function P.GuessKind(name)
  local n = low(name)
  if n == "" then return nil end
  if n:find("fishing", 1, true) or n:find("fish school", 1, true)
      or n:find("fishing device", 1, true) or n:find("fishing hole", 1, true) then
    return "fishing", 1
  end
  local reject = {
    "chest", "coffer", "trunk", "crate", "barrel", "cache", "lockbox",
    "mailbox", "door", "gate", "wreckage", "debris", "treasure",
  }
  for i = 1, #reject do
    if n:find(reject[i], 1, true) then return nil end
  end
  local skill = P.SkillForName(name, nil)
  if n:find("vein", 1, true) or n:find("deposit", 1, true)
      or n:find("ore", 1, true) or n:find("mineral", 1, true) then
    return "mining", skill or 1
  end
  local herbs = {
    "peacebloom", "silverleaf", "earthroot", "mageroyal", "briarthorn",
    "bruiseweed", "steelbloom", "kingsblood", "liferoot", "fadeleaf",
    "goldthorn", "khadgar", "wintersbite", "firebloom", "lotus",
    "sungrass", "blindweed", "mushroom", "gromsblood", "sansam",
    "dreamfoil", "silversage", "plaguebloom", "sorrowmoss", "icecap",
    "felweed", "ragveil", "terocone", "lichen", "netherbloom",
    "goldclover", "tiger lily", "talandra", "tongue", "lichbloom",
    "icethorn", "stranglekelp", "grave moss",
  }
  for i = 1, #herbs do
    if n:find(herbs[i], 1, true) then
      return "herb", skill or 1
    end
  end
  return nil
end

function P.Summary()
  local s = P.Refresh()
  return string.format("mining=%d herbalism=%d skinning=%d",
    s.mining or 0, s.herb or 0, s.skinning or 0)
end
