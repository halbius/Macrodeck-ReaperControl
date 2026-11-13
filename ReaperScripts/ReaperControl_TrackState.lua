-- ReaperControl_TrackState.lua
--
-- Publishes settings of the selected tracks for the Macro Deck plugin "REAPER Control"
-- (actions "Track" and "Track lock"). Runs in the background (defer loop) until REAPER closes or
-- the script is stopped. Start it from Scripts/__startup.lua like ReaperControl_ItemState.lua.
--
-- ExtState "ReaperControl" / <key> for every entry of TRACK_PROPERTIES below (not persisted):
--   "1" all selected tracks have it on, "0" off or nothing selected, "mixed" different values.
-- ExtState "ReaperControl" / "TrackAnySolo": "1" while any track of the project is soloed, else "0".
--
-- While the script is not running the values are empty and the buttons show "Unavailable".

local SECTION = "ReaperControl"
local CHECK_INTERVAL = 0.1 -- seconds
local FORCE_INTERVAL = 0.5 -- seconds: also re-read without project changes (mute/solo are often
                           -- not in the undo history, FX windows never are)

-- Track lock flags from the "LOCK <flags>" line of the track's state chunk (there is no
-- GetMediaTrackInfo_Value parameter for them). Check the values: see the test in the README / chat.
-- Track controls lock: "LOCK 1" in the track's own part of the state chunk (before the first
-- item; items have LOCK lines of their own). There is no GetMediaTrackInfo_Value parameter for it.
local function controls_locked(track)
  local ok, chunk = reaper.GetTrackStateChunk(track, "", false)
  if not ok then return false end
  local own = chunk:match("^(.-)\n%s*<ITEM") or chunk
  return (tonumber(own:match("\nLOCK%s+(%d+)")) or 0) & 1 ~= 0
end

-- "Lock track items" (43696) locks every item on the track: on while the track has items and
-- all of them are locked.
local function items_locked(track)
  local count = reaper.CountTrackMediaItems(track)
  if count == 0 then return false end
  for i = 0, count - 1 do
    if reaper.GetMediaItemInfo_Value(reaper.GetTrackMediaItem(track, i), "C_LOCK") & 1 == 0 then
      return false
    end
  end
  return true
end

-- Track settings to publish: ExtState key = GetMediaTrackInfo_Value parameter name (case-sensitive),
-- or a function (track) -> true/false.
-- To add one: one line here, plus one line in Actions/TrackAction.cs with State: SelectedTracks("<key>").
local TRACK_PROPERTIES = {
  TrackMute = "B_MUTE",
  TrackSolo = "I_SOLO",   -- 0 = not soloed, 1/2/5/6 = the different solo modes
  TrackPhase = "B_PHASE",
  TrackLockHeight = "B_HEIGHTLOCK",
  TrackLockControls = controls_locked,
  TrackLockItems = items_locked,
  -- FX chain window of the track is open (TrackFX_GetChainVisible: -1 = hidden).
  TrackFXChain = function(track)
    return reaper.TrackFX_GetChainVisible(track) ~= -1
  end,
}

local lastCheck = 0
local lastForced = 0
local lastSignature = nil
local lastValues = {}

local function publish(key, value)
  if lastValues[key] ~= value then
    lastValues[key] = value
    reaper.SetExtState(SECTION, key, value, false)
  end
end

-- One value for all selected tracks: the common value, "0" for no selection, "mixed" otherwise.
local function common(read)
  local count = reaper.CountSelectedTracks(0)
  if count == 0 then return "0" end

  local first = read(reaper.GetSelectedTrack(0, 0))
  for i = 1, count - 1 do
    if read(reaper.GetSelectedTrack(0, i)) ~= first then
      return "mixed"
    end
  end
  return first
end

local function any_solo()
  for i = 0, reaper.CountTracks(0) - 1 do
    if reaper.GetMediaTrackInfo_Value(reaper.GetTrack(0, i), "I_SOLO") ~= 0 then
      return "1"
    end
  end
  return "0"
end

-- Cheap check whether anything relevant may have changed: project tab, project edits and which
-- tracks are selected.
local function signature()
  local parts = {
    tostring(reaper.EnumProjects(-1)),
    reaper.GetProjectStateChangeCount(0),
  }
  for i = 0, reaper.CountSelectedTracks(0) - 1 do
    parts[#parts + 1] = tostring(reaper.GetSelectedTrack(0, i))
  end
  return table.concat(parts, "|")
end

local function update()
  for key, prop in pairs(TRACK_PROPERTIES) do
    publish(key, common(function(track)
      local on
      if type(prop) == "function" then
        on = prop(track)
      else
        on = reaper.GetMediaTrackInfo_Value(track, prop) ~= 0
      end
      return on and "1" or "0"
    end))
  end
  publish("TrackAnySolo", any_solo())
end

local function loop()
  local now = reaper.time_precise()
  if now - lastCheck >= CHECK_INTERVAL then
    lastCheck = now
    local sig = signature()
    if sig ~= lastSignature or now - lastForced >= FORCE_INTERVAL then
      lastSignature = sig
      lastForced = now
      update()
    end
  end
  reaper.defer(loop)
end

reaper.atexit(function()
  for key in pairs(TRACK_PROPERTIES) do
    reaper.DeleteExtState(SECTION, key, false)
  end
  reaper.DeleteExtState(SECTION, "TrackAnySolo", false)
end)

loop()
