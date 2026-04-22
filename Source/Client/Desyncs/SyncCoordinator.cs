using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Multiplayer.Client.Desyncs;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using Verse;

namespace Multiplayer.Client
{
    // Per-room cumulative PushHeat accumulator. Hooks Room.PushHeat to sum every heat
    // push that lands in each room across the whole session. Trace lines emit the lifetime
    // total + last-push tick + last-push energy, so divergence shows up even when the push
    // happened many ticks before the trace window (CompHeatPusher uses TickerType.Rare).
    // If host and local cumulative totals differ for the same room → PushHeat path diverged.
    // If totals match but RT still differs → divergence is in RoomTempTracker.EqualizeTemperature.
    internal static class RoomHeatProbe
    {
        private static Dictionary<int, double> cumulativeHeat = new();
        private static Dictionary<int, int> lastPushTick = new();
        private static Dictionary<int, float> lastPushEnergy = new();

        [MpPostfix(typeof(Room), nameof(Room.PushHeat), new[] { typeof(float) })]
        private static void Postfix(Room __instance, float energy, bool __result)
        {
            if (!__result) return;
            int id = __instance.ID;
            cumulativeHeat.TryGetValue(id, out double cum);
            cumulativeHeat[id] = cum + energy;
            lastPushTick[id] = Find.TickManager.ticksGameInt;
            lastPushEnergy[id] = energy;
        }

        public static double GetCumulative(int roomID)
            => cumulativeHeat.TryGetValue(roomID, out double v) ? v : 0d;

        public static int GetLastPushTick(int roomID)
            => lastPushTick.TryGetValue(roomID, out int v) ? v : -1;

        public static float GetLastPushEnergy(int roomID)
            => lastPushEnergy.TryGetValue(roomID, out float v) ? v : 0f;
    }
}

namespace Multiplayer.Client
{
    public class SyncCoordinator
    {
        public bool ShouldCollect => !Multiplayer.IsReplay;

        private ClientSyncOpinion OpinionInBuilding =>
            currentOpinion ??= new ClientSyncOpinion(TickPatch.Timer)
            {
                isLocalClientsOpinion = true
            };

        // Contains both local and remote opinions. The first opinion is the oldest one, and the last is the newest one.
        // The host player has only local opinions.
        public readonly List<ClientSyncOpinion> knownClientOpinions = [];

        private ClientSyncOpinion currentOpinion;

        public int lastValidTick = -1;
        public bool arbiterWasPlayingOnLastValidTick;

        private const int MaxBacklog = 30;

        public ClientSyncOpinion FinishLocalOpinion()
        {
            if (!ShouldCollect || currentOpinion == null) return null;
            currentOpinion.roundMode = RoundMode.GetCurrentRoundMode();
            var opinion = currentOpinion;
            currentOpinion = null;
            return opinion;
        }

        /// <summary>
        /// Adds a client opinion to the <see cref="knownClientOpinions"/> list and checks that it matches the most recent currently in there. If not, a desync event is fired.
        /// </summary>
        /// <param name="newOpinion">The <see cref="ClientSyncOpinion"/> to add and check.</param>
        public void AddClientOpinionAndCheckDesync(ClientSyncOpinion newOpinion)
        {
            //If we've already desynced, don't even bother
            if (Multiplayer.session.desynced) return;

            //If this is the first client opinion we have nothing to compare it with, so just add it
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions[0].isLocalClientsOpinion == newOpinion.isLocalClientsOpinion)
            {
                knownClientOpinions.Add(newOpinion);
                if (knownClientOpinions.Count > MaxBacklog)
                    RemoveAndClearFirst();
                return;
            }

            // Remove all opinions that started before this one, as it's the most up-to-date one
            while (knownClientOpinions.Count > 0 && knownClientOpinions[0].startTick < newOpinion.startTick)
                RemoveAndClearFirst();

            // If there are none left, we don't need to compare this new one
            if (knownClientOpinions.Count == 0)
            {
                knownClientOpinions.Add(newOpinion);
                return;
            }

            if (knownClientOpinions.First().startTick != newOpinion.startTick)
            {
                // Ignore this opinion
                newOpinion.Clear();
                return;
            }

            // If these two contain the same tick range - i.e., they start at the same time, because they should
            // continue to the current tick, then do a comparison.
            var oldOpinion = knownClientOpinions.RemoveFirst();

