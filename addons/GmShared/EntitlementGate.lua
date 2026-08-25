-- Free / offline: every addon is allowed. No store, no Core, no Discord login.

local M = _G.GmtEntitlementGate or {}
_G.GmtEntitlementGate = M

function M.HasCore()
  return true
end

function M.NameLinked()
  return true
end

function M.IsPlatform(_name)
  return true
end

function M.HasAddon(_name)
  return true
end

function M.RequireAddon(_name, _printMsg)
  return true
end

function M.RequireCore(_printMsg)
  return true
end

return M
