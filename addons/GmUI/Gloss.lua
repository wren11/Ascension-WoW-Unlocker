--[[
  GmUI — Gloss
  Reusable glossy / shimmer / spark animation layer shared by every addon.

  Three effects, all texture + OnUpdate based (no new deps, frame-rate driven so
  they stay smooth regardless of the 300ms scheduler cadence):

    1. GLOSS FINISH   — the "wet panel" look. A soft top→bottom vertical gradient
                        (bright highlight at the top edge fading to a subtle
                        shadow) laid over a frame, plus a 1px specular line.
                        Static (no animation). Gives windows/buttons the glossy
                        depth the old flat fills lacked.

    2. SHIMMER SWEEP   — a translucent diagonal highlight band that sweeps
                        left→right across a frame on a loop. Used on the active
                        toolbar tab and focused window title bars. Smooth,
                        subtle, never distracting.

    3. TRAVELING SPARK — a bright accent "comet" that travels along the row of
                        open toolbar chips, highlighting the currently-active
                        addon's chip and continuing to glide. The signature
                        "sexy animated" effect requested for the top bar.

  Public API (all return the created/updated texture set; safe to call repeatedly):
    UI.Gloss.Apply(frame, kind)            -- apply the gloss finish (kind: "bar"|"panel"|"window")
    UI.Gloss.Shimmer(frame, opts)          -- attach a shimmer sweep to a frame
    UI.Gloss.Spark(toolbar, opts)          -- attach a traveling spark to a frame
    UI.Gloss.SetEnabled(bool)              -- master switch (persisted in theme.gloss)
    UI.Gloss.Pulse(frame, colorKey)        -- one-shot attention pulse (flash + fade)

  Performance: a SINGLE shared OnUpdate frame drives every active shimmer/spark,
  so the cost is one frame script regardless of how many effects are live. When
  no effects are active the driver hides itself.
]]

local UI = GmUI
UI.Gloss = UI.Gloss or {}
local G = UI.Gloss

-- defaults merged into the theme
do
  local t = UI.DB().theme
  t.gloss        = (t.gloss == nil) and true or t.gloss
  t.glossAlpha   = t.glossAlpha   or 0.18    -- finish highlight strength
  t.shimmer      = (t.shimmer == nil) and true or t.shimmer
  t.shimmerAlpha = t.shimmerAlpha or 0.22
  t.shimmerSpeed = t.shimmerSpeed or 3.2     -- seconds per sweep
  t.spark        = (t.spark == nil) and true or t.spark
  t.sparkSpeed   = t.sparkSpeed   or 6.0     -- seconds per full travel
  t.sparkAlpha   = t.sparkAlpha   or 0.85
  t.toolbarHeight = t.toolbarHeight or 28    -- shrunk from 40
end

------------------------------------------------------------------------
-- shared animation driver: ONE OnUpdate for all effects
------------------------------------------------------------------------
local driver = CreateFrame("Frame", "GmUIGlossDriver")
driver:Hide()
local shimmers = {}   -- { frame, tex, w, speed, t0, alpha }
local sparks   = {}   -- { bar, tex, glow, speed, t0, alpha, attachTo }
local pulses   = {}   -- { frame, tex, t0, color }

local function syncDriver()
  if #shimmers > 0 or #sparks > 0 or #pulses > 0 then
    driver:Show()
  else
    driver:Hide()
  end
end

