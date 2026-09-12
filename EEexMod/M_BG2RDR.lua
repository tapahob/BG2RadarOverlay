-------------------------------------------------------------------------------
-- BG2 Radar Overlay - spawn bridge (requires EEex)
--
-- Lets the radar overlay, which runs as a separate process, ask the game to
-- spawn a creature - the game-side half of the Twitch channel-points feature.
--
-- The overlay never calls into the engine itself. It only writes a request into
-- the mailbox buffer allocated below; this script, running on the game's own
-- thread from a per-frame UI tick (see BG2RDR.menu), performs the actual spawn.
-- That split is the whole point of doing it this way: an external thread poking
-- engine structures directly is what crashes games and corrupts saves.
--
-- Install: copy BOTH this file and BG2RDR.menu into the game's override folder.
--
-- The overlay finds the mailbox by scanning the game's heap for the magic. The
-- handshake deliberately runs in that direction - an external process can probe
-- unmapped memory harmlessly (ReadProcessMemory just returns false), whereas a
-- bad read from in here would take the game down with it.
--
-- EEex must be installed as well - the EEex_* functions used below come from its
-- own Lua files, and without them this file errors out on load.
-------------------------------------------------------------------------------

-- Mailbox layout (0x28 bytes):
--   0x00  u32       MAGIC0
--   0x04  u32       MAGIC1
--   0x08  u32       layout version
--   0x0C  u32       low 32 bits of this struct's own address
--   0x10  u32       request flag: 0 = idle, 1 = spawn pending
--   0x14  char[16]  creature ResRef, null-terminated
--   0x24  u32       how many of it to spawn
--
-- The magic is deliberately a pair of numbers rather than a string. An earlier
-- version used an ASCII magic, which backfired badly: the literal lived in this
-- very file, Lua interned it null-terminated, and the overlay happily "found"
-- the string constant in the heap and wrote its request into Lua's string
-- table. Numeric constants leave no matching byte run in the loaded source.
-- The self-address field is the second half of that fix - random data that
-- happens to match both magics still won't contain its own address.
local MAGIC0      = 0x9E3779B9
local MAGIC1      = 0x7F4A7C15
local VERSION     = 3
local SIZE        = 0x28
local OFF_MAGIC1  = 0x04
local OFF_VERSION = 0x08
local OFF_SELF    = 0x0C
local OFF_FLAG    = 0x10
local OFF_RESREF  = 0x14
local OFF_AMOUNT  = 0x24

-- A typo on the overlay side shouldn't be able to lock up the game spawning thousands of
-- creatures, so the count is clamped here as well as validated over there.
local MAX_AMOUNT  = 20

BG2RDR_Mailbox = nil

-- EEex_Write32 takes a *signed* int32 and rejects anything above 0x7FFFFFFF, so any value
-- with the high bit set has to be handed over as its two's-complement negative. The bytes
-- written are identical either way, which is all the overlay compares against.
local function toSigned32(value)
    if value >= 0x80000000 then
        return value - 0x100000000
    end
    return value
end

-- Allocated once per process and never freed. An earlier version tied this to the game state
-- (alloc on initialized, free on destroyed) on the assumption that EEex_Malloc'd memory
-- wouldn't survive a reload. It does - it's ordinary process heap - and the destroy listener
-- fired without a matching re-initialise, which zeroed the magic and left the bridge dead for
-- the rest of the session: exactly one summon would work, then nothing.
local function allocMailbox()

    if BG2RDR_Mailbox ~= nil then
        return
    end

    local address = EEex_Malloc(SIZE)

    -- Zero the struct before stamping the magic: the overlay trusts the rest of
    -- the struct once the magic matches, so it must never be able to observe a
    -- mailbox whose flag/resref still hold allocator garbage.
    for i = 0, SIZE - 1 do
        EEex_Write8(address + i, 0)
    end

    EEex_Write32(address + OFF_VERSION, VERSION)
    -- Modulo rather than a bitwise AND: the engine's Lua version isn't pinned
    -- (EEex ships an optional LuaJIT/5.1 component, where '&' is a syntax error)
    -- and '%' means the same thing here in every version.
    EEex_Write32(address + OFF_SELF, toSigned32(address % 0x100000000))

    -- Magic last, for the same reason: it's what makes the struct discoverable.
    EEex_Write32(address, toSigned32(MAGIC0))
    EEex_Write32(address + OFF_MAGIC1, toSigned32(MAGIC1))

    BG2RDR_Mailbox = address
    EEex_FunctionLog(string.format("mailbox at 0x%X", address))
