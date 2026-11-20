-- ReaperControl_ThemeColors.lua
--
-- Publishes the color REAPER draws markers without a color of their own in (from the current theme)
-- for the Macro Deck plugin "REAPER Control" (marker buttons with "Use marker color").
-- Runs once and ends; the plugin starts it when the value is missing (after REAPER or the plugin
-- started). Run it by hand after switching the theme.
--
-- ExtState "ReaperControl" / "ThemeMarkerColor" (not persisted): "#rrggbb".

if not reaper.GetThemeColor then return end -- REAPER older than 6.x: the plugin uses its default red

local native = reaper.GetThemeColor("marker", 0)
if native and native ~= -1 then
  local r, g, b = reaper.ColorFromNative(native)
  reaper.SetExtState("ReaperControl", "ThemeMarkerColor", string.format("#%02x%02x%02x", r, g, b), false)
end