local now = 0
driver:SetScript("OnUpdate", function(_, dt)
  now = now + dt
  -- shimmers: sweep a highlight band across the frame
  for i = #shimmers, 1, -1 do
    local s = shimmers[i]
    if not s.frame:IsShown() then
      if s.tex then s.tex:Hide() end
    else
      if s.tex then s.tex:Show() end
      local ph = (now - s.t0) / s.speed
      if ph >= 1 then s.t0 = now; ph = 0 end
      -- band is 30% of width, sweeps from -0.3..1.0
      local x = (ph * 1.3 - 0.3) * s.w
      s.tex:ClearAllPoints()
      s.tex:SetPoint("LEFT", s.frame, "LEFT", x, 0)
    end
  end
  -- sparks: travel along the bar
  for i = #sparks, 1, -1 do
    local sp = sparks[i]
    if not sp.bar:IsShown() then
      if sp.tex then sp.tex:Hide() end
      if sp.glow then sp.glow:Hide() end
    else
      if sp.tex then sp.tex:Show() end
      if sp.glow then sp.glow:Show() end
      local ph = (now - sp.t0) / sp.speed
      if ph >= 1 then sp.t0 = now; ph = 0 end
      local w = sp.bar:GetWidth() or 800
      local x = ph * w
      sp.tex:ClearAllPoints()
      sp.tex:SetPoint("CENTER", sp.bar, "LEFT", x, 0)
      if sp.glow then
        sp.glow:ClearAllPoints()
        sp.glow:SetPoint("CENTER", sp.bar, "LEFT", x, 0)
      end
    end
  end
  -- pulses: one-shot fade
  for i = #pulses, 1, -1 do
    local p = pulses[i]
    local age = now - p.t0
    if age > 0.6 then
      if p.tex then p.tex:Hide() end
      table.remove(pulses, i)
    else
      local a = 1 - (age / 0.6)
      if p.tex then
        p.tex:Show()
        p.tex:SetVertexColor(p.color[1], p.color[2], p.color[3], a * 0.6)
      end
    end
  end
  syncDriver()
end)

------------------------------------------------------------------------
-- master switch
------------------------------------------------------------------------
function G.SetEnabled(on)
  UI.DB().theme.gloss = on and true or false
  UI.DB().theme.shimmer = on and true or false
  UI.DB().theme.spark = on and true or false
  if not on then
    for _, s in ipairs(shimmers) do if s.tex then s.tex:Hide() end end
    for _, s in ipairs(sparks)   do if s.tex then s.tex:Hide() end if s.glow then s.glow:Hide() end end
    driver:Hide()
  else
    syncDriver()
  end
end

------------------------------------------------------------------------
-- 1. GLOSS FINISH
------------------------------------------------------------------------
-- Lays a vertical highlight gradient + specular line over a frame to give it
-- the glossy "wet panel" depth. Idempotent: re-applying refreshes colors.
function G.Apply(frame, kind)
  if not frame or not UI.DB().theme.gloss then return end
  kind = kind or "panel"
  local a = UI.DB().theme.glossAlpha or 0.18
  local accent = UI.ToolColor(frame._color or "toolbox")
  -- top highlight gradient (bright at very top, fading down)
  if not frame._glossTop then
    frame._glossTop = frame:CreateTexture(nil, "BORDER", nil, 1)
    frame._glossTop:SetPoint("TOPLEFT", 1, -1)
    frame._glossTop:SetPoint("TOPRIGHT", -1, -1)
    frame._glossTop:SetHeight((kind == "bar") and 10 or 14)
  end
  frame._glossTop:SetTexture(accent[1] + 0.4, accent[2] + 0.4, accent[3] + 0.4, a)
  frame._glossTop:SetGradientAlpha("VERTICAL",
    accent[1] + 0.5, accent[2] + 0.5, accent[3] + 0.5, a * 2.2,
    accent[1], accent[2], accent[3], 0.0)
  -- specular hairline (the bright reflective edge)
  if not frame._glossSpec then
    frame._glossSpec = frame:CreateTexture(nil, "ARTWORK", nil, 1)
    frame._glossSpec:SetPoint("TOPLEFT", 2, -2)
    frame._glossSpec:SetPoint("TOPRIGHT", -2, -2)
    frame._glossSpec:SetHeight(1)
  end
  frame._glossSpec:SetTexture(1, 1, 1, a * 1.8)
  -- bottom shadow gradient (subtle depth)
  if not frame._glossBot then
    frame._glossBot = frame:CreateTexture(nil, "BORDER", nil, 0)
    frame._glossBot:SetPoint("BOTTOMLEFT", 1, 1)
    frame._glossBot:SetPoint("BOTTOMRIGHT", -1, 1)
    frame._glossBot:SetHeight(8)
  end
  frame._glossBot:SetGradientAlpha("VERTICAL",
    0, 0, 0, 0.0,
    0, 0, 0, a * 0.9)
  return frame
end