            // Actually do the comparison to find any desync
            var desyncMessage = oldOpinion.CheckForDesync(newOpinion);

            if (desyncMessage != null)
            {
                MpLog.Log($"Desynced after last valid tick {lastValidTick}: {desyncMessage}");
                Multiplayer.session.desynced = true;
                TickPatch.ClearSimulating();
                OnMainThread.Enqueue(() => HandleDesync(oldOpinion, newOpinion, desyncMessage));
            }
            else
            {
                // Update fields
                lastValidTick = oldOpinion.startTick;
                arbiterWasPlayingOnLastValidTick = Multiplayer.session.ArbiterPlaying;

                // Return inner data to the pool
                oldOpinion.Clear();
            }
        }

        private void RemoveAndClearFirst()
        {
            var opinion = knownClientOpinions.RemoveFirst();
            opinion.Clear();
        }

        /// <summary>
        /// Called by <see cref="AddClientOpinionAndCheckDesync"/> if the newly added opinion doesn't match with what other ones.
        /// </summary>
        /// <param name="oldOpinion">The first up-to-date client opinion present in <see cref="knownClientOpinions"/>, that disagreed with the new one</param>
        /// <param name="newOpinion">The opinion passed to <see cref="AddClientOpinionAndCheckDesync"/> that disagreed with the currently known opinions.</param>
        /// <param name="desyncMessage">The error message that explains exactly what desynced.</param>
        private void HandleDesync(ClientSyncOpinion oldOpinion, ClientSyncOpinion newOpinion, string desyncMessage)
        {
            // Identify which of the two sync infos is local and which is the remote.
            var local = oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;
            var remote = !oldOpinion.isLocalClientsOpinion ? oldOpinion : newOpinion;

            var diffAt = FindTraceHashesDiffTick(local, remote, out var found);
            Multiplayer.Client.Send(new ClientDesyncedPacket(local.startTick, diffAt));
            Multiplayer.session.desyncTracesFromHost = null;

            MpUI.ClearWindowStack();
            Find.WindowStack.Add(new DesyncedWindow(
                desyncMessage,
                new SaveableDesyncInfo(this, local, remote, diffAt, found)
            ));
        }

        private static int FindTraceHashesDiffTick(ClientSyncOpinion local, ClientSyncOpinion remote, out bool found)
        {
            found = true;
            //Find the length of whichever stack trace is shorter.
            var localCount = local.desyncStackTraceHashes.Count;
            var remoteCount = remote.desyncStackTraceHashes.Count;
            int count = Math.Min(localCount, remoteCount);

            //Find the point at which the hashes differ - this is where the desync occurred.
            for (int i = 0; i < count; i++)
                if (local.desyncStackTraceHashes[i] != remote.desyncStackTraceHashes[i])
                    return i;

            found = false;
            if (localCount != remoteCount)
                return count - 1;

            return -1;
        }

        /// <summary>
        /// Adds a random state to the commandRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddCommandRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.commandRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the worldRandomStates list
        /// </summary>
        /// <param name="state">The state to add</param>
        public void TryAddWorldRandomState(ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.worldRandomStates.Add((uint) (state >> 32));
        }

        /// <summary>
        /// Adds a random state to the list of the map random state handler for the map with the given id
        /// </summary>
        /// <param name="map">The map id to add the state to</param>
        /// <param name="state">The state to add</param>
        public void TryAddMapRandomState(int map, ulong state)
        {
            if (!ShouldCollect) return;
            OpinionInBuilding.TryMarkSimulating();
            OpinionInBuilding.GetRandomStatesForMap(map).Add((uint) (state >> 32));
        }

        /// <summary>
        /// Logs an item to aid in desync debugging.
        /// </summary>
        /// <param name="info1">Information to be logged</param>
        /// <param name="info2">Information to be logged</param>
        public void TryAddInfoForDesyncLog(string info1, string info2)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            int hash = Gen.HashCombineInt(info1.GetHashCode(), info2.GetHashCode());

