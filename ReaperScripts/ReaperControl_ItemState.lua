-- ReaperControl_ItemState.lua
--
-- Publishes settings of the selected items for the Macro Deck plugin "REAPER Control"
-- (actions "Item ruler" and "Item"). Runs in the background (defer loop) until REAPER closes or the
-- script is stopped.
--
-- ExtState "ReaperControl" / "ItemRuler" (not persisted):
--   "1" .. "6"  all selected items use this mode (observed ITEMRULER values, not the menu order):
--               1 Item time, 2 Source time, 3 Item H:M:S:F, 4 Item beats,
--               5 Item beats minimal, 6 Source time H:M:S:F
--   "0"         no item selected, or the ruler is off
--   "mixed"     the selected items use different modes
--
-- ExtState "ReaperControl" / <key> for every entry of ITEM_PROPERTIES below:
--   "1" all selected items have it on, "0" off or nothing selected, "mixed" different values.
--
-- While the script is not running the values are empty and the buttons show "Unavailable".

local SECTION = "ReaperControl"
local CHECK_INTERVAL = 0.1 -- seconds

-- Item settings to publish: ExtState key = GetMediaItemInfo_Value parameter name, or a function
-- (item) -> true/false for anything that is not an item parameter.
-- To add one: one line here, plus one line in Actions/ItemAction.cs with State: SelectedItems("<key>").
local ITEM_PROPERTIES = {
  ItemMute = "B_MUTE",
  ItemLock = "C_LOCK",
  ItemLoop = "B_LOOPSRC",
  ItemPlayAllTakes = "B_ALLTAKESPLAY",
  -- FX chain window of the active take is open (TakeFX_GetChainVisible: -1 = hidden).
  ItemFXChain = function(item)
    local take = reaper.GetActiveTake(item)
    return take ~= nil and reaper.TakeFX_GetChainVisible(take) ~= -1
  end,
}

local FORCE_INTERVAL = 0.5 -- seconds: also re-read without project changes (e.g. FX window opened)

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

-- ITEMRULER <mode> 0 0 in the item's state chunk; missing when the item has no ruler.
local function ruler_mode(item)
  local ok, chunk = reaper.GetItemStateChunk(item, "", false)
  if not ok then return "0" end
  return chunk:match("\nITEMRULER%s+(%d+)") or "0"
end

-- One value for all selected items: the common value, "0" for no selection, "mixed" otherwise.
local function common(read)
  local count = reaper.CountSelectedMediaItems(0)
  if count == 0 then return "0" end

  local first = read(reaper.GetSelectedMediaItem(0, 0))
  for i = 1, count - 1 do
    if read(reaper.GetSelectedMediaItem(0, i)) ~= first then
      return "mixed"
    end
  end
  return first
end

-- Cheap check whether anything relevant may have changed: project tab, project edits
-- (item setting actions are undoable), and which items are selected.
local function signature()
  local parts = {
    tostring(reaper.EnumProjects(-1)),
    reaper.GetProjectStateChangeCount(0),
  }
  for i = 0, reaper.CountSelectedMediaItems(0) - 1 do
    parts[#parts + 1] = tostring(reaper.GetSelectedMediaItem(0, i))
  end
  return table.concat(parts, "|")
end

-- Volume fader (Macro Deck variable "REAPER: Item volume"): "ItemVolume" is the volume of the
-- loudest selected item in dB ("-" when nothing is selected). The plugin sends "ItemVolumeSet" =
-- "<seq>|<dB>": the loudest one is set to that value and all other selected items move by the same
-- amount (trim, spacing kept). "ItemVolumeCommit" = "<seq>" after the fader was let go makes the
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

local function loudest_db()
  local loudest
  for i = 0, reaper.CountSelectedMediaItems(0) - 1 do
    local obj = reaper.GetSelectedMediaItem(0, i)
    local db = to_db(reaper.GetMediaItemInfo_Value(obj, "D_VOL"))
    if not loudest or db > loudest then loudest = db end
  end
  return loudest
end

local function volume_text()
  local db = loudest_db()
  return db and string.format("%.2f", db) or "-"
end

local function apply_volume(target)
  local loudest = loudest_db()
  if not loudest then return end
  local delta = math.min(target, VOL_MAX) - loudest
  for i = 0, reaper.CountSelectedMediaItems(0) - 1 do
    local obj = reaper.GetSelectedMediaItem(0, i)
    local value = from_db(to_db(reaper.GetMediaItemInfo_Value(obj, "D_VOL")) + delta)
    reaper.SetMediaItemInfo_Value(obj, "D_VOL", value)
  end
  reaper.UpdateArrange()
end

local lastVolSet = reaper.GetExtState(SECTION, "ItemVolumeSet")
local lastVolCommit = reaper.GetExtState(SECTION, "ItemVolumeCommit")
local undoPending = false

-- Called on every defer cycle: fader moves must not wait for CHECK_INTERVAL.
local function handle_volume()
  local set = reaper.GetExtState(SECTION, "ItemVolumeSet")
  if set ~= lastVolSet then
    lastVolSet = set
    local db = tonumber(set:match("|(.+)$") or "")
    if db then
      apply_volume(db)
      undoPending = true
      publish("ItemVolume", volume_text())
    end
  end
  local commit = reaper.GetExtState(SECTION, "ItemVolumeCommit")
  if commit ~= lastVolCommit then
    lastVolCommit = commit
    if undoPending then
      undoPending = false
      reaper.Undo_OnStateChange2(0, "Item volume (Macro Deck)")
    end
  end
end

local function update()
  publish("ItemVolume", volume_text())
  publish("ItemRuler", common(ruler_mode))
  for key, prop in pairs(ITEM_PROPERTIES) do
    publish(key, common(function(item)
      local on
      if type(prop) == "function" then
        on = prop(item)
      else
        on = reaper.GetMediaItemInfo_Value(item, prop) ~= 0
      end
      return on and "1" or "0"
    end))
  end
end

-- Started and stopped by the plugin (ScriptKeeper): "ItemStateRunning" tells it the script runs; the
-- plugin writes a changing "ItemStateHeartbeat" while it needs the values. Without a new heartbeat for
-- HEARTBEAT_TIMEOUT seconds (Macro Deck closed, no button needs it) the script ends itself.
local SCRIPT_NAME = "ItemState"
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
  reaper.DeleteExtState(SECTION, "ItemVolume", false)
  reaper.DeleteExtState("ReaperControl", SCRIPT_NAME .. "Running", false)
  reaper.DeleteExtState(SECTION, "ItemRuler", false)
  for key in pairs(ITEM_PROPERTIES) do
    reaper.DeleteExtState(SECTION, key, false)
  end
end)

loop()
