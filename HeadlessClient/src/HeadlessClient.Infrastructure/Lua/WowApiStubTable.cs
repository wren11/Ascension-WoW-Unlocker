using MoonSharp.Interpreter;

namespace HeadlessClient.Infrastructure.Lua;

public static class WowApiStubTable
{
    public static IReadOnlyDictionary<string, object> BuildNames() =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["CreateFrame"] = true,
            ["GetTime"] = true,
            ["UnitName"] = true,
            ["UnitGUID"] = true,
            ["print"] = true,
            ["GetRealmName"] = true,
            ["date"] = true,
            ["time"] = true,
            ["pairs"] = true,
            ["ipairs"] = true,
            ["next"] = true,
            ["type"] = true,
            ["select"] = true,
            ["tostring"] = true,
            ["tonumber"] = true,
            ["pcall"] = true,
            ["xpcall"] = true,
            ["error"] = true,
            ["assert"] = true,
            ["unpack"] = true,
            ["table"] = true,
            ["string"] = true,
            ["math"] = true,
        };

    public static void Register(Script script)
    {
        ArgumentNullException.ThrowIfNull(script);

        script.Globals["GetTime"] = (Func<double>)(() => Environment.TickCount64 / 1000.0);
        script.Globals["UnitName"] = (Func<string, string>)(unit =>
            string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase) ? "Player" : (unit ?? string.Empty));
        script.Globals["UnitGUID"] = (Func<string, string>)(unit =>
            string.Equals(unit, "player", StringComparison.OrdinalIgnoreCase) ? "0x0000000000000001" : "0x0");
        script.Globals["GetRealmName"] = (Func<string>)(() => "Headless");
        script.Globals["print"] = DynValue.NewCallback((_, args) =>
        {
            var parts = new string[args.Count];
            for (var i = 0; i < args.Count; i++)
            {
                parts[i] = args[i].ToPrintString();
            }

            var line = string.Join("\t", parts);
            Console.WriteLine(line.Length == 0 ? "" : line);
            return DynValue.Nil;
        });
        script.Globals["date"] = (Func<string?, double?, string>)((format, time) =>
        {
            var dt = time.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds((long)time.Value).LocalDateTime
                : DateTime.Now;
            if (string.IsNullOrEmpty(format) || format == "*t")
            {
                return dt.ToString("yyyy-MM-dd HH:mm:ss");
            }

            return dt.ToString(format.Replace("%Y", "yyyy").Replace("%m", "MM").Replace("%d", "dd")
                .Replace("%H", "HH").Replace("%M", "mm").Replace("%S", "ss"));
        });
        script.Globals["time"] = (Func<double>)(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        script.DoString("""
local __frames = rawget(_G, "__HC_FRAMES") or {}
rawset(_G, "__HC_FRAMES", __frames)
-- Per-addon allowlists: only RegisterEvent for declared/needed events.
rawset(_G, "__HC_ADDON_EVENTS", rawget(_G, "__HC_ADDON_EVENTS") or {})
rawset(_G, "__HC_CURRENT_ADDON", rawget(_G, "__HC_CURRENT_ADDON") or "")

local function __hc_event_allowed(addon, event)
  if not event then return false end
  local ev = string.upper(tostring(event))
  -- Lifecycle events always OK (not packet-driven).
  if ev == "ADDON_LOADED" or ev == "PLAYER_LOGIN" or ev == "PLAYER_ENTERING_WORLD"
     or ev == "PLAYER_LOGOUT" or ev == "VARIABLES_LOADED" then
    return true
  end
  local map = rawget(_G, "__HC_ADDON_EVENTS")
  if type(map) ~= "table" then return true end
  local allow = map[addon or ""]
  if type(allow) ~= "table" then
    -- No TOC allowlist yet → permit (legacy addons); host syncs packet fan-out separately.
    return true
  end
  return allow[ev] == true or allow[event] == true
end

function CreateFrame(frameType, name, parent)
  local addon = rawget(_G, "__HC_CURRENT_ADDON") or ""
  local f = { __events = {}, __scripts = {}, __type = frameType or "Frame", __name = name, __parent = parent, __addon = addon }
  function f:RegisterEvent(event)
    if not event then return end
    if not __hc_event_allowed(self.__addon, event) then
      return
    end
    self.__events[event] = true
  end
  function f:UnregisterEvent(event)
    if event then self.__events[event] = nil end
  end
  function f:SetScript(scriptName, handler)
    if scriptName then self.__scripts[scriptName] = handler end
  end
  function f:GetScript(scriptName)
    return self.__scripts[scriptName]
  end
  function f:Show() end
  function f:Hide() end
  function f:SetPoint() end
  function f:SetSize() end
  function f:EnableMouse() end
  function f:RegisterForDrag() end
  function f:CreateTexture()
    local t = {}
    function t:SetTexture() end
    function t:SetPoint() end
    function t:SetSize() end
    function t:SetAllPoints() end
    function t:Show() end
    function t:Hide() end
    return t
  end
  function f:CreateFontString()
    local fs = {}
    function fs:SetText() end
    function fs:SetPoint() end
    function fs:SetTextColor() end
    function fs:Show() end
    function fs:Hide() end
    return fs
  end
  table.insert(__frames, f)
  if name and name ~= "" then
    rawset(_G, name, f)
  end
  return f
end

function __HC_FireEvent(event, ...)
  if not event then return end
  for _, f in ipairs(__frames) do
    if f.__events[event] then
      local handler = f.__scripts and f.__scripts.OnEvent
      if type(handler) == "function" then
        pcall(handler, f, event, ...)
      end
    end
  end
  local globalHandler = rawget(_G, "OnEvent")
  if type(globalHandler) == "function" then
    pcall(globalHandler, event, ...)
  end
end

if not DEFAULT_CHAT_FRAME then
  DEFAULT_CHAT_FRAME = {
    AddMessage = function(_, msg) print(tostring(msg)) end
  }
end

if not UIParent then
  UIParent = CreateFrame("Frame", "UIParent")
end
""");
    }
}