            OpinionInBuilding.desyncStackTraces.Add(new StackTraceLogItemObj {
                tick = TickPatch.Timer,
                hash = hash,
                info1 = info1,
                info2 = info2,
            });

            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        public void TryAddStackTraceForDesyncLogRaw(StackTraceLogItemRaw item, int depth, int hashIn, string moreInfo = null)
        {
            if (!ShouldCollect) return;

            OpinionInBuilding.TryMarkSimulating();

            item.depth = depth;
            item.ticksGame = Find.TickManager.ticksGameInt;
            item.rngState = Rand.StateCompressed;
            item.tick = TickPatch.Timer;
            item.factionName = Faction.OfPlayer?.Name ?? string.Empty;
            item.moreInfo = moreInfo;

            var thing = ThingContext.Current;
            if (thing != null)
            {
                item.thingDef = thing.def;
                item.thingId = thing.thingIDNumber;

                if (thing is Pawn pawn && pawn.Spawned)
                {
                    try
                    {
                        item.moreInfo = $"{moreInfo}{BuildPawnDiag(pawn)}";
                    }
                    catch (Exception e)
                    {
                        item.moreInfo = $"{moreInfo}ERR:{e.GetType().Name}";
                    }
                }
            }

            var hash = Gen.HashCombineInt(hashIn, depth, (int)(item.rngState >> 32), (int)item.rngState);
            item.hash = hash;

            OpinionInBuilding.desyncStackTraces.Add(item);

            // Track & network trace hash, for comparison with other opinions.
            OpinionInBuilding.desyncStackTraceHashes.Add(hash);
        }

        // Per-pawn diagnostic line for desync traces. All vanilla heat inputs have been proven
        // identical between host and client, yet indoor Room.Temperature still drifts.
        // This round probes room geometry (cell/roof counts) and modded heat-pushers hiding in
        // the room's thing set — the two remaining hypotheses.
        private static string BuildPawnDiag(Pawn pawn)
        {
            var sb = new StringBuilder();
            sb.Append($"T={pawn.AmbientTemperature:F4}");
            sb.Append($" J={pawn.CurJobDef?.defName ?? "-"}");
            sb.Append($" TL={pawn.jobs?.curDriver?.ticksLeftThisToil ?? -1}");

            var room = pawn.GetRoom();
            if (room != null)
            {
                sb.Append($" rID={room.ID}");
                sb.Append($" RRole={room.Role?.defName ?? "-"}");
                sb.Append($" RC={room.CellCount}");
                sb.Append($" RR={room.OpenRoofCount}");
                sb.Append($" RX={(room.UsesOutdoorTemperature ? 1 : 0)}");
                sb.Append($" RT={room.Temperature:F4}");
                sb.Append($" RPH={RoomHeatProbe.GetCumulative(room.ID):F4}@{RoomHeatProbe.GetLastPushTick(room.ID)}={RoomHeatProbe.GetLastPushEnergy(room.ID):F4}");

                // Hash thing defs inside the room — catches modded heat-pushers we're not naming.
                int thingsHash = 17;
                int thingsCount = 0;
                foreach (var t in room.ContainedAndAdjacentThings)
                {
                    thingsHash = Gen.HashCombineInt(thingsHash, t.def?.shortHash ?? 0);
                    thingsCount++;
                }
                sb.Append($" RTH={thingsCount}/{thingsHash:X}");
            }
            else
            {
                sb.Append(" rID=-1");
            }

            var map = pawn.Map;
            if (map == null) return sb.ToString();

            sb.Append($" FC={map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count}");

            var wm = map.weatherManager;
            if (wm != null)
                sb.Append($" W={wm.curWeather?.defName ?? "-"}@{wm.curWeatherAge}");

            sb.Append($" OT={map.mapTemperature.OutdoorTemp:F4}");

            int batteryCount = 0;
            float batterySum = 0f;
            var buildings = map.listerBuildings.allBuildingsColonist;
            for (int b = 0; b < buildings.Count; b++)
            {
                var bat = buildings[b].TryGetComp<CompPowerBattery>();
                if (bat != null)
                {
                    batteryCount++;
                    batterySum += bat.StoredEnergy;
                }
            }
            sb.Append($" BC={batteryCount} BE={batterySum:F1}");

            return sb.ToString();
        }

        public static string MethodNameWithIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000]
            // rawName = rawName.Substring(0, rawName.LastIndexOf(']') + 1);

            return rawName;
        }

        public static string MethodNameWithoutIL(string rawName)
        {
            // Note: The names currently don't include IL locations so the code is commented out

            // at Verse.AI.JobDriver.ReadyForNextToil () [0x00000] in <c847e073cda54790b59d58357cc8cf98>:0
            // =>
            // at Verse.AI.JobDriver.ReadyForNextToil ()
            // rawName = rawName.Substring(0, rawName.LastIndexOf('['));

            return rawName;
        }
    }
}
