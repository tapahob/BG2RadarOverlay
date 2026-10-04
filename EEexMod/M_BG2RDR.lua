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

-- Mailbox layout (0x88 bytes):
--   0x00  u32       MAGIC0
--   0x04  u32       MAGIC1
--   0x08  u32       layout version
--   0x0C  u32       low 32 bits of this struct's own address
--   0x10  u32       request flag: 0 = idle, 1 = spawn pending
--   0x14  char[16]  creature ResRef, null-terminated
--   0x24  u32       how many of it to spawn
--   0x28  char[96]  viewer message, null-terminated (empty = show nothing)
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
local VERSION     = 5
local SIZE        = 0x8C
local OFF_MAGIC1  = 0x04
local OFF_VERSION = 0x08
local OFF_SELF    = 0x0C
local OFF_FLAG    = 0x10
local OFF_RESREF  = 0x14
local OFF_AMOUNT  = 0x24
local OFF_MESSAGE = 0x28
-- The message field ends exactly at 0x88, so the target slot grew the struct. That is why this
-- is VERSION 5 and not a silent addition: an overlay writing a target into a mailbox allocated
-- by the old mod would be writing four bytes past the end of it.
local OFF_TARGET  = 0x88

-- A typo on the overlay side shouldn't be able to lock up the game spawning thousands of
-- creatures, so the count is clamped here as well as validated over there.
local MAX_AMOUNT  = 20

-- Party slots a summon can be aimed at - Player1..Player6 in OBJECT.IDS. Anything else, zero
-- included, means the protagonist, which is what a summon that names nobody gets.
local MAX_PARTY_SLOT = 6

-- Pause the game once a summoned pack has landed, so a streamer who has stepped away does not
-- come back dead. Set to false to let summons arrive into a running game.
local PAUSE_ON_SUMMON = true

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

-- C is the engine's console table (C:CreateCreature). Older builds expose it as CLUAConsole;
-- check both rather than assuming, since a missing table would otherwise surface as an
-- unexplained silent no-op.
local function getConsole()
    local console = C or CLUAConsole
    if console == nil then
        EEex_FunctionLog("no console table (C / CLUAConsole) - cannot move the view or spawn")
    end
    return console
end

-- Scrolls the view onto a party member. PlayerN is OBJECT.IDS (Player1 is 21), INSTANT is
-- SCROLL.IDS 0; both are resolved by the engine's own parser, so the symbolic names go here
-- rather than the numbers.
--
-- Resolved live, by name, rather than from a position the overlay sent: the snapshot a viewer
-- picked from is a couple of seconds old, and the party moves. PlayerN is whoever is in that
-- slot at the moment the scroll runs.
--
-- This matters for more than presentation. CreateCreature drops its creature at the *centre of
-- the current view*, so without this a pack lands wherever the camera happened to be rather
-- than next to the character it was aimed at.
local function moveViewToPartyMember(slot)

    local console = getConsole()
    if console == nil then
        return
    end

    if slot == nil or slot < 1 or slot > MAX_PARTY_SLOT then
        slot = 1
    end

    -- Eval appends to the action queue rather than running inline (EEex's own docs describe
    -- QueueResponseStringOnAIBase as "behavior identical to C:Eval()"), so the scroll lands on a
    -- later frame. That is exactly why the spawn below waits for it instead of following in the
    -- same tick.
    console:Eval(string.format("MoveViewObject(Player%d,INSTANT)", slot))
end

-- Pauses, if the game is running. TogglePauseGame is a *toggle*, so calling it on an already
-- paused game would resume one - the exact opposite of the point, and the worst possible outcome
-- for somebody who is not at the keyboard. Hence the state check first.
--
-- The call and its argument list come from B3TimeStep.lua, which ships with EEex and does the
-- same thing for its time-step key. Wrapped in pcall because this reaches further into engine
-- internals than anything else here: if a future build moves it, a summon that cannot pause is
-- a great deal better than one that throws and leaves the bridge wedged.
local function pauseGame()

    local ok, alreadyPaused = pcall(function()
        return worldScreen:CheckIfPaused()
    end)

    if not ok then
        EEex_FunctionLog("could not read the pause state - leaving the game running")
        return
    end

    if alreadyPaused then
        return
    end

    -- byte visualPause, byte bSendMessage, int idPlayerPause, byte bLogPause, byte bRequireHostUnpause
    local pauseOk = pcall(function()
        EngineGlobals.g_pBaldurChitin.m_pEngineWorld:TogglePauseGame(true, true, 0, false, false)
    end)

    if not pauseOk then
        EEex_FunctionLog("could not pause the game")
    end
end

local function spawn(resref, amount)

    local console = getConsole()
    if console == nil then
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

-- A request whose camera move has been queued but whose creatures have not been dropped yet.
-- One at a time: a second request waits in the mailbox until this one has landed, which keeps
-- two packs from being scrolled to and spawned on top of each other.
local pendingSpawn = nil

-- Poll ticks to wait between queueing the scroll and dropping the pack. The poll below is
-- throttled to 100ms, so this is roughly a fifth of a second - long enough for a queued INSTANT
-- scroll to have run and the view to have settled, and it reads on stream the way it was asked
-- for: the camera finds the party, and then the ambush arrives.
local SPAWN_DELAY_TICKS = 2

local function poll()

    local address = BG2RDR_Mailbox
    if address == nil then
        return
    end

    -- Held over from an earlier tick, waiting for the camera to arrive.
    if pendingSpawn ~= nil then
        pendingSpawn.ticks = pendingSpawn.ticks - 1
        if pendingSpawn.ticks <= 0 then
            local request = pendingSpawn
            pendingSpawn = nil
            spawn(request.resref, request.amount)

            -- Only once nothing else is queued. A pack is written one creature at a time, and a
            -- viewer can buy several at once, so pausing after each would stop the game between
            -- the halves of a single ambush - and the camera move for whatever is still waiting
            -- is a script action, which does not run while paused.
            if PAUSE_ON_SUMMON and EEex_Read32(address + OFF_FLAG) == 0 then
                pauseGame()
            end
        end
        return
    end

    if EEex_Read32(address + OFF_FLAG) == 0 then
        return
    end

    local resref  = EEex_ReadString(address + OFF_RESREF)
    local amount  = EEex_Read32(address + OFF_AMOUNT)
    local target  = EEex_Read32(address + OFF_TARGET)
    local message = EEex_ReadString(address + OFF_MESSAGE)

    -- Clear the flag *before* spawning. If CreateCreature throws (bad ResRef,
    -- no area loaded, ...) the mailbox has to end up idle anyway, otherwise one
    -- bad request wedges the bridge for the rest of the session.
    EEex_Write32(address + OFF_FLAG, 0)

    EEex_FunctionLog(string.format("consuming request '%s' x%d at party slot %d", resref, amount, target))

    -- Prefixed, and always by us rather than by the sender: it marks the line as coming from
    -- a viewer, so nobody can type something that passes for the game's own feedback.
    if message ~= "" then
        Infinity_DisplayString("[Twitch] " .. message)
    end

    if resref ~= "" then
        -- Move first, spawn later: see moveViewToPartyMember for why the two cannot happen in
        -- the same tick.
        moveViewToPartyMember(target)
        pendingSpawn = { resref = resref, amount = amount, ticks = SPAWN_DELAY_TICKS }
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
