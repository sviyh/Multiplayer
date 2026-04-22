using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using HarmonyLib;
using Verse;

namespace Multiplayer.Client.Desyncs
{
    // Patches RoomTempTracker.RegenerateEqualizeCells to observe every shuffle call
    // (tick, room.ID, mapId, Ticking/ExecutingCmds/InInterface state, brief stack trace,
    // first 16 cells of the resulting shuffle).
    //
    // Goal: diff host vs client eqregen_{pid}.log to find the call that exists on only
    // one side — that's the upstream Rand disturbance. Stack trace tells us which
    // non-tick caller triggered it.
    //
    // Registered imperatively via TemperatureTraceLogPatch.Postfix on first tick.
    internal static class EqRegenProbeRegen
    {
        private static StreamWriter writer;
        private static readonly StringBuilder sb = new();
        private static int callCount;
        private static bool registered;

        public static void EnsureRegistered()
        {
            if (registered) return;
            registered = true;
            try
            {
                var harmony = Multiplayer.harmony;
                var target = AccessTools.Method(typeof(RoomTempTracker), "RegenerateEqualizeCells");
                if (target == null)
                {
                    Log.Error("[EqRegenProbe] could not resolve RoomTempTracker.RegenerateEqualizeCells");
                    return;
                }
                var postfix = new HarmonyMethod(typeof(EqRegenProbeRegen), nameof(Postfix));
                harmony.Patch(target, postfix: postfix);
                Log.Message($"[EqRegenProbe] registered on {target.DeclaringType?.Name}.{target.Name}");
            }
            catch (Exception e)
            {
                Log.Error($"[EqRegenProbe] register failed: {e}");
            }
        }

        public static void Postfix(RoomTempTracker __instance)
        {
            callCount++;
            EnsureWriter();
            if (writer == null) return;

            var room = __instance.room;
            var map = room?.Map;
            var cells = __instance.EqualizeCellsForReading;

            sb.Clear();
            sb.Append("REGEN n=").Append(callCount)
              .Append(" tick=").Append(Find.TickManager?.TicksGame ?? -1)
              .Append(" r=").Append(room?.ID ?? -1)
              .Append(" mapId=").Append(map?.uniqueID ?? -1)
              .Append(" count=").Append(cells.Count)
              .Append(" ticking=").Append(Multiplayer.Ticking)
              .Append(" execCmds=").Append(Multiplayer.ExecutingCmds)
              .Append(" inInterface=").Append(Multiplayer.InInterface);

            // First 16 cells — same snapshot format as WallEqProbe for easy cross-reference
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

            // Capture a short stack trace — identifies the caller chain.
            // Skip the first frame (this Postfix), fileInfo=false (cheaper, shorter).
            var stack = new StackTrace(1, false);
            int frames = Math.Min(stack.FrameCount, 10);
            sb.Append("\n  stack:");
            for (int i = 0; i < frames; i++)
            {
                var f = stack.GetFrame(i);
                var m = f.GetMethod();
                if (m == null) continue;
                sb.Append("\n    ").Append(m.DeclaringType?.FullName ?? "?").Append(".").Append(m.Name);
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
                string path = Path.Combine(Multiplayer.DesyncsDir, $"eqregen_{pid}.log");
                writer = new StreamWriter(path, append: false) { AutoFlush = false };
                writer.WriteLine($"# eqregen pid={pid} started={DateTime.UtcNow:O}");
                writer.Flush();
            }
            catch (Exception e)
            {
                Log.Error($"[EqRegenProbe] failed to open log: {e}");
                writer = null;
            }
        }
    }
}
