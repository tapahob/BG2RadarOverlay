#include <windows.h>
#include <MinHook.h>
#include <intrin.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

namespace {
using Byte = unsigned char;
enum Kind { Normal, Miss, Critical, Fire, Cold, Acid, Lightning, Magic };
// A wrapping producer invalidates the slot before touching it. The reader checks its
// sequence on both sides of the payload read and never holds a lock in the game.
struct Event { uint64_t tick; int actor, kind, damage; float x, y; int reserved; };
struct Slot { LONG64 sequence; Event event; };
struct Stream { LONG64 cursor; Slot slots[128]; };
static_assert(sizeof(Slot) == 40 && offsetof(Stream, slots) == 8);
std::atomic<Stream*> currentStream{};
std::mutex publishLock;

template<class T> T field(const void* object, size_t offset) {
    T value;
    std::memcpy(&value, static_cast<const Byte*>(object) + offset, sizeof(value));
    return value;
}

void publish(void* sprite, Kind kind, int damage) {
    auto stream = currentStream.load();
    if (!stream || !sprite) return;
    auto area = field<Byte*>(sprite, 0x18);
    if (!area) return;
    auto game = field<Byte*>(area, 0x228);
    if (!game) return;
    auto visible = field<Byte>(game, 0x6590);
    if (visible >= 12 || field<void*>(game, 0x6598 + 8 * visible) != area) return;
    auto camera = area + 0x5C8;
    int width = field<int>(camera, 0x80) - field<int>(camera, 0x78);
    int height = field<int>(camera, 0x84) - field<int>(camera, 0x7C);
    if (width <= 0 || height <= 0) return;
    float x = float(field<int>(sprite, 0xC) - field<int>(camera, 0xC0)) / width;
    // The engine places its own floating text 55 world pixels above the actor.
    float y = float(field<int>(sprite, 0x10) - field<int>(sprite, 0x14) - 55 - field<int>(camera, 0xC4)) / height;
    if (x < 0 || x > 1 || y < 0 || y > 1) return;
    std::lock_guard<std::mutex> guard(publishLock);
    auto sequence = stream->cursor + 1;
    auto& slot = stream->slots[(sequence - 1) % 128];
    InterlockedExchange64(&slot.sequence, 0);
    slot.event = {GetTickCount64(), field<int>(sprite, 0x48), kind, damage, x, y, 0};
    InterlockedExchange64(&slot.sequence, sequence);
    InterlockedExchange64(&stream->cursor, sequence);
}

Kind damageKind(uint32_t flags) {
    if (flags & (0x0008 | 0x0200)) return Fire;
    if (flags & (0x0002 | 0x0400)) return Cold;
    if (flags & 0x0001) return Acid;
    if (flags & 0x0004) return Lightning;
    if (flags & 0x0040) return Magic;
    return Normal;
}

using Hit = Byte (*)(void*, void*, void*, int, int*, int);
using Apply = int (*)(void*, void*);
using DisplayDamage = void (*)(void*, void*, int);
using Copy = void* (*)(void*);
using ProjectileAdd = void (*)(void*, void*);
using MessageAdd = short (*)(void*, void*, int);
Hit hit;
Apply apply;
DisplayDamage displayDamage;
Copy copyEffect;
ProjectileAdd projectileAdd;
MessageAdd messageAdd;
void* rangedReturn;
void* meleeReturn;
thread_local bool criticalRoll;

// Match the actual weapon effect (also through projectile copies). An elemental rider
// or another attacker hitting the same victim must never consume its critical roll.
struct Mark { void* effect; int source, target; uint64_t time; };
std::array<Mark, 128> marks{};
size_t nextMark;
std::mutex marksLock;

void record(void* effect, bool critical) {
    if (!effect) return;
    std::lock_guard<std::mutex> guard(marksLock);
    for (auto& mark : marks) if (mark.effect == effect) mark.effect = nullptr;
    if (critical) marks[nextMark++ % marks.size()] = {
        effect, field<int>(effect, 0x110), field<int>(effect, 0x150), GetTickCount64()};
}
bool marked(void* effect, bool consume) {
    std::lock_guard<std::mutex> guard(marksLock);
    for (auto& mark : marks) {
        if (mark.effect != effect) continue;
        bool valid = GetTickCount64() - mark.time < 30000 &&
            mark.source == field<int>(effect, 0x110) && mark.target == field<int>(effect, 0x150);
        if (consume || !valid) mark.effect = nullptr;
        return valid;
    }
    return false;
}
bool useEEexCriticalMarks;
bool criticalDamage(void* effect) {
    // CGameEffectBase starts at +8. Its m_effectAmount5 (+0x64) is set on
    // the base weapon effect by M_RDRCRT.lua, independently of other riders.
    if (useEEexCriticalMarks) return field<uint32_t>(effect, 0x6C) == 0x52435254;
    return marked(effect, true);
}
Byte onHit(void* actor, void* victim, void* item, int attack, int* crit, int left) {
    Byte landed = hit(actor, victim, item, attack, crit, left);
    criticalRoll = landed && crit && *crit;
    if (!landed) publish(victim, Miss, 0);
    return landed;
}
void onProjectileAdd(void* projectile, void* effect) {
    if (_ReturnAddress() == rangedReturn) record(effect, criticalRoll);
    projectileAdd(projectile, effect);
}
short onMessageAdd(void* handler, void* message, int force) {
    if (_ReturnAddress() == meleeReturn && message) record(field<void*>(message, 0x10), criticalRoll);
    return messageAdd(handler, message, force);
}
void* onCopy(void* effect) {
    void* clone = copyEffect(effect);
    record(clone, marked(effect, false));
    return clone;
}

struct DamageScope {
    void* effect;
    void* target;
    DamageScope* parent;
    bool critical;
    bool reported = false;
    int nestedLoss = 0;
};
thread_local DamageScope* activeDamage;

void onDisplayDamage(void* effect, void* target, int resisted) {
    auto scope = activeDamage;
    while (scope && scope->effect != effect) scope = scope->parent;
    if (scope) scope->reported = true;

    // DisplayDamageAmount formats the AMOUNT token from m_effectAmount here.
    // RESISTED and ORIGDMG are separate tokens. The engine can still clamp HP
    // or apply other effects after this call, especially on a killing blow.
    int amount = field<int32_t>(effect, 0x1C);
    if (amount > 0) {
        bool critical = scope ? scope->critical : criticalDamage(effect);
        publish(target, critical ? Critical : damageKind(field<uint32_t>(effect, 0x20) >> 16), amount);
    }
    displayDamage(effect, target, resisted);
}

int onApply(void* effect, void* target) {
    DamageScope scope{effect, target, activeDamage, criticalDamage(effect)};
    activeDamage = &scope;
    int before = field<int16_t>(target, 0x57C);
    auto kind = scope.critical ? Critical : damageKind(field<uint32_t>(effect, 0x20) >> 16);
    int result = apply(effect, target);
    activeDamage = scope.parent;
    // Silent and set-HP effects can bypass DisplayDamageAmount. Only these
    // use the observed loss, excluding any nested hit on the same target.
    int lost = before - field<int16_t>(target, 0x57C);
    for (auto parent = scope.parent; parent; parent = parent->parent) {
        if (parent->target == target) { parent->nestedLoss += lost; break; }
    }
    lost -= scope.nestedLoss;
    if (!scope.reported && before > 0 && lost > 0) publish(target, kind, lost);
    return result;
}

struct Build { int hit, apply, displayDamage, projectile, message, copy, swing; };
// EEex function_names.db for the x64 Enhanced Editions. Check both the function
// entries and the two weapon-effect call sites before enabling a build.
constexpr Build builds[] = {
    {0x39DA50,0x1ACF40,0x1E2FB0,0x22A010,0x204C60,0x155EF0,0x3B7FD0}, // BG / BG2 2.6.6
    {0x39DA90,0x1ACFD0,0x1E3040,0x22A0A0,0x204CF0,0x155F80,0x3B8010}, // IWD 2.6.6
    {0x39E090,0x1AD150,0x1E31C0,0x22A270,0x204EC0,0x156100,0x3B8610}, // BG / BG2 2.7.3
    {0x39E0C0,0x1AD1D0,0x1E3240,0x22A2F0,0x204F40,0x156180,0x3B8640}  // IWD 2.7.3
};
bool calls(Byte* after, Byte* fn) {
    return after[-5] == 0xE8 && after + field<int32_t>(after - 4, 0) == fn;
}
template<class Fn> bool queueHook(Byte* address, Fn replacement, Fn& original) {
    return MH_CreateHook(address, reinterpret_cast<void*>(replacement),
        reinterpret_cast<void**>(&original)) == MH_OK && MH_QueueEnableHook(address) == MH_OK;
}

bool eeexPresent(const std::wstring& gameDirectory) {
    // EEex locates and patches some of the same entry points. Its DLL can still
    // be waiting to load when InitializeASI runs. Leave those bytes intact.
    if (GetModuleHandleW(L"EEex.dll") ||
        GetFileAttributesW((gameDirectory + L"\\EEex.dll").c_str()) != INVALID_FILE_ATTRIBUTES) return true;

    // Match the override marker used by the companion installer as well.
    WIN32_FIND_DATAW entry{};
    HANDLE search = FindFirstFileW((gameDirectory + L"\\override\\M*EEex*.lua").c_str(), &entry);
    if (search == INVALID_HANDLE_VALUE) return false;
    bool found = false;
    do {
        const wchar_t* extension = wcsrchr(entry.cFileName, L'.');
        if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && extension && _wcsicmp(extension, L".lua") == 0) {
            found = true;
            break;
        }
    } while (FindNextFileW(search, &entry));
    FindClose(search);
    return found;
}

