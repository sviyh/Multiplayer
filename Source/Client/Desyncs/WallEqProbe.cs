using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    // Patches RoomTempTracker.EqualizeTemperature to observe equalizeCells ordering
    // + cycleIndex movement for the target room. Writes to MpDesyncs/eqshuffle_{pid}.log.
    //
    // Registered imperatively via TemperatureTraceLogPatch.Postfix (once) — the
    // attribute-based + StaticConstructorOnStartup approaches both silently failed
    // to apply against RoomTempTracker on this build.
    internal static class WallEqProbeEqualize
    {
        private const int TargetRoomID = 45;

        private static StreamWriter writer;
        private static readonly StringBuilder sb = new();
        private static int callCount;
        private static bool registered;

        [ThreadStatic] private static int prefixCycleIndex;
        [ThreadStatic] private static int prefixCellCount;
        [ThreadStatic] private static float prefixRoomTemp;
        [ThreadStatic] private static bool prefixCaptured;

        public static void EnsureRegistered()
        {
            TemperatureTraceLogPatch.WriteSentinel("B_ensure_registered_entered");
            if (registered) return;
            registered = true;
            try
            {
                TemperatureTraceLogPatch.WriteSentinel("C1_before_harmony_patch");
                var harmony = Multiplayer.harmony;
                var target = AccessTools.Method(typeof(RoomTempTracker), "EqualizeTemperature");
                if (target == null)
                {
                    TemperatureTraceLogPatch.WriteSentinel("C2_target_null");
                    Log.Error("[WallEqProbe] register: could not resolve RoomTempTracker.EqualizeTemperature");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(WallEqProbeEqualize), nameof(Prefix));
                var postfix = new HarmonyMethod(typeof(WallEqProbeEqualize), nameof(Postfix));
                harmony.Patch(target, prefix: prefix, postfix: postfix);

                TemperatureTraceLogPatch.WriteSentinel("C3_harmony_patch_applied");
                Log.Message($"[WallEqProbe] register applied patch on {target.DeclaringType?.Name}.{target.Name}");
            }
            catch (Exception e)
            {
                TemperatureTraceLogPatch.WriteSentinel("C4_exception_" + e.GetType().Name);
                try { File.WriteAllText(Path.Combine(Multiplayer.DesyncsDir, $"sentinel_exception_{Process.GetCurrentProcess().Id}.txt"), e.ToString()); } catch { }
                Log.Error($"[WallEqProbe] register failed: {e}");
            }
        }

        public static void Prefix(RoomTempTracker __instance)
        {
            callCount++;
            if (callCount <= 3)
                Log.Message($"[WallEqProbe] Prefix call #{callCount} roomId={__instance.room?.ID}");

            var room = __instance.room;
            if (room == null || room.ID != TargetRoomID) return;

            prefixCycleIndex = __instance.cycleIndex;
            prefixCellCount = __instance.EqualizeCellsForReading.Count;
            prefixRoomTemp = room.Temperature;
            prefixCaptured = true;
        }

        public static void Postfix(RoomTempTracker __instance)
        {
            if (!prefixCaptured) return;
            prefixCaptured = false;

            var room = __instance.room;
            if (room == null) return;

            EnsureWriter();
            if (writer == null) return;

            var cells = __instance.EqualizeCellsForReading;
            int ciBefore = prefixCycleIndex;
            int ciAfter = __instance.cycleIndex;
            var map = room.Map;

            sb.Clear();
            sb.Append("EQ tick=").Append(Find.TickManager?.TicksGame ?? -1)
              .Append(" r=").Append(room.ID)
              .Append(" mapId=").Append(map?.uniqueID ?? -1)
              .Append(" ci=").Append(ciBefore).Append("->").Append(ciAfter)
              .Append(" roomT=").Append(prefixRoomTemp.ToString("G9"))
              .Append("->").Append(room.Temperature.ToString("G9"))
              .Append(" cellsCount=").Append(prefixCellCount).Append("->").Append(cells.Count);

            sb.Append(" cells=[");
            int n = Math.Min(cells.Count, 16);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                var c = cells[i];
                sb.Append('(').Append(c.x).Append(',').Append(c.z).Append(')');
            }
            if (cells.Count > n) sb.Append(",...");
            sb.Append(']');

            if (cells.Count > 0 && ciAfter > ciBefore)
            {
                for (int ci = ciBefore + 1; ci <= ciAfter; ci++)
                {
                    int idx = ci % cells.Count;
                    var cell = cells[idx];
                    bool direct = GenTemperature.TryGetDirectAirTemperatureForCell(cell, map, out var temp);
                    sb.Append("\n  s[").Append(ci).Append("] idx=").Append(idx)
                      .Append(" cell=(").Append(cell.x).Append(',').Append(cell.z).Append(')')
                      .Append(" direct=").Append(direct)
                      .Append(" t=").Append(temp.ToString("G9"));
                }
            }

            writer.WriteLine(sb.ToString());
            writer.Flush();
        }

        private static void EnsureWriter()
        {
            if (writer != null) return;
            try
            {
                Directory.CreateDirectory(Multiplayer.DesyncsDir);
                int pid = Process.GetCurrentProcess().Id;
                string path = Path.Combine(Multiplayer.DesyncsDir, $"eqshuffle_{pid}.log");
                writer = new StreamWriter(path, append: false) { AutoFlush = false };
                writer.WriteLine($"# eqshuffle pid={pid} started={DateTime.UtcNow:O} targetRoom={TargetRoomID}");
                writer.Flush();
            }
            catch (Exception e)
            {
                Log.Error($"[WallEqProbe] failed to open log: {e}");
                writer = null;
            }
        }
    }
}
