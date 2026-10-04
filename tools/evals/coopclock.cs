// One line of the co-op clocks: the battle clock (PataCoop.Coop.BattleClock), the raw beat timer, the drum mode,
// how many catch-up steps ran (Clock.Caught), the weather and the mood.
var asm = typeof(PataCoop.CoopPlugin).Assembly; var nf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
long ticks = (long)asm.GetType("PataCoop.Coop.BattleClock").GetProperty("Ticks", nf).GetValue(null);
int caught = (int)asm.GetType("PataCoop.Coop.Clock").GetField("Caught", nf).GetValue(null);
var g = P2.Game.Game.pGame_g; if (g == null) return $"[{Instance}] no game";
var d = g.soundDirector_; var bt = d.getBeatTimer(); var w = g.map_?.weatherController_?.currentParam_;
return $"[{Instance}] {System.DateTime.Now:HH:mm:ss.f} clock {ticks / 80} steps (raw tick {bt.tick_}, hb {bt.halfBeatCount_}) mode {d.beatCommander_.getMode().ToString().Replace("Mode_", "")} mood {d.beatCommander_.evaluator_.bgmMood_.ToString().Replace("Mood_", "")} caught {caught} | weather {w?.currentWeather} rain {w?.rainyLevel} wind {w?.windLevel} | base {(int)g.getUnitMng().unitTroopPtrArray_[0].troopBasePos_[0]}";
