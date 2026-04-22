using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using Multiplayer.Client.AsyncTime;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    // Per-tick temperature snapshot log. Opt-in via env var MP_TEMP_TRACE=1.
    // Writes to MpDesyncs/temp_trace_{pid}.log so two instances on one machine don't clobber each other.
    // Diff the two files after a reproducible desync to find the first tick where roomsHash differs.
    [HarmonyPatch(typeof(AsyncTimeComp), nameof(AsyncTimeComp.Tick))]
    static class TemperatureTraceLogPatch
    {
        // Flipped to true for the active investigation. Revert to false (or delete this whole file)
        // once the divergent-tick root cause is identified.
        private static readonly bool Enabled = true;

        private static StreamWriter writer;
        private static readonly StringBuilder lineBuilder = new();

        static void Postfix(AsyncTimeComp __instance)
        {
            if (!Enabled) return;

            WriteSentinel("A_temp_trace_postfix_entered");
            WallEqProbeEqualize.EnsureRegistered();
            EqRegenProbeRegen.EnsureRegistered();
            WriteSentinel("D_after_ensure_registered");

            var map = __instance.map;
            if (map?.regionGrid == null) return;

            EnsureWriter();
            if (writer == null) return;

            int roomsHash = 17;
            int roomCount = 0;
            int targetCells = -1;
            float targetTemp = float.NaN;

            foreach (var room in map.regionGrid.AllRooms)
            {
                if (room == null || room.Dereferenced) continue;

                int tempBits = BitConverter.SingleToInt32Bits(room.Temperature);
                roomsHash = Gen.HashCombineInt(roomsHash, room.ID, tempBits, room.CellCount);
                roomCount++;

                if (room.ID == 45)
                {
                    targetCells = room.CellCount;
                    targetTemp = room.Temperature;
                }
            }

            lineBuilder.Clear();
            lineBuilder.Append("tick=").Append(__instance.mapTicks)
                .Append(" mapId=").Append(map.uniqueID)
                .Append(" rooms=").Append(roomCount)
                .Append(" roomsHash=0x").Append(roomsHash.ToString("X8"));

            if (targetCells >= 0)
                lineBuilder.Append(" r45.T=").Append(targetTemp.ToString("G9"))
                    .Append(" r45.cells=").Append(targetCells);

            writer.WriteLine(lineBuilder.ToString());

            // Flush every tick. <20s sessions × 1 map = <1200 writes, cheap and survives hard crashes.
            writer.Flush();
        }

        internal static void WriteSentinel(string tag)
        {
            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                int pid = Process.GetCurrentProcess().Id;
                string path = Path.Combine(Multiplayer.DesyncsDir, $"sentinel_{pid}_{tag}.txt");
                if (!File.Exists(path))
                    File.WriteAllText(path, $"{tag} {DateTime.UtcNow:O}\n");
            }
            catch { }
        }

        private static void EnsureWriter()
        {
            if (writer != null) return;

            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                int pid = Process.GetCurrentProcess().Id;
                string path = Path.Combine(Multiplayer.DesyncsDir, $"temp_trace_{pid}.log");
                writer = new StreamWriter(path, append: false) { AutoFlush = false };
                writer.WriteLine($"# temp_trace pid={pid} started={DateTime.UtcNow:O}");
            }
            catch (Exception e)
            {
                Log.Error($"[TemperatureTraceLog] failed to open log: {e}");
                writer = null;
            }
        }
    }
}
