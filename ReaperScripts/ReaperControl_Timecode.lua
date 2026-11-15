-- ReaperControl_Timecode.lua
--
-- Publishes the play/edit cursor position in all of REAPER's time formats for the Macro Deck plugin
-- "REAPER Control" (widget "REAPER timecode"). Runs in the background (defer loop).
--
-- ExtState "ReaperControl" / "Timecode" (not persisted): the position formatted by REAPER itself in
-- the modes 0 time, 1 measures.beats + time, 2 measures.beats, 3 seconds, 4 samples, 5 h:m:s:f,
-- 8 frames - in this order, separated by "|". Keep the order in sync with TimecodeMonitor.ScriptModes.
--
-- ExtState "ReaperControl" / "PlayRate": the project playback rate, e.g. "1.00" (widget info line).
--
-- Without this script the widget still shows "Like transport window", "Measures.Beats",
-- "Minutes:Seconds" and "Seconds".

local SECTION, KEY = "ReaperControl", "Timecode"
local MODES = { 0, 1, 2, 3, 4, 5, 8 }
local REFRESH = 0.5 -- seconds: also refresh while standing still (tempo, frame rate, project tab changes)

local lastPos, lastValue, lastRefresh, lastRate = nil, nil, 0, nil

local function position()
  -- Play position while playing, paused or recording; edit cursor while stopped.
  if reaper.GetPlayState() ~= 0 then
    return reaper.GetPlayPosition()
  end
  return reaper.GetCursorPosition()
end

-- Started and stopped by the plugin (ScriptKeeper): "TimecodeRunning" tells it the script runs; the
-- plugin writes a changing "TimecodeHeartbeat" while it needs the values. Without a new heartbeat for
-- HEARTBEAT_TIMEOUT seconds (Macro Deck closed, no button needs it) the script ends itself.
local SCRIPT_NAME = "Timecode"
local HEARTBEAT_TIMEOUT = 10 -- seconds
reaper.SetExtState("ReaperControl", SCRIPT_NAME .. "Running", "1", false)
local lastBeat = reaper.GetExtState("ReaperControl", SCRIPT_NAME .. "Heartbeat")
local lastBeatTime, lastBeatCheck = reaper.time_precise(), 0

local function plugin_alive(now)
  if now - lastBeatCheck < 1 then return true end
  lastBeatCheck = now
  local beat = reaper.GetExtState("ReaperControl", SCRIPT_NAME .. "Heartbeat")
  if beat ~= lastBeat then
    lastBeat, lastBeatTime = beat, now
  end
  return now - lastBeatTime < HEARTBEAT_TIMEOUT
end

local function loop()
  local now = reaper.time_precise()
  if not plugin_alive(now) then return end -- no defer: the script ends, atexit cleans up
  local pos = position()
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
    local rate = string.format("%.2f", reaper.Master_GetPlayRate(0))
    if rate ~= lastRate then
      lastRate = rate
      reaper.SetExtState(SECTION, "PlayRate", rate, false)
    end
  end
  reaper.defer(loop)
end

reaper.atexit(function()
  reaper.DeleteExtState("ReaperControl", SCRIPT_NAME .. "Running", false)
  reaper.DeleteExtState(SECTION, KEY, false)
  reaper.DeleteExtState(SECTION, "PlayRate", false)
end)

loop()