end

local function spawn(resref, amount)

    -- C is the engine's console table (C:CreateCreature). Older builds expose it
    -- as CLUAConsole; check both rather than assuming, since a missing table
    -- would otherwise surface as an unexplained silent no-op.
    local console = C or CLUAConsole
    if console == nil then
        EEex_FunctionLog("no console table (C / CLUAConsole) - cannot spawn")
        return
    end

    if amount < 1 then amount = 1 end
    if amount > MAX_AMOUNT then amount = MAX_AMOUNT end

    -- Each call drops one creature at the centre of the screen; the engine nudges
    -- them to nearby valid points, so a pack doesn't end up stacked on one tile.
    for _ = 1, amount do
        console:CreateCreature(resref)
    end
end

local function poll()

    local address = BG2RDR_Mailbox
    if address == nil then
        return
    end

    if EEex_Read32(address + OFF_FLAG) == 0 then
        return
    end

    local resref = EEex_ReadString(address + OFF_RESREF)
    local amount = EEex_Read32(address + OFF_AMOUNT)

    -- Clear the flag *before* spawning. If CreateCreature throws (bad ResRef,
    -- no area loaded, ...) the mailbox has to end up idle anyway, otherwise one
    -- bad request wedges the bridge for the rest of the session.
    EEex_Write32(address + OFF_FLAG, 0)

    EEex_FunctionLog(string.format("consuming request '%s' x%d", resref, amount))

    if resref ~= "" then
        spawn(resref, amount)
    end
end

-- Driven by BG2RDR.menu, whose label "enabled" expression the engine evaluates every frame.
-- That is the only polling source here that keeps running while the game is PAUSED - the AI
-- listener below fires on script trigger events (a door opening, a creature spotting someone)
-- and goes completely quiet in a idle or paused game, which made summons appear to do nothing
-- until something unrelated happened to run a script.
--
-- The engine evaluates this far more often than once a frame, so it is throttled; B3Timer.lua,
-- shipped with EEex, warns that spamming work from here makes the game lag.
local nextPollTick = 0

function BG2RDR_Tick()
    local now = Infinity_GetClockTicks()
    if now >= nextPollTick then
        nextPollTick = now + 100
        -- Self-healing: whichever listener fires first wins, and the bridge still comes up
        -- even if none of them do. allocMailbox() is a no-op once a mailbox exists.
        allocMailbox()
        poll()
    end
    -- Keeps the label disabled, so it never draws or takes input.
    return false
end

local function pushMenu()
    Infinity_PushMenu("BG2RDR_Menu")
end

-- The .menu file has to be handed to the engine before anything can push it - pushing an
-- unloaded menu just does nothing, with no error. Fires on the initial UI.MENU load and on an
-- F5 UI reload, matching how B3Timer loads its own menu.
EEex_Menu_AddMainFileLoadedListener(function()
    EEex_Menu_LoadFile("BG2RDR")
end)

EEex_GameState_AddInitializedListener(function()
    allocMailbox()
    pushMenu()
end)

-- The menu has to be re-pushed after a UI reload, same as B3Timer does.
EEex_Menu_AddAfterMainFileReloadedListener(pushMenu)

-- Logged at file scope so the log tells apart "this file never loaded" from "it loaded but
-- the listeners never fired" - the two failure modes look identical from the overlay side.
EEex_FunctionLog("spawn bridge loaded")

-- Secondary, and deliberately kept: this only fires on script trigger events, so it is no use
-- on its own (it stays silent in a paused or quiet area), but it costs one u32 read and keeps
-- the bridge partly working for anyone who copied M_BG2RDR.lua without BG2RDR.menu.
--
-- Both paths run on the game's own thread, so they cannot race each other; whichever sees the
-- flag first clears it and the other finds the mailbox idle.
--
-- Rate limiting is the overlay's job. The mailbox holding one request at a time
-- gives a crude backstop for free: a new request can't be posted until the
-- previous one has been consumed.
EEex_AIBase_AddScriptingObjectUpdatedListener(poll)
