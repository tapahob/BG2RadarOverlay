-- The weapon's base damage effect is built before delivery (including via a
-- projectile). Its extended parameters travel with copies of that effect.
-- Parameter 5 is unused by base weapon damage; respect other mods using it.
if EEex_Sprite_AddAlterBaseWeaponDamageListener and not BG2Radar_CritListenerInstalled then
    EEex_Sprite_AddAlterBaseWeaponDamageListener(function(hit)
        local effect = hit.effect
        if hit.isCritical and effect and effect.m_effectId == 12 and effect.m_effectAmount5 == 0 then
            effect.m_effectAmount5 = 0x52435254 -- "RCRT", shared with CombatEvents.cpp
        end
    end)
    BG2Radar_CritListenerInstalled = true
end
