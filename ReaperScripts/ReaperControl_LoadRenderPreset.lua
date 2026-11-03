-- ReaperControl_LoadRenderPreset.lua
--
-- Called by the Macro Deck plugin "REAPER Control" (action "Load render preset"):
-- applies the render preset named in ExtState "ReaperControl"/"RenderPreset" and opens the
-- render dialog. Does not start rendering.
--
-- Needs (both via ReaPack / sws-extension.org):
--   - cfillion's "Apply render preset" script (ReaTeam Scripts, Rendering)
--   - the SWS extension (required by that script)
--
-- Load this file once into REAPER's action list (Actions > Show action list > New action >
-- Load ReaScript...). The plugin finds it there by its file name - do not rename it.

-- Where ReaPack installs cfillion's script, relative to the resource path. Adjust if needed.
local APPLY_PRESET_SCRIPT = "/Scripts/ReaTeam Scripts/Rendering/cfillion_Apply render preset.lua"

-- "File: Render..." - opens the render dialog (check the id in the action list if in doubt).
local OPEN_RENDER_DIALOG = 40015

local SECTION, KEY = "ReaperControl", "RenderPreset"

local presetName = reaper.GetExtState(SECTION, KEY)
if presetName == "" then
  reaper.ShowMessageBox("No render preset name was passed.", "REAPER Control", 0)
  return
end

local scriptPath = reaper.GetResourcePath() .. APPLY_PRESET_SCRIPT
if not reaper.file_exists(scriptPath) then
  reaper.ShowMessageBox(
    "cfillion's \"Apply render preset\" script was not found:\n" .. scriptPath ..
    "\n\nInstall it via Extensions > ReaPack > Browse packages, or adjust APPLY_PRESET_SCRIPT in\n" ..
    debug.getinfo(1, "S").source:sub(2),
    "REAPER Control", 0)
  return
end

-- cfillion's script applies the preset named in this global without showing its list
-- (the same mechanism its "create action" scripts use). It reports unknown names itself.
ApplyPresetByName = presetName
dofile(scriptPath)
ApplyPresetByName = nil

reaper.Main_OnCommand(OPEN_RENDER_DIALOG, 0)
