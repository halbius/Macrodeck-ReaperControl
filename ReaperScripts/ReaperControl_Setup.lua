-- ReaperControl_Setup.lua
--
-- Run once after installing the scripts (Macro Deck → REAPER Control settings → "Install/update
-- REAPER scripts"): adds the scripts of the Macro Deck plugin "REAPER Control" that lie next to this
-- file to REAPER's action list, so the plugin can start them by their command id.
--
-- Load it via Actions → Show action list → New action → Load ReaScript, then run it. Running it again
-- does no harm; it is only needed again when a new script comes with a plugin update.

local SCRIPTS = {
  "ReaperControl_ItemState.lua",
  "ReaperControl_TrackState.lua",
  "ReaperControl_Timecode.lua",
  "ReaperControl_LoadRenderPreset.lua",
}

local _, thisFile = reaper.get_action_context()
local folder = thisFile:match("^(.*)[/\\]")

local lines, missing = {}, {}
for i, name in ipairs(SCRIPTS) do
  local path = folder .. "/" .. name
  local f = io.open(path, "r")
  if f then
    f:close()
    -- Last argument: write reaper-kb.ini now (only with the last script, once for all).
    local id = reaper.AddRemoveReaScript(true, 0, path, i == #SCRIPTS)
    lines[#lines + 1] = name .. "  →  " .. (id ~= 0 and reaper.ReverseNamedCommandLookup(id) or "failed")
  else
    missing[#missing + 1] = name
  end
end

if #missing > 0 then
  -- Still write reaper-kb.ini when the last script was the missing one.
  reaper.AddRemoveReaScript(true, 0, thisFile, true)
  lines[#lines + 1] = ""
  lines[#lines + 1] = "Not found next to this script: " .. table.concat(missing, ", ")
end

reaper.ShowMessageBox("Added to the action list:\n\n" .. table.concat(lines, "\n"), "REAPER Control setup", 0)
