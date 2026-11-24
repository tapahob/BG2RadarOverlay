-- Called from the hidden menu while the world is running or paused.
-- Read the saved ledger used by the mod's Known list; never open its editor.
local function collect(id, withHelp)
    local rows = {}
    local sprite = EEex_GameObject_Get(id)
    if not sprite or not EEex_GameObject_IsSprite(sprite, true) then return end
    local info = SAF_Engine.classInfo(sprite, false)
    local snapshot = info and SAF_Engine.snapshot(sprite)
    for _, feat in ipairs(SAF_CatalogOrder) do
        local rank = sprite:getLocalInt("SAF_F_" .. feat.id)
        if rank > 0 or (snapshot and SAF_Rules.isGranted(feat, snapshot))
            or (snapshot and snapshot.legacyFeats and snapshot.legacyFeats[feat.id]) then
            local row = SAF_UI.featRow(feat)
            local name = row.name:gsub("[%c]", " ")
            if rank > 1 then name = name .. " (" .. rank .. ")" end
            local line = string.format("%s\t%d\t%s", row.icon, row.sequence, name)
            if withHelp then
                -- LF delimits rows, so transport the description's paragraph breaks as CR.
                local help = feat.description or feat.specifics or feat.summary or ""
                line = line .. "\t" .. help:gsub("\r\n", "\n"):gsub("\n", "\r"):gsub("\t", " ")
            end
            rows[#rows + 1] = line
        end
    end
    return table.concat(rows, "\n")
end

-- Header shared with FeatCardReader. State is written last on both sides.
-- Only the game writes the reply. One request names one live actor.
local box, reported
local capacity = 1024 * 1024
local function alloc()
    box = EEex_Malloc(32 + capacity)
    EEex_WriteU32(box + 4, 0x61F0D972)
    EEex_WriteU32(box + 8, 1)
    EEex_WriteU32(box + 12, box % 0x100000000)
    EEex_WriteU32(box + 16, 0)
    EEex_WriteU32(box + 28, 0) -- game session
    EEex_WriteU32(box, 0x52444654)
end

function BGRadarFeatTick()
    if not box then
        if not (SAF_UI and SAF_UI.featRow and SAF_Engine and SAF_Rules and SAF_CatalogOrder) then return false end
        alloc()
    end
    if EEex_ReadU32(box + 16) == 1 then
        -- Older clients leave the previous (nonnegative) reply length here.
        local ok, text = pcall(collect, EEex_Read32(box + 20), EEex_Read32(box + 24) == -1)
        if not ok then
            if not reported then print("BG Radar feats: " .. tostring(text)); reported = true end
            text = nil
        end
        text = text or ""
        if #text >= capacity then text = "" end
        EEex_WriteString(box + 32, text)
        EEex_WriteU32(box + 24, #text)
        EEex_WriteU32(box + 16, 2)
    end
    return false
end

if EEex_Menu_AddMainFileLoadedListener then
    EEex_Menu_AddMainFileLoadedListener(function() EEex_Menu_LoadFile("RDRFEATS") end)
    local function push()
        if SAF_Loaded then Infinity_PushMenu("BGRadarFeats") end
    end
    EEex_GameState_AddInitializedListener(push)
    EEex_GameState_AddDestroyedListener(function()
        if box then
            EEex_WriteU32(box + 16, 0)
            EEex_WriteU32(box + 28, EEex_ReadU32(box + 28) + 1)
        end
    end)
    EEex_Menu_AddAfterMainFileReloadedListener(push)
end
