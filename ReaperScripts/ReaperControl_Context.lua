-- ReaperControl_Context.lua
--
-- Publishes REAPER's cursor context - the area last clicked: tracks (TCP/mixer), items or envelopes -
-- for the Macro Deck plugin "REAPER Control" (trigger "REAPER context changed", e.g. to open a folder).
-- Runs in the background (defer loop) while the plugin needs it; started and stopped by the plugin.
--
-- ExtState "ReaperControl" / "Context" (not persisted): "tracks", "items" or "envelopes".

local SECTION = "ReaperControl"
local CHECK_INTERVAL = 0.1 -- seconds

local NAMES = { [0] = "tracks", [1] = "items", [2] = "envelopes" }

local lastCheck, lastValue = 0, nil

-- Started and stopped by the plugin (ScriptKeeper): "ContextRunning" tells it the script runs; the
-- plugin writes a changing "ContextHeartbeat" while it needs the values. Without a new heartbeat for
-- HEARTBEAT_TIMEOUT seconds (Macro Deck closed, no trigger needs it) the script ends itself.
local SCRIPT_NAME = "Context"
local HEARTBEAT_TIMEOUT = 10 -- seconds
reaper.SetExtState(SECTION, SCRIPT_NAME .. "Running", "1", false)
local lastBeat = reaper.GetExtState(SECTION, SCRIPT_NAME .. "Heartbeat")
local lastBeatTime, lastBeatCheck = reaper.time_precise(), 0

local function plugin_alive(now)
  if now - lastBeatCheck < 1 then return true end
  lastBeatCheck = now
  local beat = reaper.GetExtState(SECTION, SCRIPT_NAME .. "Heartbeat")
  if beat ~= lastBeat then
    lastBeat, lastBeatTime = beat, now
  end
  return now - lastBeatTime < HEARTBEAT_TIMEOUT
end

local function loop()
  local now = reaper.time_precise()
  if not plugin_alive(now) then return end -- no defer: the script ends, atexit cleans up
  if now - lastCheck >= CHECK_INTERVAL then
    lastCheck = now
    -- true: the last valid context, so clicking a toolbar or another window keeps it.
    local value = NAMES[reaper.GetCursorContext2(true)]
    if value and value ~= lastValue then
      lastValue = value
      reaper.SetExtState(SECTION, "Context", value, false)
    end
  end
  reaper.defer(loop)
end

reaper.atexit(function()
  reaper.DeleteExtState(SECTION, SCRIPT_NAME .. "Running", false)
  reaper.DeleteExtState(SECTION, "Context", false)
end)

loop()
