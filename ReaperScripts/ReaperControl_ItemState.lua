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

local function update()
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
  reaper.DeleteExtState(SECTION, "ItemRuler", false)
  for key in pairs(ITEM_PROPERTIES) do
    reaper.DeleteExtState(SECTION, key, false)
  end
end)

loop()
