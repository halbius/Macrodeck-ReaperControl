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

-- Volume fader (Macro Deck variable "REAPER: Track volume"): "TrackVolume" is the volume of the
-- loudest selected track in dB ("-" when nothing is selected). The plugin sends "TrackVolumeSet" =
-- "<seq>|<dB>": the loudest one is set to that value and all other selected tracks move by the same
-- amount (trim, spacing kept). "TrackVolumeCommit" = "<seq>" after the fader was let go makes the
-- whole movement one undo step.
local VOL_MIN, VOL_MAX = -60, 12 -- dB; VOL_MIN and below = -inf

local function to_db(v)
  if v <= 0 then return VOL_MIN end
  return math.max(VOL_MIN, 20 * math.log(v, 10))
end

local function from_db(db)
  if db <= VOL_MIN then return 0 end
  return 10 ^ (math.min(db, VOL_MAX) / 20)
end

-- The loudest selected track (in dB, the object itself) and how many are selected.
local function loudest()
  local loudestDb, loudestObj
  local count = reaper.CountSelectedTracks(0)
  for i = 0, count - 1 do
    local obj = reaper.GetSelectedTrack(0, i)
    local db = to_db(reaper.GetMediaTrackInfo_Value(obj, "D_VOL"))
    if not loudestDb or db > loudestDb then loudestDb, loudestObj = db, obj end
  end
  return loudestDb, loudestObj, count
end

local function loudest_db()
  return (loudest())
end

local function volume_text()
  local db = loudest_db()
  return db and string.format("%.2f", db) or "-"
end

local function apply_volume(target)
  local loudest = loudest_db()
  if not loudest then return end
  local delta = math.min(target, VOL_MAX) - loudest
  for i = 0, reaper.CountSelectedTracks(0) - 1 do
    local obj = reaper.GetSelectedTrack(0, i)
    local value = from_db(to_db(reaper.GetMediaTrackInfo_Value(obj, "D_VOL")) + delta)
    reaper.SetMediaTrackInfo_Value(obj, "D_VOL", value)
  end
end

-- Names for the fader label (Macro Deck variables reaper_track_name / reaper_track_number): the loudest
-- selected track, i.e. the one whose volume the fader shows, plus "(+n)" for the other selected ones.
-- Empty when nothing is selected.
local function publish_names()
  local _, track, count = loudest()
  if not track then
    publish("TrackName", "")
    publish("TrackNumber", "")
    return
  end
  local _, name = reaper.GetTrackName(track)
  publish("TrackName", count > 1 and string.format("%s (+%d)", name, count - 1) or name)
  publish("TrackNumber", tostring(math.floor(reaper.GetMediaTrackInfo_Value(track, "IP_TRACKNUMBER"))))
end

local lastVolSet = reaper.GetExtState(SECTION, "TrackVolumeSet")
local lastVolCommit = reaper.GetExtState(SECTION, "TrackVolumeCommit")
local undoPending = false

-- Called on every defer cycle: fader moves must not wait for CHECK_INTERVAL.
local function handle_volume()
  local set = reaper.GetExtState(SECTION, "TrackVolumeSet")
  if set ~= lastVolSet then
    lastVolSet = set
    local db = tonumber(set:match("|(.+)$") or "")
    if db then
      apply_volume(db)
      undoPending = true
      publish("TrackVolume", volume_text())
    end
  end
  local commit = reaper.GetExtState(SECTION, "TrackVolumeCommit")
  if commit ~= lastVolCommit then
    lastVolCommit = commit
    if undoPending then
      undoPending = false
      reaper.Undo_OnStateChange2(0, "Track volume (Macro Deck)")
    end
  end
end

local function update()
  publish("TrackVolume", volume_text())
  publish_names()
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

-- Started and stopped by the plugin (ScriptKeeper): "TrackStateRunning" tells it the script runs; the
-- plugin writes a changing "TrackStateHeartbeat" while it needs the values. Without a new heartbeat for
-- HEARTBEAT_TIMEOUT seconds (Macro Deck closed, no button needs it) the script ends itself.
local SCRIPT_NAME = "TrackState"
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
  handle_volume()
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
  reaper.DeleteExtState(SECTION, "TrackName", false)
  reaper.DeleteExtState(SECTION, "TrackNumber", false)
  reaper.DeleteExtState(SECTION, "TrackVolume", false)
  reaper.DeleteExtState("ReaperControl", SCRIPT_NAME .. "Running", false)
  for key in pairs(TRACK_PROPERTIES) do
    reaper.DeleteExtState(SECTION, key, false)
  end
  reaper.DeleteExtState(SECTION, "TrackAnySolo", false)
end)

loop()
