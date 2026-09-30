#if !UNITY_SERVER
using UnityEngine;

namespace Novgov.UI
{
    /// <summary>
    /// Sons d'interface des écrans de menu (2026-09-30, carte de Conquête "avec du son") — une seule
    /// AudioSource 2D persistante, clips générés/chargés UNE fois puis réutilisés. Le HUD tactique
    /// garde son propre AudioSource.PlayClipAtPoint(ProceduralAudioBuilder.CreateXSound(), ...) :
    /// ici, pas de caméra de référence (les menus peuvent s'afficher avant qu'une caméra de partie
    /// n'existe) et pas de nouveau AudioClip alloué à chaque tap.
    /// Voix radio : clips déjà présents dans Assets/Resources/Sounds (utilisés en jeu par les unités).
    /// </summary>
    public static class UiSfx
    {
        public enum Sound { Tap, Select, Tab, Error, Success, RadioRoger, RadioTargetLocked }

        private static AudioSource source;
        private static AudioClip tap, select, tab, error, success, radioRoger, radioTargetLocked;

        public static void Play(Sound sound)
        {
            if (!EnsureSource()) return;
            AudioClip clip = GetClip(sound);
            if (clip == null) return;
            float volume = sound == Sound.RadioRoger || sound == Sound.RadioTargetLocked ? 0.9f : 0.7f;
            source.PlayOneShot(clip, volume);
        }

        private static bool EnsureSource()
        {
            if (source != null) return true;
            var go = new GameObject("UiSfx");
            Object.DontDestroyOnLoad(go);
            source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // son d'interface 2D, indépendant de toute caméra
            return true;
        }

        private static AudioClip GetClip(Sound sound)
        {
            // Comparaisons explicites (== null) plutôt que "??=" : l'opérateur ?? contourne le test
            // de nullité surchargé de UnityEngine.Object.
            switch (sound)
            {
                case Sound.Tap: if (tap == null) tap = ProceduralAudioBuilder.CreateClickSound(); return tap;
                case Sound.Select: if (select == null) select = ProceduralAudioBuilder.CreateTargetConfirmedSound(); return select;
                case Sound.Tab: if (tab == null) tab = ProceduralAudioBuilder.CreateHoverSound(); return tab;
                case Sound.Error: if (error == null) error = ProceduralAudioBuilder.CreateErrorSound(); return error;
                case Sound.Success: if (success == null) success = ProceduralAudioBuilder.CreateVictoryFanfareSound(); return success;
                case Sound.RadioRoger: if (radioRoger == null) radioRoger = Resources.Load<AudioClip>("Sounds/roger_will_do_radio"); return radioRoger;
                case Sound.RadioTargetLocked: if (radioTargetLocked == null) radioTargetLocked = Resources.Load<AudioClip>("Sounds/target_locked_radio_deep"); return radioTargetLocked;
                default: return null;
            }
        }
    }
}
#endif
