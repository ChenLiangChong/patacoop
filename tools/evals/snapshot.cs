// One line of battle state for tools/compare: phase, each player's army (mean x of its units), enemies by unique id, gimmick hit points
var g = P2.Game.Game.pGame_g; var sb = new System.Text.StringBuilder();
if (g == null || g.getUnitMng() == null) return "none";
sb.Append($"phase={g.gamePhase_.ToString().Replace("GamePhase_", "")} me={PataCoop.Net.CoopNet.MySlot} ms={System.DateTime.Now:HHmmss.fff}");
var troops = g.getUnitMng().unitTroopPtrArray_;
var sum = new float[4]; var cnt = new int[4];
foreach (var sq in troops[0].unitSquadPtrList_) {
  int tag = (int)(sq?.squadInfo_?.squadAddingParam?.rsv1 ?? 0); if ((tag & ~0xFF) != 0x50430000) continue; int p = tag & 0xF; if (p > 3) continue;
  foreach (var u in sq.unitBasePtrList_) { if (u == null || u.isEnd()) continue; var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m == null) continue; sum[p] += m.pos_.x; cnt[p]++; }
}
sb.Append(" armies=");
for (int p = 0; p < 4; p++) sb.Append(cnt[p] > 0 ? $"{sum[p] / cnt[p]:F0}/{cnt[p]}," : "-,");
sb.Append(" enemies=");
// enemies by squad id and place (PataCoop.Coop.UnitIds), the identity hit points and positions are synced by
var placeOf = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.UnitIds").GetMethod("PlaceOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
if (troops.Count > 1) foreach (var sq in troops[1].unitSquadPtrList_) foreach (var u in sq.unitBasePtrList_) { if (u?.info_ == null || u.isEnd()) continue; var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m != null) sb.Append($"{sq.squadInfo_?.uniqueId * 100 + (int)placeOf.Invoke(null, new object[] { u })}:{m.pos_.x:F0}:{u.pActorStatus_?.getHitPoint() ?? -1},"); }
// our troop's units by squad id and place too (their hit points are matched the same way)
sb.Append(" units=");
foreach (var sq in troops[0].unitSquadPtrList_) foreach (var u in sq.unitBasePtrList_) { if (u?.info_ == null || u.isEnd()) continue; sb.Append($"{sq.squadInfo_?.uniqueId * 100 + (int)placeOf.Invoke(null, new object[] { u })}:{u.pActorStatus_?.getHitPoint() ?? -1},"); }
sb.Append(" gimmicks=");
foreach (var gm in g.map_.gimmickManager_.gimmickList_) { if (gm == null || !gm.isEnable_) continue; int hp = gm.status_?.getHitPoint() ?? 0; int max = gm.status_?.info_?.maxHitPoint ?? 0; if (max > 0) sb.Append($"{gm.id_}:{hp},"); }
return sb.ToString();
