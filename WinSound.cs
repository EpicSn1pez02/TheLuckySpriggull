using System;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// Sounds for a win, played by every game for itself: the game's quest-complete jingle when the reels land
    /// on a win, and the game's own firework bang for each firework burst.
    /// </summary>
    internal static class WinSound
    {
        /// <summary>Plays the win jingle at the machine.</summary>
        public static void Play(Component at)
        {
            if (ApplicationManager.IsHeadless) return;
            try { SoundEffect.QuestCompleted.Resolve(at.gameObject)?.PlayAtObject(at); }
            catch (Exception e) { MelonLogger.Warning("[The Lucky Spriggull] Couldn't play the win sound: " + e.Message); }
        }

        /// <summary>The game's own firework bang, for each burst.</summary>
        public static void PlayFirework(GameObject context, Vector3 at)
        {
            if (ApplicationManager.IsHeadless) return;
            try { SoundEffect.FireworkExplosion.Resolve(context)?.PlayAtPosition(at); }
            catch (Exception e) { MelonLogger.Warning("[The Lucky Spriggull] Couldn't play the firework sound: " + e.Message); }
        }
    }
}
