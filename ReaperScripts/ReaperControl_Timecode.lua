-- ReaperControl_Timecode.lua
--
-- Publishes the play/edit cursor position in all of REAPER's time formats for the Macro Deck plugin
-- "REAPER Control" (widget "REAPER timecode"). Runs in the background (defer loop).
--
-- ExtState "ReaperControl" / "Timecode" (not persisted): the position formatted by REAPER itself in
-- the modes 0 time, 1 measures.beats + time, 2 measures.beats, 3 seconds, 4 samples, 5 h:m:s:f,
-- 8 frames - in this order, separated by "|". Keep the order in sync with TimecodeMonitor.ScriptModes.
--
-- Without this script the widget still shows "Like transport window", "Measures.Beats",
-- "Minutes:Seconds" and "Seconds".

local SECTION, KEY = "ReaperControl", "Timecode"
local MODES = { 0, 1, 2, 3, 4, 5, 8 }
local REFRESH = 0.5 -- seconds: also refresh while standing still (tempo, frame rate, project tab changes)

local lastPos, lastValue, lastRefresh = nil, nil, 0

local function position()
  -- Play position while playing, paused or recording; edit cursor while stopped.
  if reaper.GetPlayState() ~= 0 then
    return reaper.GetPlayPosition()
  end
  return reaper.GetCursorPosition()
end

local function loop()
  local pos = position()
  local now = reaper.time_precise()
  if pos ~= lastPos or now - lastRefresh >= REFRESH then
    lastPos, lastRefresh = pos, now
    local parts = {}
    for i, mode in ipairs(MODES) do
      parts[i] = reaper.format_timestr_pos(pos, "", mode)
    end
    local value = table.concat(parts, "|")
    if value ~= lastValue then
      lastValue = value
      reaper.SetExtState(SECTION, KEY, value, false)
    end
  end
  reaper.defer(loop)
end

reaper.atexit(function()
  reaper.DeleteExtState(SECTION, KEY, false)
end)

loop()