bool installCombatHooks(Byte* base, bool withEEex) {
    useEEexCriticalMarks = withEEex;
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    for (auto& b : builds) {
        if (nt->OptionalHeader.SizeOfCode < DWORD(b.swing + 0xD02)) continue;
        auto ranged = base + b.swing + 0x8E8, melee = base + b.swing + 0xD01;
        if (std::memcmp(base + b.apply, "\x40\x55\x53\x41\x54\x41\x56\x48", 8) ||
            std::memcmp(base + b.displayDamage, "\x40\x55\x56\x41\x55\x41\x56\x41", 8)) continue;
        bool hitAvailable = std::memcmp(base + b.hit, "\x48\x89\x5c\x24\x20\x4c\x89\x44", 8) == 0;
        // EEex itself intercepts AddEffect. Resolved damage and misses are
        // independent of these hooks used to follow a critical weapon effect.
        if (!withEEex && (!hitAvailable || !calls(ranged, base + b.projectile) || !calls(melee, base + b.message) ||
            std::memcmp(base + b.copy, "\x48\x89\x5c\x24\x08\x48\x89\x6c", 8) ||
            std::memcmp(base + b.projectile, "\x0f\xb7\x41\x60\x48\x83\xc1\x78", 8) ||
            std::memcmp(base + b.message, "\x48\x89\x5c\x24\x08\x48\x89\x74", 8))) continue;
        if (!queueHook(base + b.apply, onApply, apply) ||
            !queueHook(base + b.displayDamage, onDisplayDamage, displayDamage) ||
            (hitAvailable && !queueHook(base + b.hit, onHit, hit))) return false;
        if (!withEEex) {
            rangedReturn = ranged;
            meleeReturn = melee;
            if (!queueHook(base + b.copy, onCopy, copyEffect) ||
                !queueHook(base + b.projectile, onProjectileAdd, projectileAdd) ||
                !queueHook(base + b.message, onMessageAdd, messageAdd)) return false;
        }
        return MH_ApplyQueued() == MH_OK;
    }
    return false;
}
}

void startCombatEvents(const std::wstring& gameDirectory) {
    auto base = reinterpret_cast<Byte*>(GetModuleHandleW(nullptr));
    if (!installCombatHooks(base, eeexPresent(gameDirectory))) return;
    wchar_t name[96];
    swprintf_s(name, L"Local\\BG2Radar.Combat.1.%lu", GetCurrentProcessId());
    HANDLE mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Stream), name);
    if (mapping) {
        auto stream = static_cast<Stream*>(MapViewOfFile(mapping, FILE_MAP_WRITE, 0, 0, sizeof(Stream)));
        currentStream.store(stream);
        // Keep the name available to companions which attach or restart later.
        if (!stream) CloseHandle(mapping);
    }
}
