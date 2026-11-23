// Drive the real callbacks and inspect their outgoing events without a running game.
// Including the implementation keeps the observers private to the ASI in production.
#include "../CombatEvents.cpp"
#include <filesystem>
#include <fstream>
#include <stdexcept>

namespace {
template<class T> void setField(void* object, size_t offset, T value) {
    std::memcpy(static_cast<Byte*>(object) + offset, &value, sizeof(value));
}

struct Actor {
    Byte sprite[0x600]{}, area[0x700]{}, game[0x6600]{};
    Actor() {
        setField(sprite, 0x18, &area[0]);
        setField(area, 0x228, &game[0]);
        setField(game, 0x6598, &area[0]);
        setField(area, 0x5C8 + 0x80, 800);
        setField(area, 0x5C8 + 0x84, 600);
        setField(sprite, 0xC, 400);
        setField(sprite, 0x10, 300);
    }
};

Stream events{};
int originalCalls, originalResisted;
void originalDisplay(void*, void*, int resisted) {
    ++originalCalls;
    originalResisted = resisted;
}
void require(bool ok, const char* scenario) {
    if (!ok) throw std::runtime_error(scenario);
}

// Optional integration check against a supported game executable mapped as an
// image in this test process. No game code is executed and the file is read-only.
void checkGameImage(const wchar_t* path) {
    HMODULE image = LoadLibraryExW(path, nullptr, DONT_RESOLVE_DLL_REFERENCES);
    require(image != nullptr, "map game image");
    auto base = reinterpret_cast<Byte*>(image);
    require(MH_Initialize() == MH_OK, "initialize hooks");
    require(installCombatHooks(base, true), "install EEex-compatible damage hooks");
    require(apply && displayDamage && hit && !copyEffect && !projectileAdd && !messageAdd,
        "compatible mode observes individual effects without installing critical tracking");
    require(MH_Uninitialize() == MH_OK, "restore game image");
    require(MH_Initialize() == MH_OK, "initialize vanilla hooks");
    require(installCombatHooks(base, false) && copyEffect && projectileAdd && messageAdd,
        "full tracking remains available after restoring the clean image");
    require(MH_Uninitialize() == MH_OK, "restore vanilla hooks");
    FreeLibrary(image);
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 2) checkGameImage(argv[1]);
    namespace fs = std::filesystem;
    auto game = fs::temp_directory_path() / (L"BG2Radar-EEex-test-" + std::to_wstring(GetCurrentProcessId()));
    require(fs::create_directory(game), "create isolated game directory");
    require(!eeexPresent(game.wstring()), "native combat is available in a clean installation");
    std::ofstream(game / "EEex.dll").put(' ');
    require(eeexPresent(game.wstring()), "detect installed EEex before its DLL loads");
    fs::remove(game / "EEex.dll");
    fs::create_directory(game / "override");
    auto radarScript = game / "override" / "M_RDRCRT.lua";
    std::ofstream(radarScript).put(' ');
    require(!eeexPresent(game.wstring()), "Radar's own script does not indicate EEex");
    auto eeexScript = game / "override" / "M___EEex.lua";
    std::ofstream(eeexScript).put(' ');
    require(eeexPresent(game.wstring()), "detect EEex by its override script alone");
    fs::remove(eeexScript);
    fs::remove(radarScript);
    fs::remove(game / "override");
    fs::remove(game);

    Actor actor;
    Byte effect[0x158]{};
    currentStream.store(&events);
    displayDamage = originalDisplay;
    // Roll 11, resist 3. The game logs 8, then clamps the victim's 4 HP to zero.
    apply = [](void* e, void* target) {
        setField(e, 0x1C, 8);
        onDisplayDamage(e, target, 3);
        setField(target, 0x57C, int16_t(0));
        return 1;
    };
    setField(actor.sprite, 0x57C, int16_t(4));
    setField(effect, 0x1C, 11);
    record(effect, true);
    require(onApply(effect, actor.sprite) == 1 && originalCalls == 1 && originalResisted == 3,
        "forward original feedback and effect result");
    require(events.cursor == 1 && events.slots[0].event.damage == 8 && events.slots[0].event.kind == Critical,
        "lethal critical uses logged damage once");

    // The HP change differs by one. Keep the exact amount even for a tiny elemental hit.
    apply = [](void* e, void* target) {
        setField(e, 0x1C, 2);
        onDisplayDamage(e, target, 0);
        setField(target, 0x57C, int16_t(8));
        return 1;
    };
    setField(actor.sprite, 0x57C, int16_t(9));
    setField(effect, 0x20, 0x40000);
    onApply(effect, actor.sprite);
    require(events.cursor == 2 && events.slots[1].event.damage == 2 && events.slots[1].event.kind == Lightning,
        "one-point discrepancy uses logged elemental damage");

    // An unlogged outer effect causes a logged inner hit on the same actor.
    apply = [](void* e, void* target) {
        if (field<int>(e, 0x1C) == 5) {
            Byte nested[0x158]{};
            setField(nested, 0x1C, 8);
            onApply(nested, target);
            setField(target, 0x57C, int16_t(0));
        } else {
            onDisplayDamage(e, target, 0);
            setField(target, 0x57C, int16_t(5));
        }
        return 1;
    };
    setField(actor.sprite, 0x57C, int16_t(12));
    setField(effect, 0x1C, 5);
    onApply(effect, actor.sprite);
    require(events.cursor == 4 && events.slots[2].event.damage == 8 && events.slots[3].event.damage == 5,
        "silent effect excludes nested HP loss independently of logged amount");

    apply = [](void* e, void* target) {
        setField(e, 0x1C, 0);
        onDisplayDamage(e, target, 6);
        return 1;
    };
    onApply(effect, actor.sprite);
    require(events.cursor == 4 && originalCalls == 4 && originalResisted == 6,
        "fully resisted hit forwards feedback without a damage number");

    // Two components of the same strike arrive before any HP scan. The
    // compatible path has no weapon marks and must retain both amounts/types.
    setField(effect, 0x20, 0x80000);
    setField(effect, 0x1C, 7);
    onDisplayDamage(effect, actor.sprite, 3);
    setField(effect, 0x20, 0x20000);
    setField(effect, 0x1C, 2);
    onDisplayDamage(effect, actor.sprite, 0);
    require(events.cursor == 6 && events.slots[4].event.kind == Fire && events.slots[4].event.damage == 7 &&
        events.slots[5].event.kind == Cold && events.slots[5].event.damage == 2,
        "separate resolved fire and cold components");

    useEEexCriticalMarks = true;
    Byte arrowDamage[sizeof(effect)]{};
    setField(arrowDamage, 0x6C, 0x52435254);
    apply = [](void* e, void* target) {
        onDisplayDamage(e, target, 2);
        return 1;
    };
    setField(arrowDamage, 0x1C, 9);
    onApply(effect, actor.sprite); // An unrelated cold rider lands first.
    onApply(arrowDamage, actor.sprite);
    require(events.cursor == 8 && events.slots[6].event.kind == Cold &&
        events.slots[7].event.kind == Critical && events.slots[7].event.damage == 9,
        "EEex critical mark belongs to the weapon effect and uses resolved damage");
}
