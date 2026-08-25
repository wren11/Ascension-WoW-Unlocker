-- Cross-instance ExtProxy proxy: getInstance("Name").GmLoS(...) runs on that client.
-- Requires ExtProxy InstBus (GmPublishName / GmRemoteCall).

GmShared = GmShared or {}

local function chat(msg)
  if DEFAULT_CHAT_FRAME then
    DEFAULT_CHAT_FRAME:AddMessage("|cff00b4d8[GmShared]|r " .. tostring(msg))
  end
end

local function publishName()
  if type(GmPublishName) ~= "function" then return end
  local n = UnitName and UnitName("player")
  if n and n ~= "" then
    pcall(GmPublishName, n)
  end
end

--- Resolve canonical ExtProxy global name from method key (gmLos / GmLos / GmLoS).
local function resolveFnName(key)
  key = tostring(key or "")
  if key == "" then return nil end
  if type(_G[key]) == "function" then return key end
  -- camel gmX -> GmX
  if key:sub(1, 2) == "gm" and #key > 2 then
    local cand = "Gm" .. key:sub(3, 3):upper() .. key:sub(4)
    if type(_G[cand]) == "function" then return cand end
    if type(GmRemoteCall) == "function" then return cand end -- peer may have it
  end
  if key:sub(1, 2) == "Gm" then
    return key
  end
  -- bare Los -> GmLoS unlikely; try Gm..key
  local cand2 = "Gm" .. key
  if type(_G[cand2]) == "function" then return cand2 end
  return key
end

local proxyMeta = {}
proxyMeta.__index = function(t, k)
  if k == "id" or k == "name" or k == "pid" or k == "_id" or k == "_name" or k == "_pid" then
    return rawget(t, k == "id" and "_id" or (k == "name" and "_name" or (k == "pid" and "_pid" or k)))
  end
  if k == "call" then
    return function(_, fn, ...)
      return GmShared.RemoteCall(t._id, fn, ...)
    end
  end
  local fn = resolveFnName(k)
  if not fn then return nil end
  return function(first, ...)
    -- Support both leader.GmLoS(...) and leader:GmLoS(...)
    if first == t then
      return GmShared.RemoteCall(t._id, fn, ...)
    end
    if first == nil and select("#", ...) == 0 then
      return GmShared.RemoteCall(t._id, fn)
    end
    return GmShared.RemoteCall(t._id, fn, first, ...)
  end
end

function GmShared.RemoteCall(target, fnName, ...)
  if type(GmRemoteCall) ~= "function" then
    return nil, "GmRemoteCall missing — launch latest ExtProxy"
  end
  local n = select("#", ...)
  local me = GmShared.ThisInstance()
  local tid = tonumber(target)
  -- Local short-circuit: same instance → call global directly (no RPC round-trip).
  if tid and me and tid == (me.id or 0) and type(_G[fnName]) == "function" then
    return _G[fnName](...)
  end
  return GmRemoteCall(target, fnName, n, ...)
end

--- getInstance("PlayerName") or getInstance(2) → proxy table.
function GmShared.GetInstanceProxy(nameOrId)
  if type(GmResolveInstance) ~= "function" then
    return nil
  end
  local id, pid, nm = GmResolveInstance(tostring(nameOrId or ""))
  id = tonumber(id)
  if not id or id < 1 then
    return nil
  end
  local p = {
    _id = id,
    _pid = tonumber(pid) or 0,
    _name = nm or tostring(nameOrId or ""),
    id = id,
    pid = tonumber(pid) or 0,
    name = nm or tostring(nameOrId or ""),
  }
  return setmetatable(p, proxyMeta)
end

-- Global alias matching user API.
function getInstance(nameOrId)
  return GmShared.GetInstanceProxy(nameOrId)
end

function GmShared.ListInstances()
  local out = {}
  if type(GmListInstances) ~= "function" then return out end
  local n = tonumber(GmListInstances()) or 0
  for i = 1, n do
    local id, pid, nm = GmListInstances(i)
    if id then
      out[#out + 1] = { id = tonumber(id), pid = tonumber(pid), name = tostring(nm or "") }
    end
  end
  return out
end

-- Keep name directory fresh.
local pub = CreateFrame("Frame")
local pubAcc = 0
pub:RegisterEvent("PLAYER_LOGIN")
pub:RegisterEvent("PLAYER_ENTERING_WORLD")
pub:RegisterEvent("PLAYER_LOGOUT")
pub:SetScript("OnEvent", function(_, ev)
  if ev == "PLAYER_LOGOUT" then return end
  publishName()
end)
pub:SetScript("OnUpdate", function(_, dt)
  pubAcc = pubAcc + (dt or 0)
  if pubAcc < 2.0 then return end
  pubAcc = 0
  publishName()
end)

SLASH_GMINSTANCE1 = "/gminstance"
SlashCmdList.GMINSTANCE = function(msg)
  msg = tostring(msg or ""):gsub("^%s+", ""):gsub("%s+$", "")
  publishName()
  if msg == "" or msg == "list" then
    local list = GmShared.ListInstances()
    chat(string.format("directory=%d  (publish=%s)", #list, tostring(UnitName("player"))))
    for i = 1, #list do
      local e = list[i]
      chat(string.format("  [%d] %s  pid=%s", e.id, e.name, tostring(e.pid)))
    end
    return
  end
  local p = getInstance(msg)
  if not p then
    chat("not found: " .. msg)
    return
  end
  chat(string.format("proxy id=%d name=%s pid=%d — e.g. :GmPlayerXYZ()", p.id, p.name, p.pid))
end