------------------------------------------------------------------------
-- 2. SHIMMER SWEEP
------------------------------------------------------------------------
-- Attaches a translucent diagonal band that sweeps across the frame on a loop.
-- opts: { speed=sec, alpha=0..1, color={r,g,b} }
function G.Shimmer(frame, opts)
  if not frame or not UI.DB().theme.shimmer then return end
  opts = opts or {}
  -- find existing shimmer for this frame
  local s
  for _, e in ipairs(shimmers) do if e.frame == frame then s = e; break end end
  if not s then
    s = { frame = frame, tex = nil, w = frame:GetWidth() or 200, t0 = now,
          speed = opts.speed or UI.DB().theme.shimmerSpeed or 3.2,
          alpha = opts.alpha or UI.DB().theme.shimmerAlpha or 0.22 }
    s.tex = frame:CreateTexture(nil, "OVERLAY", nil, 2)
    s.tex:SetBlendMode("ADD")
    s.tex:SetTexture("Interface\\Buttons\\WHITE8x8")
    s.tex:SetWidth(s.w * 0.30)
    s.tex:SetHeight(frame:GetHeight() or 20)
    shimmers[#shimmers + 1] = s
  end
  local col = opts.color or { 1, 1, 1 }
  s.tex:SetVertexColor(col[1], col[2], col[3], s.alpha)
  s.tex:SetGradientAlpha("HORIZONTAL",
    1, 1, 1, 0.0,
    1, 1, 1, s.alpha)
  syncDriver()
  return s
end

------------------------------------------------------------------------
-- 3. TRAVELING SPARK
------------------------------------------------------------------------
-- Attaches a bright "comet" that glides along the width of a frame (the toolbar).
-- opts: { speed=sec, alpha, color={r,g,b}, attachTo=chipFrame }
function G.Spark(bar, opts)
  if not bar or not UI.DB().theme.spark then return end
  opts = opts or {}
  -- single spark per bar
  for i = #sparks, 1, -1 do
    if sparks[i].bar == bar then table.remove(sparks, i) end
  end
  local sp = { bar = bar, t0 = now,
               speed = opts.speed or UI.DB().theme.sparkSpeed or 6.0,
               alpha = opts.alpha or UI.DB().theme.sparkAlpha or 0.85 }
  -- glow halo
  sp.glow = bar:CreateTexture(nil, "OVERLAY", nil, 3)
  sp.glow:SetBlendMode("ADD")
  sp.glow:SetTexture("Interface\\Buttons\\WHITE8x8")
  sp.glow:SetWidth(48)
  sp.glow:SetHeight((bar:GetHeight() or 28) + 6)
  local col = opts.color or { 1, 0.85, 0.4 }
  sp.glow:SetVertexColor(col[1], col[2], col[3], sp.alpha * 0.35)
  sp.glow:SetGradientAlpha("HORIZONTAL",
    col[1], col[2], col[3], 0.0,
    col[1], col[2], col[3], sp.alpha * 0.35,
    col[1], col[2], col[3], 0.0)
  -- bright core
  sp.tex = bar:CreateTexture(nil, "OVERLAY", nil, 4)
  sp.tex:SetBlendMode("ADD")
  sp.tex:SetTexture("Interface\\Buttons\\WHITE8x8")
  sp.tex:SetWidth(20)
  sp.tex:SetHeight((bar:GetHeight() or 28) - 6)
  sp.tex:SetVertexColor(1, 1, 1, sp.alpha)
  sp.tex:SetGradientAlpha("HORIZONTAL",
    1, 1, 1, 0.0, 1, 1, 1, sp.alpha, 1, 1, 1, 0.0)
  sparks[#sparks + 1] = sp
  syncDriver()
  return sp
end

-- Remove the spark from a bar (e.g. when toolbar hides).
function G.RemoveSpark(bar)
  for i = #sparks, 1, -1 do
    if sparks[i].bar == bar then
      if sparks[i].tex then sparks[i].tex:Hide() end
      if sparks[i].glow then sparks[i].glow:Hide() end
      table.remove(sparks, i)
    end
  end
  syncDriver()
end

------------------------------------------------------------------------
-- 4. ONE-SHOT PULSE
------------------------------------------------------------------------
function G.Pulse(frame, colorKey)
  if not frame then return end
  if not frame._pulseTex then
    frame._pulseTex = frame:CreateTexture(nil, "OVERLAY", nil, 5)
    frame._pulseTex:SetAllPoints()
    frame._pulseTex:SetTexture("Interface\\Buttons\\WHITE8x8")
    frame._pulseTex:SetBlendMode("ADD")
  end
  local r, g, b = UI.RGB(colorKey or "accent")
  pulses[#pulses + 1] = { frame = frame, tex = frame._pulseTex, t0 = now, color = { r, g, b } }
  syncDriver()
end
