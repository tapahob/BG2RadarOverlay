-------------------------------------------------------------------------------
-- BG2 Radar Overlay - spawn bridge (requires EEex)
--
-- Lets the radar overlay, which runs as a separate process, ask the game to
-- spawn a creature - the game-side half of the Twitch channel-points feature.
--
-- The overlay never calls into the engine itself. It only writes a request into
-- the mailbox buffer allocated below; this script, running on the game's own
-- thread during AI processing, performs the actual spawn at a safe point in the
-- frame. That split is the whole point of doing it this way: an external thread
-- poking engine structures directly is what crashes games and corrupts saves.
--
-- The overlay finds the mailbox by scanning the game's heap for the magic. The
-- handshake deliberately runs in that direction - an external process can probe
-- unmapped memory harmlessly (ReadProcessMemory just returns false), whereas a
-- bad read from in here would take the game down with it.
--
-- Install: copy this file into the game's override folder. EEex must be
-- installed too (the EEex_* functions below come from its own Lua files in
-- override - if those are missing, this file errors out on load).
-------------------------------------------------------------------------------

-- Mailbox layout (0x24 bytes):
--   0x00  u32       MAGIC0
--   0x04  u32       MAGIC1
--   0x08  u32       layout version
--   0x0C  u32       low 32 bits of this struct's own address
--   0x10  u32       request flag: 0 = idle, 1 = spawn pending
--   0x14  char[16]  creature ResRef, null-terminated
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
local VERSION     = 2
local SIZE        = 0x24
local OFF_MAGIC1  = 0x04
local OFF_VERSION = 0x08
local OFF_SELF    = 0x0C
local OFF_FLAG    = 0x10
local OFF_RESREF  = 0x14

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

local function allocMailbox()

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

local function freeMailbox()

    local address = BG2RDR_Mailbox
    if address == nil then
        return
    end

    -- Clear the magic before freeing. Otherwise the freed block keeps looking
    -- like a live mailbox until something reuses it, and the overlay could bind
    -- to this stale copy instead of the one the next game load allocates.
    EEex_Write32(address, 0)
    EEex_Write32(address + OFF_MAGIC1, 0)

    EEex_Free(address)
    BG2RDR_Mailbox = nil
end

local function spawn(resref)

    -- C is the engine's console table (C:CreateCreature). Older builds expose it
    -- as CLUAConsole; check both rather than assuming, since a missing table
    -- would otherwise surface as an unexplained silent no-op.
    local console = C or CLUAConsole
    if console == nil then
        EEex_FunctionLog("no console table (C / CLUAConsole) - cannot spawn")
        return
    end

    console:CreateCreature(resref)
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

    -- Clear the flag *before* spawning. If CreateCreature throws (bad ResRef,
    -- no area loaded, ...) the mailbox has to end up idle anyway, otherwise one
    -- bad request wedges the bridge for the rest of the session.
    EEex_Write32(address + OFF_FLAG, 0)

    EEex_FunctionLog(string.format("consuming request '%s'", resref))

    if resref ~= "" then
        spawn(resref)
    end
end

-- Allocated per game load rather than once at startup: EEex_Malloc'd memory
-- doesn't survive a reload, and the overlay re-scans whenever the magic stops
-- matching, so a fresh address each load is fine.
EEex_GameState_AddInitializedListener(allocMailbox)
EEex_GameState_AddDestroyedListener(freeMailbox)

-- Logged at file scope so the log tells apart "this file never loaded" from "it loaded but
-- the listeners never fired" - the two failure modes look identical from the overlay side.
EEex_FunctionLog("spawn bridge loaded")

-- Fires during AI processing, which means it only runs while a game is actually
-- being played - exactly when spawning is meaningful. It fires once per
-- scripting object, so more than once per tick, but the common path here is a
-- single u32 read and isn't worth throttling.
--
-- Rate limiting is the overlay's job. The mailbox holding one request at a time
-- gives a crude backstop for free: a new request can't be posted until the
-- previous one has been consumed.
EEex_AIBase_AddScriptingObjectUpdatedListener(poll)
