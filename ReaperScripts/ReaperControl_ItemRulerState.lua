-- ReaperControl_ItemRulerState.lua
--
-- Publishes the item ruler mode of the selected items for the Macro Deck plugin "REAPER Control".
-- Runs in the background (defer loop) until REAPER closes or the script is stopped.
--
-- ExtState "ReaperControl" / "ItemRuler" (not persisted):
--   "1" .. "6"  all selected items use this mode (observed ITEMRULER values, not the menu order):
--               1 Item time, 2 Source time, 3 Item H:M:S:F, 4 Item beats,
--               5 Item beats minimal, 6 Source time H:M:S:F
--   "0"         no item selected, or the ruler is off
--   "mixed"     the selected items use different modes
-- While the script is not running the value is empty and the buttons show "Unavailable".

local SECTION, KEY = "ReaperControl", "ItemRuler"
local CHECK_INTERVAL = 0.1 -- seconds

local lastCheck = 0
local lastSignature = nil
local lastValue = nil

-- ITEMRULER <mode> 0 0 in the item's state chunk; missing when the item has no ruler.
local function ruler_mode(item)
  local ok, chunk = reaper.GetItemStateChunk(item, "", false)
  if not ok then return "0" end
  return chunk:match("\nITEMRULER%s+(%d+)") or "0"
end

local function current_value()
  local count = reaper.CountSelectedMediaItems(0)
  if count == 0 then return "0" end

  local first = ruler_mode(reaper.GetSelectedMediaItem(0, 0))
  for i = 1, count - 1 do
    if ruler_mode(reaper.GetSelectedMediaItem(0, i)) ~= first then
      return "mixed"
    end
  end
  return first
end

-- Cheap check whether anything relevant may have changed: project tab, project edits
-- (the ruler actions are undoable), and which items are selected.
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

local function loop()
  local now = reaper.time_precise()
  if now - lastCheck >= CHECK_INTERVAL then
    lastCheck = now
    local sig = signature()
    if sig ~= lastSignature then
      lastSignature = sig
      local value = current_value()
      if value ~= lastValue then
        lastValue = value
        reaper.SetExtState(SECTION, KEY, value, false)
      end
    end
  end
  reaper.defer(loop)
end

reaper.atexit(function()
  reaper.DeleteExtState(SECTION, KEY, false)
end)

loop()
