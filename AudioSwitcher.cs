// AudioSwitcher.cs - Smart per-game audio format switcher
//
// Build:
//   csc /target:exe /out:AudioSwitcher.exe /reference:System.Management.dll AudioSwitcher.cs
// or via the included AudioSwitcher.ps1 -Install
//
// Runtime:
//   AudioSwitcher.exe [--foreground] [--verbose] [--config <path>]
//   AudioSwitcher.exe --test-etw         run ETW for 30s and dump events
//   AudioSwitcher.exe --list-devices     list audio endpoints, exit
//   AudioSwitcher.exe --show-state       print learned overrides/crash/glitch logs
//   AudioSwitcher.exe --lock <exe> <tier>  lock a game's profile
//   AudioSwitcher.exe --reset            wipe learned state
//   AudioSwitcher.exe --status           query running daemon via named pipe

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AudioSwitcher
{
    // ====================================================================
    // CONFIGURATION
    // ====================================================================
    public class ProfileTier
    {
        public int Rate { get; set; }
        public int Bits { get; set; }
        public string Name { get; set; } = "";
    }

    public class Config
    {
        public int ConfigVersion { get; set; } = 9;   // bump when built-in defaults change; triggers a regen
        public string TargetDeviceName { get; set; } = "";   // empty = current default playback device
        public bool CheckForUpdates { get; set; } = true;    // check GitHub Releases at startup (notify only)
        public int Channels { get; set; } = 2;
        public int IdleTier { get; set; } = 0;
        public int CrashThresholdSeconds { get; set; } = 25;
        public int GlitchThreshold { get; set; } = 5;
        public int GlitchWindowSeconds { get; set; } = 30;
        // Silence detection: a game whose audio session is Active but whose peak meter stays
        // silent this many seconds (after the grace period, never having produced sound) is
        // treated as "format unusable" and bumped down. ON by default (15s). Safe because the
        // daemon self-verifies the peak meter works (sees it read >0) before ever acting, so a
        // broken meter can't wrongly flag games. 0 = off.
        public int SilenceWindowSeconds { get; set; } = 15;
        public int SilenceGraceSeconds { get; set; } = 25;
        // Auto-learn games: if an EXCLUSIVE-fullscreen Direct3D app (a strong, low-false-positive
        // "this is a game" signal - browsers/video/desktop apps don't use exclusive fullscreen)
        // stays foreground this long and isn't already known, add its exe to GameProcesses so
        // future launches are caught at start. 0 = off. Even a rare false-add is harmless - the
        // app just gets tracked, never altered unless it actually crashes/glitches.
        public int AutoLearnFullscreenSeconds { get; set; } = 15;
        // Upward probe: every Nth launch of a game we've previously dropped, retry ONE tier
        // higher to find the true ceiling / self-heal over-drops. 0 = OFF (default) - a game
        // with a genuine limit will fail on the probe launch, so it's opt-in.
        public int ProbeEveryLaunches { get; set; } = 0;
        // Freeze a known game across the format switch: the instant a game we've already learned (or
        // that's known-quirky) launches, suspend all its threads, change the device format, then
        // resume - so it physically cannot open its audio device at the old rate before the switch
        // lands. Guarantees the switch wins the race (the game isn't running while we change it).
        // ON by default - it's the only thing that reliably beats a game that opens audio the instant
        // it starts. Safe because of SuspendSkipIfAntiCheat below. Uses documented ntdll process
        // suspend - no injection, no hooking. The fast-apply path is always on regardless.
        public bool SuspendDuringSwitch { get; set; } = true;
        // Safety gate for the freeze: skip suspending if a known anti-cheat KERNEL DRIVER is loaded
        // (EAC, BattlEye, Vanguard, ...). Suspending a protected game is the one thing an anti-cheat
        // could read as tampering, so with this on (default) the freeze auto-disables itself for
        // online/anti-cheat titles and only ever fires on offline games - fast-apply still runs for
        // the protected ones. Set false only if you know your game has no anti-cheat and want the
        // freeze unconditionally.
        public bool SuspendSkipIfAntiCheat { get; set; } = true;

        public List<ProfileTier> ProfileTiers { get; set; } = new()
        {
            // Tier ladder, best -> safest. Rate is what fixes the UE/winmm crash; some games
            // instead need lower DEPTH at a given rate (e.g. BF1 = 192/16), so 16-bit is
            // interleaved, not just at the bottom. Formats a given device doesn't support are
            // auto-skipped at apply time (see Daemon.ApplySupported), so this list can be generous.
            new() { Rate = 384000, Bits = 32, Name = "audiophile" },
            new() { Rate = 192000, Bits = 32, Name = "safe-modern" },
            new() { Rate = 192000, Bits = 16, Name = "safe-16" },
            new() { Rate = 96000,  Bits = 32, Name = "safer" },
            new() { Rate = 48000,  Bits = 32, Name = "universal" },
            new() { Rate = 44100,  Bits = 16, Name = "CD" }
        };

        // Starting tier per engine (index into ProfileTiers). Default 0 = max: most games are
        // fine at the device's top format (verified: BeamNG, KCD2), so we DON'T preemptively
        // drop them - only on an observed crash/glitch/silence (learned). UE4/UE5 are the known
        // exception (winmm null-deref at very high rates), so they start one tier down.
        public Dictionary<string, int> EngineDefaults { get; set; } = new()
        {
            ["UE5"]          = 1,
            ["UE4"]          = 1,
            ["Unity"]        = 0,
            ["Source2"]      = 0,
            ["Source"]       = 0,
            ["FMOD"]         = 0,
            ["Wwise"]        = 0,
            ["CryEngine"]    = 0,
            ["idTech7"]      = 0,
            ["RAGE"]         = 0,
            ["REDengine"]    = 0,
            ["Anvil"]        = 0,
            ["Frostbite"]    = 0,
            ["Decima"]       = 0,
            ["Unknown-Game"] = 0
        };

        public Dictionary<string, KnownQuirkyEntry> KnownQuirky { get; set; } = new()
        {
            ["Greylock-Win64-Shipping.exe"] = new()
            {
                Tier = 1,
                Reason = "Echo Point Nova UE5 winmm crash at 384/32 (user-confirmed)"
            }
        };

        public List<string> LauncherProcesses { get; set; } = new()
        {
            "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe",
            "Battle.net.exe", "EADesktop.exe", "Origin.exe",
            "upc.exe", "UbisoftConnect.exe",
            "RiotClientServices.exe", "GamingServices.exe",
            "Amazon Games UI.exe", "Launcher.exe"
        };

        public List<string> GamePathHints { get; set; } = new()
        {
            @"\steamapps\common\", @"\Epic Games\", @"\GOG Galaxy\Games\",
            @"\Riot Games\", @"\EA Games\", @"\EA\", @"\Origin Games\",
            @"\Ubisoft\Ubisoft Game Launcher\games\",
            @"\Battle.net\", @"\Blizzard\", @"\Rockstar Games\",
            // NOT plain \WindowsApps\ - that matches every Store app (Snipping Tool, Calculator...).
            // Game Pass games live under XboxGames or ModifiableWindowsApps.
            @"\XboxGames\", @"\ModifiableWindowsApps\",
            @"\Amazon Games\Library\"
        };

        // Never treated as games even if they match a path/launcher rule. Launcher infrastructure
        // and helper processes (Steam's own web helper / overlay), crash handlers, and common
        // non-game Store apps that live in WindowsApps.
        public List<string> IgnoreProcesses { get; set; } = new()
        {
            "steamwebhelper.exe", "gameoverlayui64.exe", "gameoverlayui.exe",
            "steamerrorreporter.exe", "steamservice.exe", "streaming_client.exe",
            "crashhandler.exe", "crashhandler64.exe", "crashpad_handler.exe",
            "CrashReportClient.exe", "EpicWebHelper.exe", "UnrealCEFSubProcess.exe",
            "EABackgroundService.exe", "SnippingTool.exe", "ScreenClippingHost.exe",
            "ScreenSketch.exe", "Calculator.exe", "Microsoft.Photos.exe",
            "WindowsTerminal.exe", "Notepad.exe"
        };

        // Always treated as games by exe name, regardless of path/launcher. This is the escape
        // hatch for games that don't sit under a known store folder or launcher - e.g. Store/UWP
        // games in WindowsApps (Minecraft Bedrock), portable installs, emulators. Add your own.
        public List<string> GameProcesses { get; set; } = new()
        {
            "Minecraft.Windows.exe"      // Minecraft Bedrock (UWP, in WindowsApps - not caught by path/launcher)
        };
    }

    public class KnownQuirkyEntry
    {
        public int Tier { get; set; }
        public string Reason { get; set; } = "";
    }

    public class OverrideEntry
    {
        public int TierIdx { get; set; }
        public int Rate { get; set; }
        public int Bits { get; set; }
        public string Engine { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool Locked { get; set; }
        public string Updated { get; set; } = "";
        public int Launches { get; set; }   // launches since last upward probe
    }

    // ====================================================================
    // ETW MONITOR - pure P/Invoke, no NuGet
    // ====================================================================
    public class GlitchEvent
    {
        public DateTime When;
        public uint Pid;
        public ushort EventId;
        public byte Level;
        public ulong Keyword;
    }

    // Raw event capture for the diagnostic --test-etw2 (Microsoft.Windows.Audio.Client provider).
    public class RawEvent
    {
        public DateTime When;
        public uint Pid;
        public ushort Id;
        public byte Opcode;
        public byte Level;
        public string Name = "";   // TDH-decoded TraceLogging event name (id is always 0 for these)
        public byte[] Payload = Array.Empty<byte>();
    }

    public static class EtwMonitor
    {
        // Microsoft-Windows-Audio provider GUID (engine-side, from audiodg - glitch signals)
        private static readonly Guid AudioProvider = new("AE4BD3BE-F36F-45B6-8D21-BDD6FB832853");
        // Microsoft.Windows.Audio.Client TraceLogging provider - fires IN-PROCESS from the game
        // (AudioSes.dll), so the event PID is the game's and it carries the Initialize HRESULT.
        // GUID verified = SHA1 name-hash of "Microsoft.Windows.Audio.Client".
        public static readonly Guid AudioClientProvider = new("6e7b1892-5288-5fe5-8f34-e3b0dc671fd2");
        // Microsoft-Windows-Kernel-Process - fires on process CREATE within ~1ms (vs WMI's ~1s), so
        // we can drop the format for a game we already know BEFORE it opens its audio device instead
        // of a second later. Enabled with keyword 0x10 (WINEVENT_KEYWORD_PROCESS); event Id 1 =
        // ProcessStart, and the new PID is the first payload field (UInt32). OnProcessStartFast is
        // set by the daemon and invoked on the ETW thread with that PID.
        public static readonly Guid KernelProcessProvider = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
        public static Action<int>? OnProcessStartFast;
        private const string SessionName = "AudioSwitcherSession";

        public static ConcurrentQueue<GlitchEvent> Events { get; } = new();
        public static ConcurrentQueue<RawEvent> RawEvents { get; } = new();
        public static volatile bool Running;

        private static IntPtr _sessionHandle = IntPtr.Zero;
        private static ulong _traceHandle;
        private static Thread? _processThread;
        private static EventRecordCallback? _callback;  // pinned
        private static byte _enableLevel = 3;     // provider emits events with level <= this (3 = Warning)
        private static bool _captureAll = false;  // -test-etw sets true to keep every level (diagnostic)
        private static Guid? _extraProvider;      // optional 2nd provider to also enable (diagnostic)
        private static bool _captureRaw = false;  // capture raw payload bytes for the extra provider

        [StructLayout(LayoutKind.Sequential)]
        private struct WNODE_HEADER
        {
            public uint BufferSize; public uint ProviderId;
            public ulong HistoricalContext; public long TimeStamp;
            public Guid Guid; public uint ClientContext; public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize; public uint MinimumBuffers; public uint MaximumBuffers;
            public uint MaximumFileSize; public uint LogFileMode; public uint FlushTimer;
            public uint EnableFlags; public int AgeLimit; public uint NumberOfBuffers;
            public uint FreeBuffers; public uint EventsLost; public uint BuffersWritten;
            public uint LogBuffersLost; public uint RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset; public uint LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_HEADER
        {
            public ushort Size; public ushort HeaderType; public ushort Flags; public ushort EventProperty;
            public uint ThreadId; public uint ProcessId; public long TimeStamp;
            public Guid ProviderId;
            public ushort Id; public byte Version; public byte Channel; public byte Level;
            public byte Opcode; public ushort Task; public ulong Keyword;
            public uint KernelTime; public uint UserTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ETW_BUFFER_CONTEXT
        {
            public byte ProcessorNumber; public byte Alignment; public ushort LoggerId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_RECORD
        {
            public EVENT_HEADER EventHeader;
            public ETW_BUFFER_CONTEXT BufferContext;
            public ushort ExtendedDataCount; public ushort UserDataLength;
            public IntPtr ExtendedData; public IntPtr UserData; public IntPtr UserContext;
        }

        // EVENT_TRACE_LOGFILEW, x64. The original Sequential layout under-sized the
        // inline CurrentEvent (EVENT_TRACE, 88B) + LogfileHeader (TRACE_LOGFILE_HEADER,
        // 280B) = 368B region, placing EventRecordCallback at offset 208 instead of 424.
        // ETW read garbage as the callback pointer, ProcessTrace never fired, zero events.
        // Explicit offsets per evntrace.h; total size MUST be 448.
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 448)]
        private struct EVENT_TRACE_LOGFILE
        {
            [FieldOffset(0)]   public string LogFileName;
            [FieldOffset(8)]   public string LoggerName;
            [FieldOffset(16)]  public long CurrentTime;
            [FieldOffset(24)]  public uint BuffersRead;
            [FieldOffset(28)]  public uint ProcessTraceMode;   // union with LogFileMode
            // [32..120)  EVENT_TRACE          CurrentEvent   (88B, unused)
            // [120..400) TRACE_LOGFILE_HEADER LogfileHeader  (280B, unused)
            [FieldOffset(400)] public IntPtr BufferCallback;
            [FieldOffset(408)] public uint BufferSize;
            [FieldOffset(412)] public uint Filled;
            [FieldOffset(416)] public uint EventsLost;
            [FieldOffset(424)] public IntPtr EventRecordCallbackPtr;
            [FieldOffset(432)] public uint IsKernelTrace;
            [FieldOffset(440)] public IntPtr Context;
        }

        private delegate void EventRecordCallback(ref EVENT_RECORD record);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint StartTrace(out IntPtr SessionHandle, string SessionName, IntPtr Properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ControlTrace(IntPtr SessionHandle, string SessionName, IntPtr Properties, uint ControlCode);

        [DllImport("advapi32.dll")]
        private static extern uint EnableTraceEx2(IntPtr SessionHandle, ref Guid ProviderId, uint ControlCode,
            byte Level, ulong MatchAnyKeyword, ulong MatchAllKeyword, uint Timeout, IntPtr EnableParameters);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE Logfile);

        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(ulong[] HandleArray, uint HandleCount, IntPtr StartTime, IntPtr EndTime);

        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong TraceHandle);

        // TDH (tdh.dll) - built-in Windows event-decoding API; reads the schema already registered
        // on the system. Used to turn TraceLogging events (whose EVENT_HEADER.Id is always 0) into
        // their real event name, so we can find the AudioClientInitialize event.
        [DllImport("tdh.dll")]
        private static extern uint TdhGetEventInformation(ref EVENT_RECORD Event, uint ExtdDataCount,
            IntPtr ExtdData, IntPtr Buffer, ref uint BufferSize);

        // TRACE_EVENT_INFO name offsets (bytes from buffer start). TaskNameOffset(68) is stable across
        // all SDK versions; EventNameOffset(92) is where newer Windows puts TraceLogging event names.
        // We read both and report whatever is populated.
        private static string DecodeTlgName(ref EVENT_RECORD record)
        {
            uint size = 0;
            TdhGetEventInformation(ref record, 0, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0 || size > 65536) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (TdhGetEventInformation(ref record, 0, IntPtr.Zero, buf, ref size) != 0) return "";
                var names = new List<string>();
                foreach (int off in new[] { 92, 68 })   // EventNameOffset (newer), TaskNameOffset (stable)
                {
                    if (off + 4 > size) continue;
                    uint no = (uint)Marshal.ReadInt32(buf, off);
                    if (no == 0 || no >= size) continue;
                    string s = Marshal.PtrToStringUni(buf + (int)no) ?? "";
                    if (s.Length > 0 && !names.Contains(s)) names.Add(s);
                }
                return string.Join("/", names);
            }
            catch { return ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private const uint EVENT_CONTROL_CODE_ENABLE = 1;
        private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const ulong INVALID_PROCESSTRACE_HANDLE = unchecked((ulong)-1L);

        public static void Start(byte enableLevel = 3, bool captureAll = false,
                                 Guid? extraProvider = null, bool captureRaw = false)
        {
            _enableLevel = enableLevel;
            _captureAll = captureAll;
            _extraProvider = extraProvider;
            _captureRaw = captureRaw;
            StopOrphanedSession();

            int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            int totalSize = propsSize + (SessionName.Length + 1) * 2 + 16;
            IntPtr propsPtr = Marshal.AllocHGlobal(totalSize);
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(propsPtr, i, 0);

            var props = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER
                {
                    BufferSize = (uint)totalSize,
                    Flags = 0x00020000,    // WNODE_FLAG_TRACED_GUID
                    ClientContext = 1,      // QPC clock
                    Guid = Guid.NewGuid()
                },
                BufferSize = 64,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                LoggerNameOffset = (uint)propsSize
            };
            Marshal.StructureToPtr(props, propsPtr, false);

            uint rc = StartTrace(out _sessionHandle, SessionName, propsPtr);
            if (rc != 0)
            {
                Marshal.FreeHGlobal(propsPtr);
                throw new InvalidOperationException($"StartTrace failed: {rc}");
            }

            Guid provider = AudioProvider;
            rc = EnableTraceEx2(_sessionHandle, ref provider, EVENT_CONTROL_CODE_ENABLE,
                _enableLevel, ulong.MaxValue, 0, 0, IntPtr.Zero);
            Marshal.FreeHGlobal(propsPtr);
            if (rc != 0) throw new InvalidOperationException($"EnableTraceEx2 failed: {rc}");

            // Also enable the kernel process provider for near-instant launch detection (keyword
            // 0x10 = WINEVENT_KEYWORD_PROCESS, Info level). Best-effort: if it fails we still have
            // WMI-speed detection, so don't abort the session over it.
            if (OnProcessStartFast != null)
            {
                try
                {
                    Guid kp = KernelProcessProvider;
                    EnableTraceEx2(_sessionHandle, ref kp, EVENT_CONTROL_CODE_ENABLE,
                        5, 0x10, 0, 0, IntPtr.Zero);
                }
                catch { }
            }

            // Optionally also enable a second provider (diagnostic: the Audio.Client provider),
            // at Verbose so we see every init event on this session.
            if (_extraProvider.HasValue)
            {
                Guid ep = _extraProvider.Value;
                EnableTraceEx2(_sessionHandle, ref ep, EVENT_CONTROL_CODE_ENABLE,
                    5, ulong.MaxValue, 0, 0, IntPtr.Zero);
            }

            _callback = OnEvent;
            var logfile = new EVENT_TRACE_LOGFILE
            {
                LoggerName = SessionName,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(_callback)
            };

            _traceHandle = OpenTrace(ref logfile);
            if (_traceHandle == INVALID_PROCESSTRACE_HANDLE)
                throw new InvalidOperationException("OpenTrace failed");

            Running = true;
            _processThread = new Thread(() =>
            {
                try { ProcessTrace(new[] { _traceHandle }, 1, IntPtr.Zero, IntPtr.Zero); }
                catch { }
                Running = false;
            }) { IsBackground = true, Name = "ETW-ProcessTrace" };
            _processThread.Start();
        }

        private static void OnEvent(ref EVENT_RECORD record)
        {
            // Kernel process-start: route to the fast launch handler and stop. These are Info-level
            // (4) and would otherwise be dropped by the glitch level filter below; they must never
            // be counted as audio glitches. Id 1 = ProcessStart; the new PID is the first payload
            // field (UInt32) - a stable offset across Windows versions, unlike the later fields.
            if (record.EventHeader.ProviderId == KernelProcessProvider)
            {
                var cb = OnProcessStartFast;
                if (cb != null && record.EventHeader.Id == 1 &&
                    record.UserDataLength >= 4 && record.UserData != IntPtr.Zero)
                {
                    int pid = Marshal.ReadInt32(record.UserData);
                    if (pid > 0) { try { cb(pid); } catch { } }
                }
                return;
            }

            // Diagnostic: raw-capture the extra (Audio.Client) provider's events with payload.
            if (_captureRaw && _extraProvider.HasValue && record.EventHeader.ProviderId == _extraProvider.Value)
            {
                int len = record.UserDataLength;
                byte[] payload = Array.Empty<byte>();
                if (len > 0 && record.UserData != IntPtr.Zero)
                {
                    if (len > 512) len = 512;
                    payload = new byte[len];
                    try { Marshal.Copy(record.UserData, payload, 0, len); } catch { }
                }
                string name = "";
                try { name = DecodeTlgName(ref record); } catch { }
                while (RawEvents.Count > 500) RawEvents.TryDequeue(out _);
                RawEvents.Enqueue(new RawEvent
                {
                    When = DateTime.Now, Pid = record.EventHeader.ProcessId,
                    Id = record.EventHeader.Id, Opcode = record.EventHeader.Opcode,
                    Level = record.EventHeader.Level, Name = name, Payload = payload
                });
                return;
            }

            // Production keeps Warning(3)/Error(2)/Critical(1). Diagnostic (-test-etw) keeps all.
            if (!_captureAll && (record.EventHeader.Level == 0 || record.EventHeader.Level > 3)) return;

            var ev = new GlitchEvent
            {
                // Session clock is QPC (ClientContext=1), so EventHeader.TimeStamp is NOT
                // a FILETIME. Real-time delivery is near-instant, so wall-clock at capture
                // time is accurate enough for the glitch-rate window and avoids QPC math.
                When = DateTime.Now,
                Pid = record.EventHeader.ProcessId,
                EventId = record.EventHeader.Id,
                Level = record.EventHeader.Level,
                Keyword = record.EventHeader.Keyword
            };

            // Cap queue size to prevent unbounded growth on extreme glitch storms
            while (Events.Count > 1000) Events.TryDequeue(out _);
            Events.Enqueue(ev);
        }

        private static void StopOrphanedSession()
        {
            int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            int totalSize = propsSize + (SessionName.Length + 1) * 2 + 16;
            IntPtr propsPtr = Marshal.AllocHGlobal(totalSize);
            for (int i = 0; i < totalSize; i++) Marshal.WriteByte(propsPtr, i, 0);

            var props = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER { BufferSize = (uint)totalSize },
                LoggerNameOffset = (uint)propsSize
            };
            Marshal.StructureToPtr(props, propsPtr, false);
            ControlTrace(IntPtr.Zero, SessionName, propsPtr, EVENT_TRACE_CONTROL_STOP);
            Marshal.FreeHGlobal(propsPtr);
        }

        public static void Stop()
        {
            Running = false;
            if (_traceHandle != 0 && _traceHandle != INVALID_PROCESSTRACE_HANDLE)
            {
                CloseTrace(_traceHandle);
                _traceHandle = 0;
            }
            StopOrphanedSession();
            _sessionHandle = IntPtr.Zero;
            _processThread?.Join(2000);
        }
    }

    // ====================================================================
    // ENDPOINT MANAGEMENT - registry + PnP
    // ====================================================================
    public class AudioEndpoint
    {
        public string Guid { get; set; } = "";
        public string Name { get; set; } = "";
        public string PnpId { get; set; } = "";
        public string PropertiesKeyPath { get; set; } = "";  // HKLM full path
        public int? State { get; set; }
    }

    public static class EndpointManager
    {
        public const string MMRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        private const string DescKey     = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";  // PKEY_Device_DeviceDesc -> "Speakers"
        private const string AdapterKey  = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";  // adapter/device name    -> "FiiO K7"
        private const string InstanceKey = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";  // instance id "{1}.USB\VID_2972&PID_0047&MI_00\..."

        public static List<AudioEndpoint> Enumerate()
        {
            var result = new List<AudioEndpoint>();
            using var root = Registry.LocalMachine.OpenSubKey(MMRoot);
            if (root == null) return result;

            foreach (var guid in root.GetSubKeyNames())
            {
                using var devKey = root.OpenSubKey(guid);
                if (devKey == null) continue;
                using var propsKey = devKey.OpenSubKey("Properties");

                string? desc    = propsKey?.GetValue(DescKey)    as string;
                string? adapter = propsKey?.GetValue(AdapterKey) as string;  // the real product name, e.g. "FiiO K7"
                string  name    = (!string.IsNullOrEmpty(adapter) && adapter != desc)
                                    ? $"{desc} ({adapter})" : (desc ?? adapter ?? "");
                // Instance id is stored with a "{N}." prefix; strip it for Disable/Enable-PnpDevice.
                string  pnp     = propsKey?.GetValue(InstanceKey) as string ?? "";
                int br = pnp.IndexOf("}.", StringComparison.Ordinal);
                if (br >= 0) pnp = pnp.Substring(br + 2);
                int? state   = devKey.GetValue("DeviceState") as int?;

                result.Add(new AudioEndpoint
                {
                    Guid = guid,
                    Name = name,
                    PnpId = pnp,
                    PropertiesKeyPath = $@"HKLM\{MMRoot}\{guid}\Properties",
                    State = state
                });
            }
            return result;
        }

        // DeviceState low nibble: 1=active, 2=disabled, 4=not-present, 8=unplugged.
        public static bool IsActive(AudioEndpoint e) => ((e.State ?? 0) & 0xF) == 1;

        public static AudioEndpoint? FindByName(string substring)
        {
            // Prefer the active endpoint; the registry keeps many stale duplicates.
            return Enumerate()
                .Where(e => e.Name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(IsActive)
                .FirstOrDefault();
        }

        // Endpoint to manage: the current default playback device by default, or a
        // name-substring override if TargetDeviceName is set. This is what makes it
        // work for anyone with no config (not hardcoded to one DAC).
        public static AudioEndpoint? Resolve(Config cfg)
        {
            return string.IsNullOrWhiteSpace(cfg.TargetDeviceName)
                ? GetDefaultRender() : FindByName(cfg.TargetDeviceName);
        }

        public static AudioEndpoint? GetDefaultRender()
        {
            string? id = DefaultRenderId();
            if (string.IsNullOrEmpty(id)) return null;
            int dot = id.LastIndexOf('.');
            string guid = dot >= 0 ? id.Substring(dot + 1) : id;   // "{0.0.0.00000000}.{guid}" -> "{guid}"
            return Enumerate().FirstOrDefault(e => string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase));
        }

        // ---- IMMDeviceEnumerator: default render endpoint id. COM, no libs. ----
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class CMMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
            [PreserveSig] int GetId(out IntPtr id);
        }

        private static string? DefaultRenderId()
        {
            IMMDeviceEnumerator? en = null; IMMDevice? dev = null;
            try
            {
                en = (IMMDeviceEnumerator)new CMMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return null;  // eRender, eConsole
                if (dev.GetId(out IntPtr p) != 0 || p == IntPtr.Zero) return null;
                string? id = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return id;
            }
            catch { return null; }
            finally
            {
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        // ---- Per-app audio sessions: detect "running but silent". COM, no libs. ----
        private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr ctl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr vol);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        // GetSession returns IAudioSessionControl; we QI to the two interfaces below.
        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl { }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl (base) methods first, in vtable order:
            [PreserveSig] int GetState(out int state);          // 0=Inactive, 1=Active, 2=Expired
            [PreserveSig] int GetDisplayName(out IntPtr n);
            [PreserveSig] int SetDisplayName(string n, ref Guid ev);
            [PreserveSig] int GetIconPath(out IntPtr p);
            [PreserveSig] int SetIconPath(string p, ref Guid ev);
            [PreserveSig] int GetGroupingParam(out Guid g);
            [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ev);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
            // IAudioSessionControl2 additions:
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out uint count);
        }

        private static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

        // Is a working peak meter available on the default endpoint? A real meter reports its
        // channel count (>0); a broken/absent one doesn't. Lets us validate silence detection at
        // startup WITHOUT playing any sound. Endpoint-level meter (master output).
        public static bool MeterAvailable()
        {
            IMMDeviceEnumerator? en = null; IMMDevice? dev = null; IAudioMeterInformation? meter = null;
            try
            {
                en = (IMMDeviceEnumerator)new CMMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return false;
                var iid = IID_IAudioMeterInformation;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out object m) != 0 || m == null) return false;
                meter = (IAudioMeterInformation)m;
                return meter.GetMeteringChannelCount(out uint ch) == 0 && ch > 0;
            }
            catch { return false; }
            finally
            {
                if (meter != null) Marshal.ReleaseComObject(meter);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        public readonly struct SessionInfo
        {
            public readonly uint Pid; public readonly int State; public readonly float Peak;
            public SessionInfo(uint p, int s, float pk) { Pid = p; State = s; Peak = pk; }
        }

        // Snapshot of the default render endpoint's audio sessions (pid / state / peak meter).
        // state: 0=Inactive, 1=Active, 2=Expired. peak: 0.0..1.0 over the last device period.
        public static List<SessionInfo> GetSessions()
        {
            var result = new List<SessionInfo>();
            IMMDeviceEnumerator? en = null; IMMDevice? dev = null;
            IAudioSessionManager2? mgr = null; IAudioSessionEnumerator? se = null;
            try
            {
                en = (IMMDeviceEnumerator)new CMMDeviceEnumerator();
                if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return result;
                var iid = IID_IAudioSessionManager2;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out object mgrObj) != 0 || mgrObj == null) return result;
                mgr = (IAudioSessionManager2)mgrObj;
                if (mgr.GetSessionEnumerator(out se) != 0 || se == null) return result;
                se.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? sc = null;
                    try
                    {
                        if (se.GetSession(i, out sc) != 0 || sc == null) continue;
                        var c2 = (IAudioSessionControl2)sc;
                        c2.GetProcessId(out uint pid);
                        c2.GetState(out int state);
                        float peak = 0f;
                        try { ((IAudioMeterInformation)sc).GetPeakValue(out peak); } catch { }
                        result.Add(new SessionInfo(pid, state, peak));
                    }
                    catch { }
                    finally { if (sc != null) Marshal.ReleaseComObject(sc); }
                }
                return result;
            }
            catch { return result; }
            finally
            {
                if (se != null) Marshal.ReleaseComObject(se);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
            }
        }

        public static byte[] BuildWaveFormatBlob(int sampleRate, int bitDepth, int channels, bool isFloat = false)
        {
            // Windows shared-mode PCM uses a 16- or 32-bit container. 24-bit is carried as
            // 24 valid bits inside a 32-bit container; a packed 24-bit (3-byte) container is
            // rejected as AUDCLNT_E_UNSUPPORTED_FORMAT (0x88890008).
            int validBits     = isFloat ? 32 : bitDepth;
            int containerBits = (isFloat || bitDepth == 24) ? 32 : bitDepth;
            int blockAlign = channels * (containerBits / 8);
            var subFmt = new Guid(isFloat ? "00000003-0000-0010-8000-00aa00389b71"   // IEEE_FLOAT (engine mix)
                                          : "00000001-0000-0010-8000-00aa00389b71"); // PCM
            ushort mask = (ushort)(channels == 2 ? 0x3 : 0x0);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((ushort)0xFFFE);              // wFormatTag = WAVE_FORMAT_EXTENSIBLE
            bw.Write((ushort)channels);
            bw.Write((uint)sampleRate);
            bw.Write((uint)(sampleRate * blockAlign));  // nAvgBytesPerSec
            bw.Write((ushort)blockAlign);
            bw.Write((ushort)containerBits);       // wBitsPerSample = container size (16/32)
            bw.Write((ushort)22);                  // cbSize
            bw.Write((ushort)validBits);           // wValidBitsPerSample
            bw.Write((uint)mask);
            bw.Write(subFmt.ToByteArray());
            return ms.ToArray();
        }

        // The MMDevices format key is ACL'd to SYSTEM/audiosrv, so a direct registry write
        // fails even elevated ("registry access is not allowed"). IPolicyConfig is the private
        // COM interface the Sound Control Panel uses: the audio service does the privileged
        // write and the format applies without a PnP bounce. Works from an elevated process.
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            // Declared in vtable order; only SetDeviceFormat is called.
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int bDefault, out IntPtr fmt);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFmt, IntPtr mixFmt);
        }

        public static void SetFormat(AudioEndpoint endpoint, int sampleRate, int bitDepth, int channels, bool verbose)
        {
            byte[] epBlob  = BuildWaveFormatBlob(sampleRate, bitDepth, channels);           // device endpoint format (PCM)
            byte[] mixBlob = BuildWaveFormatBlob(sampleRate, 32, channels, isFloat: true);  // engine mix format (32-bit float)
            string deviceId = "{0.0.0.00000000}." + endpoint.Guid;   // render endpoint id PolicyConfig expects

            IntPtr pEp  = Marshal.AllocHGlobal(epBlob.Length);
            IntPtr pMix = Marshal.AllocHGlobal(mixBlob.Length);
            IPolicyConfig? pc = null;
            try
            {
                Marshal.Copy(epBlob, 0, pEp, epBlob.Length);
                Marshal.Copy(mixBlob, 0, pMix, mixBlob.Length);
                pc = (IPolicyConfig)new CPolicyConfigClient();
                int hr = pc.SetDeviceFormat(deviceId, pEp, pMix);   // endpoint = PCM, mix = 32-bit float
                if (hr != 0)
                    throw new InvalidOperationException($"SetDeviceFormat failed 0x{hr:X8} for {deviceId}");
                if (verbose) Console.WriteLine($"    SetDeviceFormat {deviceId} -> {sampleRate}/{bitDepth}");
            }
            finally
            {
                if (pc != null) Marshal.ReleaseComObject(pc);
                Marshal.FreeHGlobal(pEp);
                Marshal.FreeHGlobal(pMix);
            }
        }

        public static string ProbeFormats(AudioEndpoint endpoint)
        {
            string deviceId = "{0.0.0.00000000}." + endpoint.Guid;
            var pc = (IPolicyConfig)new CPolicyConfigClient();
            try
            {
                string dev = pc.GetDeviceFormat(deviceId, 1, out IntPtr pd) == 0 && pd != IntPtr.Zero
                    ? DescribeWaveFormat(pd) : "GetDeviceFormat failed";
                string mix = pc.GetMixFormat(deviceId, out IntPtr pm) == 0 && pm != IntPtr.Zero
                    ? DescribeWaveFormat(pm) : "GetMixFormat failed";
                return $"  deviceId  = {deviceId}\n  DeviceFmt = {dev}\n  MixFmt    = {mix}";
            }
            finally { Marshal.ReleaseComObject(pc); }
        }

        private static string DescribeWaveFormat(IntPtr p)
        {
            ushort tag = (ushort)Marshal.ReadInt16(p, 0);
            ushort ch  = (ushort)Marshal.ReadInt16(p, 2);
            uint rate  = (uint)Marshal.ReadInt32(p, 4);
            uint avg   = (uint)Marshal.ReadInt32(p, 8);
            ushort ba  = (ushort)Marshal.ReadInt16(p, 12);
            ushort bits= (ushort)Marshal.ReadInt16(p, 14);
            ushort cb  = (ushort)Marshal.ReadInt16(p, 16);
            string s = $"tag=0x{tag:X4} ch={ch} rate={rate} avg={avg} blockAlign={ba} bits={bits} cbSize={cb}";
            if (tag == 0xFFFE && cb >= 22)
            {
                ushort valid = (ushort)Marshal.ReadInt16(p, 18);
                uint mask = (uint)Marshal.ReadInt32(p, 20);
                byte[] g = new byte[16]; Marshal.Copy(p + 24, g, 0, 16);
                s += $" valid={valid} mask=0x{mask:X} sub={new Guid(g)}";
            }
            return s;
        }
    }

    // ====================================================================
    // GAME DETECTION + ENGINE FINGERPRINTING
    // ====================================================================
    public static class GameDetection
    {
        // procName = the process image name from WMI (always available, e.g. "Minecraft.Windows.exe");
        // exePath = the full path (may be "" for UWP/protected processes we can't read).
        public static (bool isGame, string reason)? IsGame(string exePath, string procName, int parentPid, Config cfg)
        {
            // Prefer the WMI process name for name checks - it works even when the path is unreadable.
            string fileName = !string.IsNullOrEmpty(procName) ? procName
                            : (!string.IsNullOrEmpty(exePath) ? Path.GetFileName(exePath) : "");
            if (string.IsNullOrEmpty(fileName) && string.IsNullOrEmpty(exePath)) return null;

            // Denylist first: launcher helpers / crash handlers / non-game Store apps are never games.
            if (cfg.IgnoreProcesses.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
                return null;

            // Allowlist: exe names explicitly declared games (Store/UWP games, portable, user-added).
            if (cfg.GameProcesses.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
                return (true, $"name:{fileName}");

            // Path hints only apply when we actually have a path.
            if (!string.IsNullOrEmpty(exePath))
                foreach (var hint in cfg.GamePathHints)
                {
                    if (exePath.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return (true, $"path:{hint}");
                }

            if (parentPid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    string parentExe = parent.ProcessName + ".exe";
                    if (cfg.LauncherProcesses.Any(l => string.Equals(l, parentExe, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Disambiguate generic Launcher.exe
                        if (string.Equals(parentExe, "Launcher.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                string parentPath = parent.MainModule?.FileName ?? "";
                                if (parentPath.IndexOf("Rockstar", StringComparison.OrdinalIgnoreCase) < 0)
                                    return null;
                            }
                            catch { return null; }
                        }
                        return (true, $"parent:{parent.ProcessName}");
                    }
                }
                catch { }
            }
            return null;
        }

        public static string Fingerprint(int pid, string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "Unknown-Game";
            string baseName = Path.GetFileNameWithoutExtension(exePath);

            if (baseName.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                baseName.EndsWith("-Win32-Shipping", StringComparison.OrdinalIgnoreCase))
                return "UE5";

            try
            {
                string dir = Path.GetDirectoryName(exePath) ?? "";
                if (File.Exists(Path.Combine(dir, "UnityPlayer.dll")))  return "Unity";
                if (File.Exists(Path.Combine(dir, "GameAssembly.dll"))) return "Unity";
                if (File.Exists(Path.Combine(dir, "fmod.dll")))         return "FMOD";
                if (File.Exists(Path.Combine(dir, "fmodstudio.dll")))   return "FMOD";
                if (Directory.Exists(dir))
                {
                    if (Directory.EnumerateFiles(dir, "AkSoundEngine*.dll").Any()) return "Wwise";
                    if (Directory.EnumerateFiles(dir, "Wwise*.dll").Any())         return "Wwise";
                }
                if (Directory.Exists(Path.Combine(dir, "..", "Engine", "Binaries"))) return "UE5";
            }
            catch { }

            try
            {
                using var proc = Process.GetProcessById(pid);
                var modules = proc.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
                if (modules.Contains("UnityPlayer.dll", StringComparer.OrdinalIgnoreCase)) return "Unity";
                if (modules.Any(m => m.StartsWith("UE4", StringComparison.OrdinalIgnoreCase) ||
                                     m.StartsWith("UE5", StringComparison.OrdinalIgnoreCase) ||
                                     m.StartsWith("UnrealEngine", StringComparison.OrdinalIgnoreCase)))
                    return "UE5";
                if (modules.Any(m => m.StartsWith("fmod", StringComparison.OrdinalIgnoreCase))) return "FMOD";
                if (modules.Any(m => m.StartsWith("AkSoundEngine", StringComparison.OrdinalIgnoreCase) ||
                                     m.StartsWith("Wwise", StringComparison.OrdinalIgnoreCase)))
                    return "Wwise";
                if (modules.Contains("engine2.dll", StringComparer.OrdinalIgnoreCase)) return "Source2";
                if (modules.Contains("tier0.dll", StringComparer.OrdinalIgnoreCase)) return "Source";
            }
            catch { }

            return "Unknown-Game";
        }
    }

    // ====================================================================
    // STATE: overrides, crashes, glitches
    // ====================================================================
    public static class StateStore
    {
        public static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitcher");
        public static readonly string ConfigFile    = Path.Combine(ConfigDir, "config.json");
        public static readonly string OverridesFile = Path.Combine(ConfigDir, "overrides.json");
        public static readonly string CrashLogFile  = Path.Combine(ConfigDir, "crash-log.json");
        public static readonly string GlitchLogFile = Path.Combine(ConfigDir, "glitch-log.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        static StateStore() { Directory.CreateDirectory(ConfigDir); }

        public static Config LoadConfig()
        {
            if (!File.Exists(ConfigFile))
            {
                var c = new Config();
                Save(c, ConfigFile);
                return c;
            }
            try
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile)) ?? new Config();
                var fresh = new Config();
                if (loaded.ConfigVersion != fresh.ConfigVersion)
                {
                    // Config is from a different build - back it up and regenerate so changed tier
                    // ladders / engine defaults take effect. Old file is kept as config.json.old.
                    try { File.Copy(ConfigFile, ConfigFile + ".old", true); } catch { }
                    Save(fresh, ConfigFile);
                    Console.WriteLine($"[info] config.json was v{loaded.ConfigVersion}; regenerated at v{fresh.ConfigVersion} (old kept as config.json.old)");
                    return fresh;
                }
                return Validate(loaded);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] Config parse failed, using defaults: {ex.Message}");
                return new Config();
            }
        }

        // Guard against a hand-edited config.json crashing the daemon (empty tiers, bad indices,
        // nonsensical values, null collections).
        private static Config Validate(Config c)
        {
            var d = new Config();
            if (c.ProfileTiers == null || c.ProfileTiers.Count == 0) c.ProfileTiers = d.ProfileTiers;
            if (c.IdleTier < 0 || c.IdleTier >= c.ProfileTiers.Count) c.IdleTier = 0;
            if (c.Channels < 1) c.Channels = 2;
            if (c.CrashThresholdSeconds < 0) c.CrashThresholdSeconds = d.CrashThresholdSeconds;
            if (c.GlitchThreshold < 1) c.GlitchThreshold = d.GlitchThreshold;
            if (c.GlitchWindowSeconds < 1) c.GlitchWindowSeconds = d.GlitchWindowSeconds;
            if (c.SilenceWindowSeconds < 0) c.SilenceWindowSeconds = 0;
            if (c.SilenceGraceSeconds < 0) c.SilenceGraceSeconds = d.SilenceGraceSeconds;
            if (c.AutoLearnFullscreenSeconds < 0) c.AutoLearnFullscreenSeconds = 0;
            c.EngineDefaults ??= d.EngineDefaults;
            c.LauncherProcesses ??= d.LauncherProcesses;
            c.GamePathHints ??= d.GamePathHints;
            c.IgnoreProcesses ??= d.IgnoreProcesses;
            c.GameProcesses ??= d.GameProcesses;
            c.KnownQuirky ??= d.KnownQuirky;
            return c;
        }

        public static Dictionary<string, OverrideEntry> LoadOverrides() => LoadDict<OverrideEntry>(OverridesFile);
        public static Dictionary<string, List<Dictionary<string, object>>> LoadCrashLog()  => LoadDictList(CrashLogFile);
        public static Dictionary<string, List<Dictionary<string, object>>> LoadGlitchLog() => LoadDictList(GlitchLogFile);

        public static void SaveOverrides(Dictionary<string, OverrideEntry> d) => Save(d, OverridesFile);
        public static void SaveCrashLog(Dictionary<string, List<Dictionary<string, object>>> d) => Save(d, CrashLogFile);
        public static void SaveGlitchLog(Dictionary<string, List<Dictionary<string, object>>> d) => Save(d, GlitchLogFile);

        private static Dictionary<string, T> LoadDict<T>(string path)
        {
            if (!File.Exists(path)) return new();
            try { return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }

        private static Dictionary<string, List<Dictionary<string, object>>> LoadDictList(string path)
        {
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object>>>>(File.ReadAllText(path)) ?? new();
            }
            catch { return new(); }
        }

        public static void Save<T>(T obj, string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOpts));
    }

    // ====================================================================
    // CRASH DUMP SCANNING
    // ====================================================================
    public static class CrashScanner
    {
        private static readonly string[] AudioKeywords = {
            "winmm", "XAudio", "AudioMixer", "fmod", "Wwise", "AkSoundEngine",
            "WASAPI", "AudioDevice", "SoundEngine", "mmdevapi"
        };

        public static bool HasAudioEvidence(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(exePath);
                string gameApp = baseName.Replace("-Win64-Shipping", "");
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var candidates = new[]
                {
                    Path.Combine(localApp, gameApp, "Saved", "Crashes"),
                    Path.Combine(localApp, "CrashDumps")
                };

                foreach (var folder in candidates)
                {
                    if (!Directory.Exists(folder)) continue;
                    var cutoff = DateTime.Now.AddMinutes(-2);
                    var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".log") || f.EndsWith(".xml") || f.EndsWith(".txt"))
                        .Where(f => File.GetLastWriteTime(f) > cutoff);

                    foreach (var file in files)
                    {
                        try
                        {
                            string content = File.ReadAllText(file);
                            if (AudioKeywords.Any(kw => content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                                return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // RELEASE SIGNING (class Signing) lives in Signing.cs so its crypto can be unit-tested
    // without the Windows-only code here. It's still in the AudioSwitcher namespace.

    // ====================================================================
    // UPDATE CHECK - queries the public GitHub Releases API (no auth), notify-only
    // ====================================================================
    public static class UpdateChecker
    {
        private const string LatestApi = "https://api.github.com/repos/thetrueartist/Audio-Switcher/releases/latest";
        public const string ReleasesPage = "https://github.com/thetrueartist/Audio-Switcher/releases/latest";
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

        static UpdateChecker() { _http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioSwitcher-UpdateCheck"); }

        // Returns the latest release version (e.g. "1.2.0") if it's NEWER than 'current', else "".
        // When release signing is enabled, ONLY a release carrying a validly-signed manifest is
        // trusted - a compromised repo without the private key can't forge one, so no update is
        // offered. Also exposes the signed sha256 of the download for a future verified install.
        public static string VerifiedSha256 { get; private set; } = "";   // signed hash of the zip ("" if unsigned)
        public static string ZipUrl { get; private set; } = "";           // download URL of the release zip

        public static string NewerVersion(string current)
        {
            try
            {
                string json = _http.GetStringAsync(LatestApi).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                string latest = tag.TrimStart('v', 'V');

                string? mUrl = null, sUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.GetProperty("name").GetString() ?? "";
                        string url = a.GetProperty("browser_download_url").GetString() ?? "";
                        if (name == "manifest.json") mUrl = url;
                        else if (name == "manifest.sig") sUrl = url;
                        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipUrl = url;
                    }

                if (Signing.Enabled)
                {
                    if (mUrl == null || sUrl == null) return "";   // unsigned release -> don't trust
                    byte[] mBytes = _http.GetByteArrayAsync(mUrl).GetAwaiter().GetResult();
                    string sig = _http.GetStringAsync(sUrl).GetAwaiter().GetResult().Trim();
                    if (!Signing.Verify(mBytes, sig)) return "";   // bad signature -> reject
                    using var mdoc = JsonDocument.Parse(mBytes);
                    latest = (mdoc.RootElement.GetProperty("version").GetString() ?? latest).TrimStart('v', 'V');
                    VerifiedSha256 = mdoc.RootElement.TryGetProperty("sha256", out var h) ? (h.GetString() ?? "") : "";
                }
                return IsNewer(latest, current) ? latest : "";
            }
            catch { return ""; }   // no network / rate-limited / parse fail -> silently skip
        }

        private static bool IsNewer(string a, string b)   // dotted-numeric compare: is a > b?
        {
            var pa = a.Split('.'); var pb = b.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length && int.TryParse(pa[i], out var xi) ? xi : 0;
                int y = i < pb.Length && int.TryParse(pb[i], out var yi) ? yi : 0;
                if (x != y) return x > y;
            }
            return false;
        }
    }

    // ====================================================================
    // SELF-UPDATE - user-triggered one-click install. Verifies the signed SHA-256 (when
    // signing is on), swaps the exe via rename (rollback-safe), and restarts via a helper.
    // ====================================================================
    public static class SelfUpdate
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
        static SelfUpdate() { _http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioSwitcher-SelfUpdate"); }

        // Stage the download/extract/restart-helper in a subfolder of the install dir - NOT %TEMP%,
        // which is world-writable. An elevated updater must never read-then-execute files that a
        // normal-privilege process could swap in between the hash check and the run (TOCTOU -> EoP).
        // Once installed under Program Files, this folder inherits its admin-only-write ACL.
        private static string StagingDir()
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string baseDir = exe.Length > 0 ? (Path.GetDirectoryName(exe) ?? Path.GetTempPath()) : Path.GetTempPath();
            return Path.Combine(baseDir, "update");
        }

        // Downloads the zip, verifies its hash, and applies it. Returns "" on success (caller must
        // then quit - the helper restarts the new exe), or an error string (nothing was changed).
        public static string DownloadAndApply(string zipUrl, string expectedSha256, Action<string>? log = null)
        {
            try
            {
                if (string.IsNullOrEmpty(zipUrl)) return "no download URL";
                string tmp = StagingDir();
                Directory.CreateDirectory(tmp);
                string zipPath = Path.Combine(tmp, "update.zip");
                log?.Invoke("Downloading update...");
                using (var resp = _http.GetAsync(zipUrl).GetAwaiter().GetResult())
                {
                    resp.EnsureSuccessStatusCode();
                    using var fs = File.Create(zipPath);
                    resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                }
                return Apply(zipPath, expectedSha256, log);
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Verify + swap from a local zip (also used by --apply-update for isolated testing).
        public static string Apply(string zipPath, string expectedSha256, Action<string>? log = null)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(zipPath);
                string sha = Signing.Sha256Hex(bytes);
                if (!string.IsNullOrEmpty(expectedSha256) &&
                    !sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    return $"SHA-256 mismatch - update REJECTED (expected {expectedSha256}, got {sha})";
                if (string.IsNullOrEmpty(expectedSha256))
                    log?.Invoke("[warn] no signed hash to verify against (signing not enabled) - trusting TLS only");

                string extract = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(zipPath))!, "extracted");
                if (Directory.Exists(extract)) Directory.Delete(extract, true);
                ZipFile.ExtractToDirectory(zipPath, extract);
                string newExe = Path.Combine(extract, "AudioSwitcher.exe");
                if (!File.Exists(newExe)) return "AudioSwitcher.exe not found in the update zip";

                string curExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(curExe) || !File.Exists(curExe)) return "cannot locate the running exe";
                string oldExe = curExe + ".old";

                // Windows allows RENAMING a running exe (but not overwriting it). Rename current out
                // of the way, copy the new one in. On any failure, rename back (rollback).
                try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
                File.Move(curExe, oldExe);
                try { File.Copy(newExe, curExe, true); }
                catch (Exception ex)
                {
                    try { if (!File.Exists(curExe)) File.Move(oldExe, curExe); } catch { }
                    return "swap failed (rolled back): " + ex.Message;
                }

                // Clear the first-run marker so the restarted daemon opens the control panel
                // (the user sees it came back on the new version).
                try { File.Delete(Path.Combine(StateStore.ConfigDir, ".gui-shown")); } catch { }

                // Helper: wait for THIS process to exit, then start the new exe and remove .old.
                int pid = Process.GetCurrentProcess().Id;
                string helper = Path.Combine(StagingDir(), "restart.cmd");
                File.WriteAllText(helper,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
                    $"schtasks /run /tn AudioSwitcherDaemon >nul 2>nul || start \"\" \"{curExe}\"\r\n" +
                    $"del \"{oldExe}\" >nul 2>nul\r\n");
                Process.Start(new ProcessStartInfo { FileName = helper, UseShellExecute = true,
                    CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                log?.Invoke("Update applied - restarting.");
                return "";
            }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ====================================================================
    // FULLSCREEN GAME DETECTION - low-FP "this is a game" signal for auto-learn
    // ====================================================================
    public static class Foreground
    {
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")]  private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]  private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 6;

        // Exe name of the foreground process IF an exclusive-fullscreen Direct3D app is running,
        // else "". Exclusive fullscreen is a strong game signal - browsers/video/desktop apps
        // composite in windowed/borderless mode and never report this state.
        public static string FullscreenGameExe()
        {
            try
            {
                if (SHQueryUserNotificationState(out int st) != 0 || st != QUNS_RUNNING_D3D_FULL_SCREEN)
                    return "";
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return "";
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName + ".exe";
            }
            catch { return ""; }
        }
    }

    // ====================================================================
    // MAIN DAEMON
    // ====================================================================
    public class RunningGame
    {
        public int Pid;
        public string Exe = "";
        public string Engine = "";
        public string ExePath = "";
        public DateTime StartedAt;
        public ProfileTier Profile = new();
        public int TierIdx;
        public List<DateTime> GlitchHistory = new();
        public DateTime? SilentSince;   // when the session first went Active-but-silent (null = not)
        public bool SawAudio;           // latched true once the game produces any sound
        public bool IsProbe;            // this launch is an upward probe (one tier above the learned override)
        public bool Failed;             // a crash/glitch/silence bump fired this session
    }

    // Optional game freeze across a format switch. Suspending every thread of the game the instant
    // it launches, changing the device format, then resuming means the game opens its audio device
    // at the NEW rate - it can't win the race because it isn't running while we switch. Uses the
    // documented ntdll process-wide suspend (the same primitive Process Explorer / Process Hacker's
    // "Suspend" uses); no memory read/write, no injection, no hooking. Off by default (SuspendDuring-
    // Switch) because a running anti-cheat could flag the handle/suspend; meant for offline titles.
    static class ProcessControl
    {
        [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr handle);
        [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inheritHandle, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        const int PROCESS_SUSPEND_RESUME = 0x0800;

        // Freeze the process; returns a handle to pass to Resume, or IntPtr.Zero on any failure
        // (caller then just proceeds unfrozen - freezing is best-effort, it never blocks a launch).
        public static IntPtr Suspend(int pid)
        {
            try
            {
                IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero) return IntPtr.Zero;
                if (NtSuspendProcess(h) != 0) { CloseHandle(h); return IntPtr.Zero; }
                return h;
            }
            catch { return IntPtr.Zero; }
        }

        // Resume and release the handle. Safe to call with IntPtr.Zero (no-op).
        public static void Resume(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { NtResumeProcess(handle); } catch { }
            try { CloseHandle(handle); } catch { }
        }

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EnumDeviceDrivers(IntPtr[]? addresses, int cb, out int cbNeeded);
        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetDeviceDriverBaseName(IntPtr address, StringBuilder name, int size);

        // Known anti-cheat kernel drivers. These are the ACs that actually watch for tampering
        // (kernel Ob-callbacks); if one is loaded we must NOT suspend the game. Matched as a
        // substring of each loaded driver's base name, case-insensitively.
        static readonly string[] AntiCheatDrivers =
        {
            "EasyAntiCheat", "BEDaisy", "BattlEye", "vgk",   // EAC, BattlEye, Riot Vanguard
            "mhyprot",                                        // miHoYo (Genshin/HSR/ZZZ)
            "xhunter1", "TFSysMon", "SGuard",                 // XIGNCODE3, nProtect, others
        };

        // Returns the name of a loaded anti-cheat driver, or null if none. Cheap (one syscall +
        // a name lookup per driver). On any failure returns null (fail-open) - the caller logs the
        // outcome either way, so a user on an anti-cheat title can still see what happened.
        public static string? AntiCheatDriverLoaded()
        {
            try
            {
                if (!EnumDeviceDrivers(null, 0, out int need) || need <= 0) return null;
                var addrs = new IntPtr[need / IntPtr.Size];
                if (!EnumDeviceDrivers(addrs, need, out _)) return null;
                var sb = new StringBuilder(260);
                foreach (var addr in addrs)
                {
                    if (addr == IntPtr.Zero) continue;
                    sb.Clear();
                    if (GetDeviceDriverBaseName(addr, sb, sb.Capacity) == 0) continue;
                    string dn = sb.ToString();
                    foreach (var ac in AntiCheatDrivers)
                        if (dn.IndexOf(ac, StringComparison.OrdinalIgnoreCase) >= 0)
                            return dn;
                }
            }
            catch { }
            return null;
        }
    }

    public class Daemon
    {
        private readonly Config _config;
        private readonly AudioEndpoint _endpoint;
        private readonly bool _verbose;
        private readonly Dictionary<int, RunningGame> _running = new();
        private readonly HashSet<int> _claimed = new();   // pids already taken (ETW-fast vs WMI dedup)
        private readonly object _lock = new();
        private readonly object _applyLock = new();   // serializes ApplyEffective (UI pause vs WMI events)
        private ProfileTier _lastApplied;
        private ManagementEventWatcher? _startWatcher;
        private ManagementEventWatcher? _stopWatcher;
        private readonly ManualResetEventSlim _shutdown = new(false);
        private Thread? _glitchThread;
        private Thread? _ipcThread;
        private Thread? _autoLearnThread;

        public Daemon(Config config, AudioEndpoint endpoint, bool verbose)
        {
            _config = config;
            _endpoint = endpoint;
            _verbose = verbose;
            _lastApplied = config.ProfileTiers[config.IdleTier];
        }

        // ---- Read-only state + controls for the tray UI ----
        public string DeviceName => _endpoint.Name;
        public string CurrentFormat => $"{_lastApplied.Rate} Hz / {_lastApplied.Bits}-bit";
        public int CurrentRate => _lastApplied.Rate;
        public bool AtIdle { get { lock (_lock) return _running.Count == 0; } }
        public string UpdateVersion { get; private set; } = "";   // set if a newer release exists ("" = up to date/unknown)
        public volatile bool ShowGuiRequested;   // set by IPC "showgui" when the exe is opened again
        public bool UpdateChecked;                // true once a check has completed (for the GUI)
        public string CheckForUpdatesNow()
        {
            try { UpdateVersion = UpdateChecker.NewerVersion(Program.AppVersion()); } catch { }
            UpdateChecked = true;
            return UpdateVersion;
        }
        // True when the currently-applied format equals the idle/audiophile tier - i.e. NOT lowered.
        public bool CurrentIsIdleFormat
        {
            get { var idle = _config.ProfileTiers[_config.IdleTier]; return _lastApplied.Rate == idle.Rate && _lastApplied.Bits == idle.Bits; }
        }
        public bool Paused { get; private set; }
        public List<string> RunningGameNames()
        {
            lock (_lock) return _running.Values.Select(g => $"{g.Exe}  ({g.Profile.Rate}/{g.Profile.Bits})").ToList();
        }
        public void RequestShutdown() => _shutdown.Set();
        public void SetPaused(bool p)
        {
            Paused = p;
            Log(p ? "Paused - holding idle profile, ignoring games" : "Resumed");
            // Apply OFF the caller's thread: this is invoked from the GUI/tray UI thread, and the
            // format change is a blocking COM call that would otherwise freeze the window.
            ThreadPool.QueueUserWorkItem(_ => { try { ApplyEffective(); } catch { } });
        }

        public void Run()
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _shutdown.Set(); };

            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine();
                Console.WriteLine("   ##  #  # ###  ###  ##   ### #   # ### ###  ### #  # #### ###");
                Console.WriteLine("  #  # #  # #  #  #  #  # #    #   #  #   #  #    #  # #    #  #");
                Console.WriteLine("  #### #  # #  #  #  #  #  ##  # # #  #   #  #    #### ###  ###");
                Console.WriteLine("  #  # #  # #  #  #  #  #    # ## ##  #   #  #    #  # #    # #");
                Console.WriteLine("  #  #  ##  ###  ###  ##  ###  #   # ###  #   ### #  # #### #  #");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  per-game audio format switcher            by @thetrueartist");
                Console.ResetColor();
                Console.WriteLine();
            }
            catch { }
            var idle = _config.ProfileTiers[_config.IdleTier];
            Log($"Device : {_endpoint.Name}");
            Log($"Idle   : {idle.Rate} Hz / {idle.Bits}-bit ({idle.Name})");
            Log($"State  : {StateStore.ConfigDir}");
            Log("");

            // Apply idle profile at startup
            ApplyEffective();

            // Start ETW
            try
            {
                EtwMonitor.OnProcessStartFast = FastProcessStart;   // near-instant known-game launches
                EtwMonitor.Start();
                Log("ETW session active");
            }
            catch (Exception ex)
            {
                Log($"[warn] ETW failed: {ex.Message} - falling back to crash-only learning");
            }

            // WMI subscriptions
            try
            {
                _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _startWatcher.EventArrived += OnProcessStart;
                _startWatcher.Start();

                _stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                _stopWatcher.EventArrived += OnProcessStop;
                _stopWatcher.Start();
                Log("Process watchers active");
            }
            catch (Exception ex)
            {
                Log($"[fatal] WMI subscription failed: {ex.Message}");
                return;
            }

            // Background threads
            _glitchThread = new Thread(GlitchPump) { IsBackground = true, Name = "GlitchPump" };
            _glitchThread.Start();
            _ipcThread = new Thread(IpcServer) { IsBackground = true, Name = "IPC" };
            _ipcThread.Start();
            _autoLearnThread = new Thread(AutoLearnPump) { IsBackground = true, Name = "AutoLearn" };
            _autoLearnThread.Start();

            // Validate the peak meter at startup WITHOUT playing any sound: if the endpoint reports a
            // working meter, silence detection can run immediately (no need to wait for some app to
            // play). Still gets reinforced by seeing a real peak >0 later.
            try { if (EndpointManager.MeterAvailable()) { _meterEverPositive = true; if (_verbose) Log("Peak meter present - silence detection ready"); } } catch { }

            // Background update check (GitHub Releases API, notify only) - never blocks startup.
            if (_config.CheckForUpdates)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string v = CheckForUpdatesNow();
                    if (!string.IsNullOrEmpty(v)) Log($"Update available: v{v} (you have v{Program.AppVersion()})");
                });

            Log("Active. Ctrl-C to exit.");
            Log("");

            _shutdown.Wait();

            // Shutdown
            Log("");
            Log("Shutting down...");
            try { _startWatcher?.Stop(); _startWatcher?.Dispose(); } catch { }
            try { _stopWatcher?.Stop(); _stopWatcher?.Dispose(); } catch { }
            try { EtwMonitor.Stop(); } catch { }
            try
            {
                EndpointManager.SetFormat(_endpoint,
                    _config.ProfileTiers[_config.IdleTier].Rate,
                    _config.ProfileTiers[_config.IdleTier].Bits,
                    _config.Channels, _verbose);
                Log("Restored idle profile.");
            }
            catch (Exception ex) { Log($"[warn] Idle restore failed: {ex.Message}"); }
        }

        // Atomically reserve a pid so the ETW-fast path and the WMI watcher never both handle the
        // same launch. Returns true if this caller claimed it first.
        private bool Claim(int pid) { lock (_lock) { return _claimed.Add(pid); } }

        // Near-instant launch handler, driven by the kernel ETW provider (~1ms after process create,
        // vs WMI's ~1s). Only acts on games we ALREADY know (learned override / known-quirky): for
        // those the safe format is resolved from the exe name alone, so we can drop it (freezing the
        // game) BEFORE it opens its audio device. This is what lets the switch beat a game that inits
        // audio the instant it starts (e.g. Hotline Miami). Unknown games are left to the WMI path.
        private void FastProcessStart(int pid)
        {
            string exe;
            DateTime? started = null;
            try
            {
                using var p = Process.GetProcessById(pid);
                exe = p.ProcessName + ".exe";
                try { started = p.StartTime; } catch { }
            }
            catch { return; }   // already gone or unreadable - WMI still catches it if it's a game
            if (string.IsNullOrEmpty(exe)) return;
            if (_config.IgnoreProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase))) return;

            var overrides = StateStore.LoadOverrides();
            overrides.TryGetValue(exe, out var ov);
            if (ov == null && !_config.KnownQuirky.ContainsKey(exe)) return;   // unknown -> let WMI handle it
            if (!Claim(pid)) return;                                           // WMI already took this pid

            // How long after the process actually started did we catch it? This is the number that
            // decides whether the freeze beats the game's audio init - log it so it's verifiable.
            if (started.HasValue)
                Log($"ETW caught {exe} {(int)(DateTime.Now - started.Value).TotalMilliseconds}ms after launch");
            RegisterAndApply(pid, exe, "", ov?.Engine ?? "", ov, overrides,
                             ov != null ? "override/etw" : "known-quirky/etw", allowSuspend: true);
        }

        private void OnProcessStart(object? sender, EventArrivedEventArgs e)
        {
            try
            {
                int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                int ppid = Convert.ToInt32(e.NewEvent.Properties["ParentProcessID"].Value);
                string exe = e.NewEvent.Properties["ProcessName"].Value?.ToString() ?? "";

                // Fastest path: a game we've already learned (override) or that's known-quirky is
                // fully resolved from its exe name alone - no path read, no engine fingerprint. Apply
                // its safe format the INSTANT the PID appears, before the game opens its audio device.
                // This is the case that matters for "switch too slow": a repeat launch of a game we
                // already KNOW needs a lower rate. Skipping the ~250ms path probe and ~800ms
                // fingerprint below cuts ~1s of latency, and with SuspendDuringSwitch the game is
                // frozen across the switch so it cannot lose the race at all.
                var overrides = StateStore.LoadOverrides();
                overrides.TryGetValue(exe, out var ovEntry);   // null if none learned
                bool ignored = _config.IgnoreProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase));
                if (!ignored && (ovEntry != null || _config.KnownQuirky.ContainsKey(exe)))
                {
                    // ETW's fast path usually claimed this already (and applied before the game hit
                    // audio); only handle it here if it didn't.
                    if (Claim(pid))
                        RegisterAndApply(pid, exe, "", ovEntry?.Engine ?? "", ovEntry, overrides,
                                         ovEntry != null ? "override" : "known-quirky", allowSuspend: true);
                    return;
                }

                Thread.Sleep(250);
                string path = "";
                // UWP/Store games (e.g. Minecraft Bedrock) run in an AppContainer - MainModule is
                // unreadable even elevated. Don't bail: fall back to the WMI process name so the
                // GameProcesses/IgnoreProcesses name checks still work.
                try { using var proc = Process.GetProcessById(pid); path = proc.MainModule?.FileName ?? ""; }
                catch { }

                var detection = GameDetection.IsGame(path, exe, ppid, _config);
                if (detection == null) return;

                // Never-seen game: fingerprint the engine so a first-timer starts at its engine's
                // default tier (e.g. UE one down). No suspend here - we don't yet know the rate, and
                // freezing across the 800ms fingerprint would stall the launch for no benefit.
                Thread.Sleep(800);
                string engine = GameDetection.Fingerprint(pid, path);
                if (Claim(pid))
                    RegisterAndApply(pid, exe, path, engine, null, overrides, detection.Value.reason,
                                     allowSuspend: false);
            }
            catch (Exception ex)
            {
                if (_verbose) Log($"[warn] start handler: {ex.Message}");
            }
        }

        // Resolve the profile, register the running game, and apply the format - shared by the fast
        // (known-game) and slow (first-seen) launch paths. When allowSuspend is set and the user has
        // opted into SuspendDuringSwitch, the game is frozen across the format change so it cannot
        // open its audio device at the old rate first (the definitive fix for switch-too-slow).
        private void RegisterAndApply(int pid, string exe, string path, string engine,
                                      OverrideEntry? ovEntry, Dictionary<string, OverrideEntry> overrides,
                                      string reason, bool allowSuspend)
        {
            var profile = ResolveProfile(exe, engine);
            int tier = profile.tier, rate = profile.rate, bits = profile.bits;
            string source = profile.source;
            bool isProbe = false;

            // Upward probe: every Nth launch of an overridden game, retry one tier HIGHER to
            // discover the true ceiling / self-heal an over-drop. If it fails, normal learning
            // re-drops it; if it survives a clean session, OnProcessStop promotes it.
            if (ovEntry != null && _config.ProbeEveryLaunches > 0 && ovEntry.TierIdx > 0)
            {
                if (++ovEntry.Launches >= _config.ProbeEveryLaunches)
                {
                    ovEntry.Launches = 0;
                    tier = ovEntry.TierIdx - 1;
                    var pt = _config.ProfileTiers[tier];
                    rate = pt.Rate; bits = pt.Bits; source = $"probe up from tier {ovEntry.TierIdx}";
                    isProbe = true;
                }
                StateStore.SaveOverrides(overrides);   // persist launch counter / reset
            }

            lock (_lock)
            {
                _running[pid] = new RunningGame
                {
                    Pid = pid, Exe = exe, Engine = engine, ExePath = path,
                    StartedAt = DateTime.Now,
                    Profile = new ProfileTier { Rate = rate, Bits = bits, Name = "" },
                    TierIdx = tier,
                    IsProbe = isProbe
                };
            }
            Log($"+ {exe}  [{reason} | {engine} | tier {tier} {source}]");

            // Freeze the game across the switch so it physically cannot open audio at the old rate
            // before the new format is live (known games only - we resolve their rate instantly).
            // On by default, but auto-skipped when an anti-cheat driver is loaded so it never fires
            // on a protected online title. Best-effort - if suspend fails we still apply. The freeze
            // lasts only the ~tens-of-ms synchronous switch.
            IntPtr frozen = IntPtr.Zero;
            if (allowSuspend && _config.SuspendDuringSwitch)
            {
                string? ac = _config.SuspendSkipIfAntiCheat ? ProcessControl.AntiCheatDriverLoaded() : null;
                if (ac != null)
                    Log($"  not freezing {exe}: anti-cheat driver loaded ({ac})");
                else
                {
                    frozen = ProcessControl.Suspend(pid);
                    Log(frozen != IntPtr.Zero ? $"  froze {exe} across switch"
                                              : $"  freeze failed for {exe} (applying anyway)");
                }
            }
            try { ApplyEffective(); }
            finally { ProcessControl.Resume(frozen); }
        }

        private void OnProcessStop(object? sender, EventArrivedEventArgs e)
        {
            try
            {
                int pid = Convert.ToInt32(e.NewEvent.Properties["ProcessID"].Value);
                RunningGame? game = null;
                lock (_lock) { _running.TryGetValue(pid, out game); _running.Remove(pid); _claimed.Remove(pid); }
                if (game == null) return;

                // Process exit code distinguishes a crash from a clean quit: a crash exits with
                // its exception code (e.g. 0xC0000005 access violation) -> non-zero; a normal
                // quit exits 0. Far more reliable than a lifetime-only timer.
                uint exitCode = 0;
                try { exitCode = Convert.ToUInt32(e.NewEvent.Properties["ExitStatus"].Value); } catch { }
                var lifetime = DateTime.Now - game.StartedAt;
                bool crashed = exitCode != 0;
                Log($"- {game.Exe} (lifetime {lifetime.TotalSeconds:F0}s, exit 0x{exitCode:X8} {(crashed ? "CRASH" : "clean")})");

                // Audio-crash only when it actually crashed (non-zero exit) AND died young.
                // A clean quit, however quick, is the user leaving - never bump for that.
                if (crashed && lifetime.TotalSeconds < _config.CrashThresholdSeconds)
                {
                    Thread.Sleep(2000);  // let dumps flush
                    bool evidence = CrashScanner.HasAudioEvidence(game.ExePath);
                    string reason = evidence ? $"crash 0x{exitCode:X8} + audio frame evidence"
                                             : $"crash 0x{exitCode:X8} on startup";
                    Log($"  {reason}");

                    var cl = StateStore.LoadCrashLog();
                    if (!cl.ContainsKey(game.Exe)) cl[game.Exe] = new();
                    cl[game.Exe].Add(new Dictionary<string, object>
                    {
                        ["When"] = DateTime.Now.ToString("o"),
                        ["Lifetime"] = (int)lifetime.TotalSeconds,
                        ["ExitCode"] = $"0x{exitCode:X8}",
                        ["AudioEvidence"] = evidence
                    });
                    StateStore.SaveCrashLog(cl);

                    BumpProfileDown(game.Exe, game.Engine, reason);
                }
                else if (!crashed && game.IsProbe && !game.Failed &&
                         lifetime.TotalSeconds >= _config.CrashThresholdSeconds)
                {
                    // An upward probe survived a full clean session at the higher tier -> promote.
                    var ov = StateStore.LoadOverrides();
                    if (ov.TryGetValue(game.Exe, out var e2))
                    {
                        if (game.TierIdx <= 0)
                        {
                            ov.Remove(game.Exe);
                            Log($"  {game.Exe}: ran clean at max -> learned override cleared");
                        }
                        else
                        {
                            var t = _config.ProfileTiers[game.TierIdx];
                            e2.TierIdx = game.TierIdx; e2.Rate = t.Rate; e2.Bits = t.Bits;
                            e2.Reason = "probe promoted"; e2.Updated = DateTime.Now.ToString("o"); e2.Launches = 0;
                            Log($"  {game.Exe}: probe survived -> promoted to tier {game.TierIdx} ({t.Rate}/{t.Bits})");
                        }
                        StateStore.SaveOverrides(ov);
                    }
                }
                ApplyEffective();
            }
            catch (Exception ex)
            {
                if (_verbose) Log($"[warn] stop handler: {ex.Message}");
            }
        }

        private void GlitchPump()
        {
            while (!_shutdown.IsSet)
            {
                if (_shutdown.Wait(500)) break;

                bool anyRunning;
                lock (_lock) { anyRunning = _running.Count > 0; }
                if (!anyRunning) continue;

                int processed = 0;
                while (EtwMonitor.Events.TryDequeue(out var ev) && processed++ < 200)
                {
                    RunningGame? target = null;
                    lock (_lock)
                    {
                        // Try direct PID correlation
                        if (_running.TryGetValue((int)ev.Pid, out var direct))
                            target = direct;
                        // Fallback: if only one game running, attribute to it
                        else if (_running.Count == 1)
                            target = _running.Values.First();
                    }
                    if (target == null) continue;

                    target.GlitchHistory.Add(ev.When);
                    var cutoff = DateTime.Now.AddSeconds(-_config.GlitchWindowSeconds);
                    target.GlitchHistory.RemoveAll(t => t < cutoff);

                    if (target.GlitchHistory.Count >= _config.GlitchThreshold)
                    {
                        Log($"! {target.Exe}: {target.GlitchHistory.Count} glitches in {_config.GlitchWindowSeconds}s");

                        var gl = StateStore.LoadGlitchLog();
                        if (!gl.ContainsKey(target.Exe)) gl[target.Exe] = new();
                        gl[target.Exe].Add(new Dictionary<string, object>
                        {
                            ["When"] = DateTime.Now.ToString("o"),
                            ["Count"] = target.GlitchHistory.Count
                        });
                        StateStore.SaveGlitchLog(gl);

                        target.Failed = true;
                        if (BumpProfileDown(target.Exe, target.Engine, "ETW glitch storm"))
                        {
                            var prof = ResolveProfile(target.Exe, target.Engine);
                            target.TierIdx = prof.tier;
                            target.Profile = new ProfileTier { Rate = prof.rate, Bits = prof.bits };
                            ApplyEffective();
                        }
                        target.GlitchHistory.Clear();
                    }
                }

                // Silence detection (opt-in via SilenceWindowSeconds > 0): a game whose audio
                // session is Active but whose peak meter stays ~0, and which never produced any
                // sound, failed to open audio at this format. Throttled to ~2s. Exclusive-mode
                // games have no shared session, so they're naturally exempt (no false positive).
                if (_config.SilenceWindowSeconds > 0 &&
                    (DateTime.Now - _lastSilenceCheck).TotalSeconds >= 2)
                {
                    _lastSilenceCheck = DateTime.Now;
                    List<RunningGame> games;
                    lock (_lock) { games = _running.Values.ToList(); }
                    var sessions = games.Count > 0 ? EndpointManager.GetSessions() : null;
                    var now = DateTime.Now;
                    // Self-verify the peak meter: if ANY session ever reads > 0, the reader works.
                    // We only ACT on silence once verified - so a broken/zero-reading meter can NEVER
                    // wrongly flag every game. This is what makes on-by-default safe.
                    if (sessions != null && sessions.Any(x => x.Peak > 0.0005f) && !_meterEverPositive)
                    {
                        _meterEverPositive = true;
                        if (_verbose) Log("Silence detection: peak meter verified working");
                    }
                    if (sessions != null)
                        foreach (var g in games)
                        {
                            if (g.SawAudio) continue;
                            if ((now - g.StartedAt).TotalSeconds < _config.SilenceGraceSeconds) continue;
                            var matches = sessions.Where(x => x.Pid == (uint)g.Pid).ToList();
                            if (matches.Count == 0) { g.SilentSince = null; continue; }   // no audio session -> can't judge
                            var s = matches[0];
                            if (s.State == 1 && s.Peak > 0.0005f) { g.SawAudio = true; g.SilentSince = null; continue; }  // has sound
                            // A session exists but isn't producing sound: Active-but-silent, OR Inactive
                            // (its stream never started - how older games fail at a rate they can't handle).
                            // Both count as silent.
                            g.SilentSince ??= now;
                            if ((now - g.SilentSince.Value).TotalSeconds >= _config.SilenceWindowSeconds && _meterEverPositive)
                            {
                                string how = s.State == 1 ? "session Active but silent" : "session never started (Inactive)";
                                Log($"! {g.Exe}: {how} {_config.SilenceWindowSeconds}s -> format likely unusable");
                                g.Failed = true;
                                if (BumpProfileDown(g.Exe, g.Engine, "running but silent"))
                                {
                                    var prof = ResolveProfile(g.Exe, g.Engine);
                                    g.TierIdx = prof.tier;
                                    g.Profile = new ProfileTier { Rate = prof.rate, Bits = prof.bits };
                                    ApplyEffective();
                                }
                                g.SilentSince = null;
                            }
                        }
                }
            }
        }

        private DateTime _lastSilenceCheck = DateTime.MinValue;
        private bool _meterEverPositive;   // set once we've seen the peak meter read >0 (proves it works)

        // Auto-learn: watch for an exclusive-fullscreen D3D app that isn't already known, and after
        // it stays foreground long enough, add its exe to GameProcesses so its NEXT launch is caught
        // at process-start (this session was detected too late to manage without disrupting audio).
        private void AutoLearnPump()
        {
            string candidate = ""; DateTime since = DateTime.MinValue;
            while (!_shutdown.IsSet)
            {
                if (_shutdown.Wait(2000)) break;
                if (_config.AutoLearnFullscreenSeconds <= 0) { candidate = ""; continue; }

                string exe = Foreground.FullscreenGameExe();
                if (string.IsNullOrEmpty(exe)) { candidate = ""; continue; }

                bool known = _config.GameProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase))
                          || _config.IgnoreProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase));
                if (known) { candidate = ""; continue; }

                if (!string.Equals(exe, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    candidate = exe; since = DateTime.Now; continue;   // start timing this exe
                }
                if ((DateTime.Now - since).TotalSeconds >= _config.AutoLearnFullscreenSeconds)
                {
                    LearnGameProcess(exe);
                    candidate = "";   // don't immediately re-learn
                }
            }
        }

        private void LearnGameProcess(string exe)
        {
            try
            {
                var cfg = StateStore.LoadConfig();   // load fresh so we don't clobber the user's edits
                if (cfg.GameProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase))) return;
                cfg.GameProcesses.Add(exe);
                StateStore.Save(cfg, StateStore.ConfigFile);
                if (!_config.GameProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)))
                    _config.GameProcesses.Add(exe);   // reflect immediately in the running daemon
                Log($"Learned '{exe}' as a game (exclusive fullscreen) - it'll be managed from its next launch.");
            }
            catch (Exception ex) { if (_verbose) Log($"[warn] auto-learn: {ex.Message}"); }
        }

        private (int rate, int bits, int tier, string source) ResolveProfile(string exe, string engine)
        {
            int last = _config.ProfileTiers.Count - 1;
            int Clamp(int i) => i < 0 ? 0 : i > last ? last : i;

            var overrides = StateStore.LoadOverrides();
            if (overrides.TryGetValue(exe, out var ov))
                return (ov.Rate, ov.Bits, Clamp(ov.TierIdx), $"override(locked={ov.Locked})");

            if (_config.KnownQuirky.TryGetValue(exe, out var quirky))
            {
                int qi = Clamp(quirky.Tier);
                var tier = _config.ProfileTiers[qi];
                return (tier.Rate, tier.Bits, qi, "known-quirky");
            }

            int idx = Clamp(_config.EngineDefaults.TryGetValue(engine, out var v) ? v : 0);
            var t = _config.ProfileTiers[idx];
            return (t.Rate, t.Bits, idx, $"engine:{engine}");
        }

        private bool BumpProfileDown(string exe, string engine, string reason)
        {
            var overrides = StateStore.LoadOverrides();
            int currentIdx;
            bool locked = false;
            if (overrides.TryGetValue(exe, out var ov))
            {
                currentIdx = ov.TierIdx;
                locked = ov.Locked;
            }
            else
            {
                var current = ResolveProfile(exe, engine);
                currentIdx = current.tier;
            }
            if (locked) { Log("  Profile locked"); return false; }

            int nextIdx = Math.Min(_config.ProfileTiers.Count - 1, currentIdx + 1);
            if (nextIdx == currentIdx) { Log("  Already at lowest tier"); return false; }

            var next = _config.ProfileTiers[nextIdx];
            overrides[exe] = new OverrideEntry
            {
                TierIdx = nextIdx, Rate = next.Rate, Bits = next.Bits,
                Engine = engine, Reason = reason,
                Updated = DateTime.Now.ToString("o")
            };
            StateStore.SaveOverrides(overrides);
            Log($"  Learned: {exe} -> tier {nextIdx} ({next.Rate}/{next.Bits})  [{reason}]");
            return true;
        }

        private void ApplyEffective()
        {
            // Serialized: the UI pause toggle, the WMI start/stop handlers and the glitch pump all
            // call this. Without this lock, two concurrent applies can leave the device format and
            // _lastApplied out of sync, so later switches get wrongly skipped ("stops switching").
            lock (_applyLock)
            {
                ProfileTier target;
                bool anyRunning;
                lock (_lock)
                {
                    anyRunning = _running.Count > 0;
                    if (Paused || !anyRunning)   // paused -> hold the idle/max profile
                        target = _config.ProfileTiers[_config.IdleTier];
                    else
                        target = _running.Values
                            .Select(g => g.Profile)
                            .OrderBy(p => p.Rate).ThenBy(p => p.Bits)
                            .First();
                }
                if (target.Rate != _lastApplied.Rate || target.Bits != _lastApplied.Bits)
                {
                    string reason = Paused ? "paused" : (anyRunning ? "game running" : "idle restore");
                    Log($"=> {target.Rate}Hz / {target.Bits}-bit  [{reason}]");
                    ApplySupported(target);
                }
            }
        }

        // Apply 'desired'; if the device rejects that exact format (unsupported), walk DOWN the
        // tier ladder to the next format it accepts. Keeps the daemon working across DACs that
        // expose different format sets (e.g. no 24-bit) instead of silently failing to switch.
        private void ApplySupported(ProfileTier desired)
        {
            int start = _config.ProfileTiers.FindIndex(t => t.Rate == desired.Rate && t.Bits == desired.Bits);
            if (start < 0) start = 0;
            for (int i = start; i < _config.ProfileTiers.Count; i++)
            {
                var t = _config.ProfileTiers[i];
                try
                {
                    EndpointManager.SetFormat(_endpoint, t.Rate, t.Bits, _config.Channels, _verbose);
                    _lastApplied = t;
                    if (i != start)
                        Log($"  device rejected {desired.Rate}/{desired.Bits}; applied {t.Rate}/{t.Bits}");
                    return;
                }
                catch (Exception ex)
                {
                    if (i == _config.ProfileTiers.Count - 1)
                        Log($"[error] no device-supported format found (last {t.Rate}/{t.Bits}): {ex.Message}");
                }
            }
        }

        // -- Named pipe for status queries from AudioSwitcher.ps1 --------
        private void IpcServer()
        {
            while (!_shutdown.IsSet)
            {
                try
                {
                    using var server = CreatePipe();
                    var connectTask = server.WaitForConnectionAsync();
                    while (!connectTask.IsCompleted && !_shutdown.IsSet) Thread.Sleep(100);
                    if (_shutdown.IsSet) return;

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(server, Encoding.UTF8) { AutoFlush = true };
                    string? cmd = reader.ReadLine();
                    if (cmd == "showgui")
                    {
                        ShowGuiRequested = true;   // the tray timer picks this up and opens the window
                    }
                    else if (cmd == "status")
                    {
                        lock (_lock)
                        {
                            writer.WriteLine($"endpoint={_endpoint.Name}");
                            writer.WriteLine($"current={_lastApplied.Rate}/{_lastApplied.Bits}");
                            writer.WriteLine($"running={_running.Count}");
                            foreach (var g in _running.Values)
                                writer.WriteLine($"game={g.Exe};engine={g.Engine};tier={g.TierIdx};rate={g.Profile.Rate};bits={g.Profile.Bits}");
                        }
                    }
                    else if (cmd == "reload")
                    {
                        ReloadLists();   // pick up --exclude / --unexclude edits without a restart
                        Log("Reloaded exclusion / game lists from config.");
                    }
                    writer.WriteLine("END");
                }
                catch { if (!_shutdown.IsSet) Thread.Sleep(1000); }
            }
        }

        // Refresh the mutable name lists (exclusions / game allowlist) from config.json without a
        // restart - used after the CLI edits them (--exclude/--unexclude). Mirrors how auto-learn
        // already mutates _config.GameProcesses live on this same daemon instance.
        public void ReloadLists()
        {
            try
            {
                var fresh = StateStore.LoadConfig();
                _config.IgnoreProcesses.Clear(); _config.IgnoreProcesses.AddRange(fresh.IgnoreProcesses);
                _config.GameProcesses.Clear();   _config.GameProcesses.AddRange(fresh.GameProcesses);
            }
            catch { }
        }

        // The daemon runs elevated (scheduled task, Highest), so a normal-privilege client
        // (.\AudioSwitcher.ps1 -Status, or re-opening the exe) can't open a default-ACL pipe ->
        // "Access denied". Grant only the CURRENT USER read/write - enough for the same-user client
        // at any integrity level, but not other users on a shared machine (tighter than Authenticated
        // Users, which the harmless showgui/status commands don't need exposed that widely).
        private static NamedPipeServerStream CreatePipe()
        {
            try
            {
                var sec = new System.IO.Pipes.PipeSecurity();
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (me == null) throw new InvalidOperationException("no current-user SID");
                sec.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
                    me,
                    System.IO.Pipes.PipeAccessRights.ReadWrite,
                    System.Security.AccessControl.AccessControlType.Allow));
                return NamedPipeServerStreamAcl.Create("AudioSwitcherPipe", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, sec);
            }
            catch
            {
                return new NamedPipeServerStream("AudioSwitcherPipe", PipeDirection.InOut, 1);
            }
        }

        private readonly object _logLock = new();
        private void Log(string msg)
        {
            string stamp = $"[{DateTime.Now:HH:mm:ss}] ";
            var color = ColorFor(msg);
            try
            {
                Console.Write(stamp);
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.WriteLine(msg);
                if (color.HasValue) Console.ResetColor();
            }
            catch { Console.WriteLine(stamp + msg); }

            // Also append to a log file - an installed (scheduled-task) daemon has no console.
            try
            {
                lock (_logLock)
                {
                    string f = Path.Combine(StateStore.ConfigDir, "daemon.log");
                    try { if (File.Exists(f) && new FileInfo(f).Length > 5_000_000) File.Move(f, f + ".old", true); } catch { }
                    File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        // Colour-code console output by the message's leading marker for readability.
        private static ConsoleColor? ColorFor(string m)
        {
            if (m.StartsWith("=>")) return ConsoleColor.Green;      // format applied
            if (m.StartsWith("+"))  return ConsoleColor.Cyan;       // game started
            if (m.StartsWith("-"))  return ConsoleColor.DarkCyan;   // game exited
            if (m.StartsWith("!"))  return ConsoleColor.Yellow;     // glitch/silence warning
            if (m.Contains("[error]") || m.Contains("[fatal]")) return ConsoleColor.Red;
            if (m.Contains("[warn]"))  return ConsoleColor.Yellow;
            if (m.Contains("Learned") || m.Contains("promoted") || m.Contains("Restored")) return ConsoleColor.Magenta;
            return null;
        }
    }

    // ====================================================================
    // SYSTEM TRAY UI (WinForms) - the friendly face of the daemon
    // ====================================================================
    public static class IconFactory
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

        // Draw a simple speaker glyph on a state-coloured disc, at runtime (no .ico asset needed).
        // Green = idle/max quality, Amber = a game lowered the rate, Grey = paused.
        public static Icon Make(Color disc)
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(disc)) g.FillEllipse(b, 1, 1, 30, 30);
                using var white = new SolidBrush(Color.White);
                using var pen = new Pen(Color.White, 2f);
                // speaker body + cone
                g.FillRectangle(white, 9, 13, 4, 6);
                g.FillPolygon(white, new[] { new Point(13, 13), new Point(18, 8), new Point(18, 24), new Point(13, 19) });
                // sound waves
                g.DrawArc(pen, 15, 8, 8, 16, -55, 110);
                g.DrawArc(pen, 12, 5, 14, 22, -55, 110);
            }
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        // Also usable to write an .ico file (for a shortcut / exe icon) via --make-icon.
        public static void SaveIco(string path)
        {
            using var ic = Make(Color.FromArgb(46, 160, 67));
            using var fs = File.Create(path);
            ic.Save(fs);
        }
    }

    public static class TrayApp
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_HIDE = 0;

        private static readonly Color Green = Color.FromArgb(46, 160, 67);
        private static readonly Color Amber = Color.FromArgb(219, 149, 20);
        private static readonly Color Grey  = Color.FromArgb(110, 118, 129);

        public static void Run(Daemon daemon)
        {
            // Hide the console window - the tray is the UI now.
            try { var h = GetConsoleWindow(); if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var menu = new ContextMenuStrip();
            var hdr = new ToolStripMenuItem("AudioSwitcher v" + Program.AppVersion()) { Enabled = false };
            var open = new ToolStripMenuItem("Open control panel...") { Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold) };
            var update = new ToolStripMenuItem("") { Visible = false, ForeColor = Color.FromArgb(30, 120, 40) };
            var status = new ToolStripMenuItem("starting...") { Enabled = false };
            var games = new ToolStripMenuItem("No games running") { Enabled = false };
            var pause = new ToolStripMenuItem("Pause switching");
            var logs = new ToolStripMenuItem("Open log folder");
            var quit = new ToolStripMenuItem("Quit");

            var tray = new NotifyIcon
            {
                Icon = IconFactory.Make(Green),
                Text = "AudioSwitcher",
                Visible = true,
                ContextMenuStrip = menu
            };

            void OpenWindow() => MainWindow.ShowSingleton(daemon);
            open.Click += (_, __) => OpenWindow();
            tray.DoubleClick += (_, __) => OpenWindow();
            update.Click += (_, __) => MainWindow.ShowAndInstall(daemon);
            pause.Click += (_, __) => { daemon.SetPaused(!daemon.Paused); };
            logs.Click += (_, __) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = StateStore.ConfigDir, UseShellExecute = true }); } catch { }
            };
            quit.Click += (_, __) =>
            {
                tray.Visible = false;
                daemon.RequestShutdown();
                Application.Exit();
            };

            menu.Items.AddRange(new ToolStripItem[]
            {
                hdr, update, new ToolStripSeparator(), open, status, games,
                new ToolStripSeparator(), pause, logs,
                new ToolStripSeparator(), quit
            });

            // Refresh the icon colour, tooltip and menu every second.
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            Color lastColor = Green;
            bool updateNotified = false;
            timer.Tick += (_, __) =>
            {
                // Someone re-opened the exe (or clicked the Start Menu shortcut) -> show the window.
                if (daemon.ShowGuiRequested) { daemon.ShowGuiRequested = false; OpenWindow(); }

                // Green = at full/idle quality (even if a game is running), amber = actually lowered, grey = paused.
                Color c = daemon.Paused ? Grey : (daemon.CurrentIsIdleFormat ? Green : Amber);
                string state = daemon.Paused ? "paused"
                    : (daemon.AtIdle ? "idle" : (daemon.CurrentIsIdleFormat ? "game running, full quality" : "game running, lowered"));
                string tip = $"AudioSwitcher\n{daemon.CurrentFormat}  ({state})";
                if (tip.Length > 127) tip = tip.Substring(0, 127);
                tray.Text = tip;

                status.Text = $"{daemon.CurrentFormat}  -  {state}";
                var g = daemon.RunningGameNames();
                games.Text = g.Count == 0 ? "No games running" : string.Join("\n", g);
                pause.Checked = daemon.Paused;
                pause.Text = daemon.Paused ? "Resume switching" : "Pause switching";

                // Update notification (set by the background check).
                if (!string.IsNullOrEmpty(daemon.UpdateVersion))
                {
                    update.Text = $"Update available: v{daemon.UpdateVersion}  (click to install)";
                    update.Visible = true;
                    if (!updateNotified)
                    {
                        updateNotified = true;
                        try { tray.BalloonTipTitle = "AudioSwitcher update available";
                              tray.BalloonTipText = $"v{daemon.UpdateVersion} is out (you have v{Program.AppVersion()}). Click the tray icon."; tray.ShowBalloonTip(6000); } catch { }
                    }
                }

                if (c != lastColor)
                {
                    var old = tray.Icon;
                    tray.Icon = IconFactory.Make(c);
                    old?.Dispose();
                    lastColor = c;
                }
            };
            timer.Start();

            // Balloon so the user notices it started.
            try { tray.BalloonTipTitle = "AudioSwitcher running";
                  tray.BalloonTipText = $"Managing {daemon.DeviceName}"; tray.ShowBalloonTip(3000); } catch { }

            // First run (e.g. just after Install) -> open the control panel so the user sees the GUI.
            // A marker file gates it so it doesn't pop up at every logon.
            try
            {
                string marker = Path.Combine(StateStore.ConfigDir, ".gui-shown");
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, "shown");
                    MainWindow.ShowSingleton(daemon);
                }
            }
            catch { }

            Application.Run();   // pumps messages until Application.Exit()
            tray.Dispose();
        }
    }

    // Auto-start via the logon scheduled task, managed from the GUI (the daemon runs
    // elevated, so it can create/remove the task). Mirrors what AudioSwitcher.ps1 -Install does.
    public static class AutoStart
    {
        private const string TaskName = "AudioSwitcherDaemon";

        public static bool IsEnabled() => Run($"/query /tn \"{TaskName}\"") == 0;

        // Program Files\AudioSwitcher\AudioSwitcher.exe - admin-only-writable, so a normal user
        // can't replace the binary that the task runs ELEVATED at logon (that would be a local EoP).
        public static string SecureExePath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "AudioSwitcher", "AudioSwitcher.exe");

        public static void Enable()
        {
            // Register the task against the admin-only Program Files copy, relocating the exe there
            // if we're running from somewhere user-writable. If we can't write Program Files (e.g. not
            // elevated), fall back to the current path so the feature still works.
            string cur = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (cur.Length == 0) return;
            string exe = cur;
            try
            {
                string dst = SecureExePath();
                if (!string.Equals(cur, dst, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);   // inherits PF admin-only ACL
                    File.Copy(cur, dst, true);
                    exe = dst;
                }
            }
            catch { exe = cur; }
            Run($"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\"\" /sc onlogon /rl highest /f");
        }

        public static void Disable() => Run($"/delete /tn \"{TaskName}\" /f");

        private static int Run(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                p.WaitForExit(8000);
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }

    // ====================================================================
    // GUI CONTROL PANEL (WinForms) - the "real window" beyond the tray menu
    // ====================================================================
    public class MainWindow : Form
    {
        private static MainWindow? _instance;
        public static void ShowSingleton(Daemon d)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new MainWindow(d);
                _instance.Show();
            }
            if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
            _instance.Activate();
            _instance.BringToFront();
        }

        public static void ShowAndInstall(Daemon d) { ShowSingleton(d); _instance?.DoInstall(); }

        private bool _installing;
        public void DoInstall()
        {
            if (_installing || string.IsNullOrEmpty(_d.UpdateVersion)) return;
            // No download URL (e.g. no zip asset / signing rejected) -> fall back to the release page.
            if (string.IsNullOrEmpty(UpdateChecker.ZipUrl))
            {
                try { Process.Start(new ProcessStartInfo { FileName = UpdateChecker.ReleasesPage, UseShellExecute = true }); } catch { }
                return;
            }
            if (MessageBox.Show(this, $"Download and install v{_d.UpdateVersion}?\nAudioSwitcher will restart.",
                "Install update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _installing = true;
            _lblUpdate.Text = "Downloading update...";
            System.Threading.Tasks.Task.Run(() =>
            {
                string err = SelfUpdate.DownloadAndApply(UpdateChecker.ZipUrl, UpdateChecker.VerifiedSha256,
                    m => { try { BeginInvoke(new Action(() => _lblUpdate.Text = m)); } catch { } });
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (err == "") { _d.RequestShutdown(); Application.Exit(); }   // helper restarts the new exe
                        else
                        {
                            _installing = false;
                            _lblUpdate.Text = $"Update available: v{_d.UpdateVersion} - click to install";
                            MessageBox.Show(this, "Update failed:\n" + err, "AudioSwitcher",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }));
                }
                catch { }
            });
        }

        // On-demand "check for updates" (the link when no update is known).
        public void CheckNow()
        {
            if (_checking) return;
            _checking = true;
            Refresh2();
            System.Threading.Tasks.Task.Run(() =>
            {
                _d.CheckForUpdatesNow();
                try { BeginInvoke(new Action(() => { _checking = false; Refresh2(); })); } catch { }
            });
        }

        private readonly Daemon _d;
        private readonly Label _lblDevice = Lbl(), _lblFormat = Lbl(), _lblState = Lbl();
        private readonly ListBox _games = new() { IntegralHeight = false };
        private readonly ListBox _overrides = new() { IntegralHeight = false };
        private readonly Button _btnPause = new();
        private readonly LinkLabel _lblUpdate = new() { AutoSize = true, LinkColor = Color.FromArgb(30, 120, 40) };
        private bool _checking;
        private readonly CheckBox _chkAuto = new() { Text = "Start automatically at logon", AutoSize = true };
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
        private bool _syncingAuto;

        private static Label Lbl() => new() { AutoSize = true };

        public MainWindow(Daemon d)
        {
            _d = d;
            Text = "AudioSwitcher v" + Program.AppVersion();
            ClientSize = new Size(460, 580);
            MinimumSize = new Size(420, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            try { Icon = IconFactory.Make(Color.FromArgb(46, 160, 67)); } catch { }
            BuildUi();
            _timer.Tick += (_, __) => Refresh2();
            _timer.Start();
            Refresh2();
        }

        private void BuildUi()
        {
            int x = 16, y = 14, w = ClientSize.Width - 32;

            var title = new Label { Text = "AudioSwitcher", AutoSize = true, Location = new Point(x, y),
                Font = new Font(Font.FontFamily, 15, FontStyle.Bold), ForeColor = Color.FromArgb(46, 130, 200) };
            Controls.Add(title);
            var by = new Label { Text = "v" + Program.AppVersion() + "    by @thetrueartist", AutoSize = true, ForeColor = Color.Gray,
                Location = new Point(x + 2, y + 30) };
            Controls.Add(by);
            _lblUpdate.Location = new Point(x + 2, y + 52);
            _lblUpdate.Click += (_, __) => { if (!string.IsNullOrEmpty(_d.UpdateVersion)) DoInstall(); else CheckNow(); };
            Controls.Add(_lblUpdate);
            y += 80;

            _lblDevice.Location = new Point(x, y); Controls.Add(_lblDevice); y += 24;
            _lblFormat.Location = new Point(x, y); _lblFormat.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(_lblFormat); y += 24;
            _lblState.Location = new Point(x, y); Controls.Add(_lblState); y += 32;

            _btnPause.SetBounds(x, y, 150, 30);
            _btnPause.Click += (_, __) => { _d.SetPaused(!_d.Paused); Refresh2(); };
            Controls.Add(_btnPause);

            _chkAuto.Location = new Point(x + 165, y + 6);
            _chkAuto.CheckedChanged += (_, __) =>
            {
                if (_syncingAuto) return;
                if (_chkAuto.Checked) AutoStart.Enable(); else AutoStart.Disable();
                Refresh2();
            };
            Controls.Add(_chkAuto);
            y += 42;

            Controls.Add(new Label { Text = "Running games", AutoSize = true, Location = new Point(x, y),
                ForeColor = Color.Gray }); y += 20;
            _games.SetBounds(x, y, w, 80); Controls.Add(_games); y += 90;

            Controls.Add(new Label { Text = "Learned per-game profiles", AutoSize = true, Location = new Point(x, y),
                ForeColor = Color.Gray }); y += 20;
            _overrides.SetBounds(x, y, w, 150); Controls.Add(_overrides); y += 158;

            var btnClear = new Button { Text = "Clear selected", Location = new Point(x, y) };
            btnClear.Width = 130; btnClear.Height = 30;
            btnClear.Click += (_, __) => ClearSelected();
            Controls.Add(btnClear);

            var btnReset = new Button { Text = "Reset all learning", Location = new Point(x + 140, y) };
            btnReset.Width = 150; btnReset.Height = 30;
            btnReset.Click += (_, __) => ResetLearning();
            Controls.Add(btnReset);

            var btnLogs = new Button { Text = "Open logs", Location = new Point(x + 300, y) };
            btnLogs.Width = 100; btnLogs.Height = 30;
            btnLogs.Click += (_, __) => { try { Process.Start(new ProcessStartInfo { FileName = StateStore.ConfigDir, UseShellExecute = true }); } catch { } };
            Controls.Add(btnLogs);
        }

        private void Refresh2()
        {
            _lblDevice.Text = "Device:  " + _d.DeviceName;
            _lblFormat.Text = "Now:  " + _d.CurrentFormat;
            string state;
            Color stateColor = Color.Black;
            if (_d.Paused) { state = "Paused (holding idle)"; stateColor = Color.FromArgb(110, 118, 129); }
            else if (_d.AtIdle) { state = "Idle - audiophile profile"; stateColor = Color.FromArgb(46, 130, 70); }
            else if (_d.CurrentIsIdleFormat) { state = "Game running - full quality (not lowered)"; stateColor = Color.FromArgb(46, 130, 70); }
            else { state = "Game running - lowered to " + _d.CurrentFormat; stateColor = Color.FromArgb(180, 120, 10); }
            _lblState.Text = "State:  " + state;
            _lblState.ForeColor = stateColor;
            _btnPause.Text = _d.Paused ? "Resume switching" : "Pause switching";

            if (_checking)
                _lblUpdate.Text = "Checking for updates...";
            else if (!string.IsNullOrEmpty(_d.UpdateVersion))
                _lblUpdate.Text = $"Update available: v{_d.UpdateVersion} - click to install";
            else if (_d.UpdateChecked)
                _lblUpdate.Text = "You're up to date - click to check again";
            else
                _lblUpdate.Text = "Check for updates";

            var g = _d.RunningGameNames();
            SyncList(_games, g.Count == 0 ? new List<string> { "(none)" } : g);

            var ov = StateStore.LoadOverrides();
            var lines = ov.Count == 0 ? new List<string> { "(nothing learned yet)" }
                : ov.Select(kv => $"{kv.Key}  ->  tier {kv.Value.TierIdx}  {kv.Value.Rate}/{kv.Value.Bits}"
                    + (kv.Value.Locked ? "  [locked]" : "")).ToList();
            SyncList(_overrides, lines);

            _syncingAuto = true;
            try { _chkAuto.Checked = AutoStart.IsEnabled(); } catch { }
            _syncingAuto = false;
        }

        private static void SyncList(ListBox box, List<string> items)
        {
            // Only rebuild if changed, to preserve selection and avoid flicker.
            if (box.Items.Count == items.Count)
            {
                bool same = true;
                for (int i = 0; i < items.Count; i++)
                    if (!Equals(box.Items[i], items[i])) { same = false; break; }
                if (same) return;
            }
            int sel = box.SelectedIndex;
            box.BeginUpdate();
            box.Items.Clear();
            foreach (var s in items) box.Items.Add(s);
            if (sel >= 0 && sel < box.Items.Count) box.SelectedIndex = sel;
            box.EndUpdate();
        }

        private void ClearSelected()
        {
            if (_overrides.SelectedItem is not string line) return;
            int arrow = line.IndexOf("  ->", StringComparison.Ordinal);
            if (arrow <= 0) return;
            string exe = line.Substring(0, arrow);
            var ov = StateStore.LoadOverrides();
            if (ov.Remove(exe)) { StateStore.SaveOverrides(ov); Refresh2(); }
        }

        private void ResetLearning()
        {
            if (MessageBox.Show(this, "Forget all learned per-game profiles and crash/glitch history?",
                "Reset learning", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var f in new[] { StateStore.OverridesFile, StateStore.CrashLogFile, StateStore.GlitchLogFile })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            Refresh2();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            base.OnFormClosing(e);   // just closes the window; the daemon keeps running in the tray
        }
    }

    // ====================================================================
    // ENTRY POINT + CLI HANDLERS
    // ====================================================================
    public static class Program
    {
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        // Assembly version (set from the git tag by the release workflow; "dev" for local builds).
        public static string AppVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return (v == null || v.Major == 0) ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }

        [STAThread]
        public static int Main(string[] args)
        {
            // WinExe has no console. If launched from a terminal directly, attach to it so CLI
            // output is visible. (PowerShell's `& $exe` captures stdout via a pipe regardless.)
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
            try
            {
                if (args.Contains("--version")) { Console.WriteLine($"AudioSwitcher {AppVersion()}"); return 0; }
                if (args.Contains("--check-update"))
                {
                    string v = UpdateChecker.NewerVersion(AppVersion());
                    Console.WriteLine(string.IsNullOrEmpty(v)
                        ? $"Up to date (v{AppVersion()})."
                        : $"Update available: v{v} (you have v{AppVersion()}).  {UpdateChecker.ReleasesPage}");
                    return 0;
                }
                int auIdx = Array.IndexOf(args, "--apply-update");
                if (auIdx >= 0 && auIdx + 1 < args.Length)
                {
                    // Test the swap in isolation with a local zip (optional 3rd arg = expected sha256).
                    string sha = auIdx + 2 < args.Length && !args[auIdx + 2].StartsWith("--") ? args[auIdx + 2] : "";
                    string err = SelfUpdate.Apply(args[auIdx + 1], sha, Console.WriteLine);
                    Console.WriteLine(err == "" ? "OK - restarting shortly." : "FAILED: " + err);
                    return err == "" ? 0 : 1;
                }
                if (args.Contains("--gen-signing-key"))    return GenSigningKey();
                int sgIdx = Array.IndexOf(args, "--sign-release");
                if (sgIdx >= 0 && sgIdx + 3 < args.Length)
                    return SignRelease(args[sgIdx + 1], args[sgIdx + 2], args[sgIdx + 3]);
                if (args.Contains("--list-devices")) return ListDevices();
                if (args.Contains("--dump-active"))   return DumpProps();
                int miIdx = Array.IndexOf(args, "--make-icon");
                if (args.Contains("--make-icon"))
                {
                    string path = (miIdx + 1 < args.Length && !args[miIdx + 1].StartsWith("--"))
                        ? args[miIdx + 1] : Path.Combine(StateStore.ConfigDir, "AudioSwitcher.ico");
                    IconFactory.SaveIco(path);
                    Console.WriteLine($"Wrote icon: {path}");
                    return 0;
                }
                if (args.Contains("--probe-format"))  return ProbeFormat();
                if (args.Contains("--sessions"))      return DumpSessions();
                int sfIdx = Array.IndexOf(args, "--set-format");
                if (sfIdx >= 0 && sfIdx + 2 < args.Length)
                    return SetFormatCli(int.Parse(args[sfIdx + 1]), int.Parse(args[sfIdx + 2]));
                if (args.Contains("--show-state"))   return ShowState();
                if (args.Contains("--reset"))        return ResetState();
                if (args.Contains("--test-etw"))     return TestEtw();
                if (args.Contains("--test-etw2"))    return TestEtw2();
                if (args.Contains("--status"))       return QueryStatus();

                int lockIdx = Array.IndexOf(args, "--lock");
                if (lockIdx >= 0 && lockIdx + 2 < args.Length)
                    return LockProfile(args[lockIdx + 1], int.Parse(args[lockIdx + 2]));

                int exIdx = Array.IndexOf(args, "--exclude");
                if (exIdx >= 0 && exIdx + 1 < args.Length) return SetExcluded(args[exIdx + 1], true);
                int unexIdx = Array.IndexOf(args, "--unexclude");
                if (unexIdx >= 0 && unexIdx + 1 < args.Length) return SetExcluded(args[unexIdx + 1], false);
                if (args.Contains("--list-excluded")) return ListExcluded();

                bool verbose = args.Contains("--verbose");
                var cfg = StateStore.LoadConfig();
                var ep = EndpointManager.Resolve(cfg);
                if (ep == null)
                {
                    string which = string.IsNullOrWhiteSpace(cfg.TargetDeviceName)
                        ? "default playback device" : $"'{cfg.TargetDeviceName}'";
                    Console.Error.WriteLine($"No audio endpoint for {which}. Run --list-devices.");
                    return 1;
                }
                // Single-instance guard: two daemons would fight over the ETW session, the named
                // pipe and the format writes. If one is already running, don't start another.
                if (AnotherInstanceRunning())
                {
                    if (args.Contains("--console") || args.Contains("--foreground"))
                        Console.Error.WriteLine("AudioSwitcher is already running. Stop it first (tray -> Quit).");
                    else
                        SendPipe("showgui");   // re-opening the exe -> tell the running daemon to open its window
                    return 0;
                }

                var daemon = new Daemon(cfg, ep, verbose);
                // Default launch = system-tray app (what auto-start runs). --console / --foreground
                // keep the classic blocking console daemon with a live colour log.
                if (args.Contains("--console") || args.Contains("--foreground"))
                {
                    daemon.Run();
                }
                else
                {
                    var t = new Thread(daemon.Run) { IsBackground = true, Name = "Daemon" };
                    t.Start();
                    TrayApp.Run(daemon);   // blocks until Quit
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }

        // True if another AudioSwitcher process (not us) is already running. Process-name
        // enumeration works across integrity levels (elevated task daemon vs a normal double-click).
        private static bool AnotherInstanceRunning()
        {
            try
            {
                var me = Process.GetCurrentProcess();
                return Process.GetProcessesByName(me.ProcessName).Any(p => p.Id != me.Id);
            }
            catch { return false; }
        }

        private static int ListDevices()
        {
            // Active endpoints first ("*"); most rows are stale not-present duplicates.
            foreach (var ep in EndpointManager.Enumerate()
                         .OrderByDescending(EndpointManager.IsActive).ThenBy(e => e.Name))
                Console.WriteLine($"{(EndpointManager.IsActive(ep) ? "* " : "  ")}{ep.Name,-42} state={ep.State} guid={ep.Guid}");
            return 0;
        }

        private static int DumpProps()
        {
            foreach (var ep in EndpointManager.Enumerate().Where(EndpointManager.IsActive))
            {
                Console.WriteLine($"=== {ep.Name}  guid={ep.Guid}");
                using var key = Registry.LocalMachine.OpenSubKey($@"{EndpointManager.MMRoot}\{ep.Guid}\Properties");
                if (key != null)
                    foreach (var vn in key.GetValueNames())
                        if (key.GetValue(vn) is string s && s.Length > 0)
                            Console.WriteLine($"  {vn} = {s}");
                Console.WriteLine();
            }
            return 0;
        }

        private static int DumpSessions()
        {
            var sessions = EndpointManager.GetSessions();
            if (sessions.Count == 0) { Console.WriteLine("(no audio sessions on the default render endpoint)"); return 0; }
            float maxPeak = 0;
            foreach (var s in sessions)
            {
                string st = s.State == 1 ? "Active" : s.State == 2 ? "Expired" : "Inactive";
                string name = "?";
                try { using var p = Process.GetProcessById((int)s.Pid); name = p.ProcessName + ".exe"; }
                catch { name = s.Pid == 0 ? "(system)" : "(unknown)"; }
                if (s.Peak > maxPeak) maxPeak = s.Peak;
                Console.WriteLine($"  pid={s.Pid,-7} {st,-8} peak={s.Peak:F4}  {name}");
            }
            Console.WriteLine();
            Console.WriteLine(maxPeak > 0.0005f
                ? $"  Peak meter WORKS (saw {maxPeak:F4}). Silence detection can run."
                : "  All peaks 0 - either nothing is playing, OR the meter can't read. Re-run WHILE audio plays:");
            if (maxPeak <= 0.0005f)
                Console.WriteLine("    if a playing app STILL shows 0, the meter is the problem (not silence).");
            return 0;
        }

        private static int ProbeFormat()
        {
            var cfg = StateStore.LoadConfig();
            var ep = EndpointManager.Resolve(cfg);
            if (ep == null) { Console.Error.WriteLine($"No endpoint matching '{cfg.TargetDeviceName}'."); return 1; }
            Console.WriteLine($"Endpoint: {ep.Name} guid={ep.Guid}");
            Console.WriteLine(EndpointManager.ProbeFormats(ep));
            return 0;
        }

        private static int SetFormatCli(int rate, int bits)
        {
            var cfg = StateStore.LoadConfig();
            var ep = EndpointManager.Resolve(cfg);
            if (ep == null) { Console.Error.WriteLine($"No endpoint matching '{cfg.TargetDeviceName}'."); return 1; }
            Console.WriteLine($"Endpoint: {ep.Name} guid={ep.Guid}");
            try { EndpointManager.SetFormat(ep, rate, bits, cfg.Channels, verbose: true); Console.WriteLine("OK"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine($"FAILED: {ex.Message}"); return 1; }
        }

        private static int ShowState()
        {
            Console.WriteLine("Overrides:");
            var ov = StateStore.LoadOverrides();
            Console.WriteLine(ov.Count == 0 ? "  (none)" :
                JsonSerializer.Serialize(ov, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine("\nCrash log:");
            var cl = StateStore.LoadCrashLog();
            Console.WriteLine(cl.Count == 0 ? "  (none)" :
                JsonSerializer.Serialize(cl, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine("\nGlitch log:");
            var gl = StateStore.LoadGlitchLog();
            Console.WriteLine(gl.Count == 0 ? "  (none)" :
                JsonSerializer.Serialize(gl, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        private static int ResetState()
        {
            foreach (var f in new[] { StateStore.OverridesFile, StateStore.CrashLogFile, StateStore.GlitchLogFile })
                if (File.Exists(f)) File.Delete(f);
            Console.WriteLine("Learned state cleared.");
            return 0;
        }

        private static int TestEtw()
        {
            Console.WriteLine("ETW 30s @ Verbose, ALL levels. Play audio / unplug the DAC / launch a game...");
            EtwMonitor.Start(enableLevel: 5, captureAll: true);   // 5 = Verbose: capture everything to prove the pipe
            var start = DateTime.Now;
            int total = 0, shown = 0;
            while ((DateTime.Now - start).TotalSeconds < 30)
            {
                while (EtwMonitor.Events.TryDequeue(out var ev))
                {
                    total++;
                    if (shown < 40) { shown++;
                        Console.WriteLine($"  [{ev.When:HH:mm:ss.fff}] pid={ev.Pid} id={ev.EventId} lvl={ev.Level} kw=0x{ev.Keyword:X}"); }
                }
                Thread.Sleep(200);
            }
            EtwMonitor.Stop();
            Console.WriteLine($"Done. Captured {total} events (showed first {shown}).");
            return 0;
        }

        // Diagnostic for the research lead: capture the Microsoft.Windows.Audio.Client provider,
        // which fires in-process from the game and should carry the Initialize HRESULT. Launch a
        // game during the 60s window; we want to see events whose pid = the game and a payload that
        // contains an HRESULT (0 = ok, 8-hex-digit 0x8889xxxx = audio failure). This tells us
        // whether it's worth wiring in as a cleaner per-game success/fail sensor.
        private static int TestEtw2()
        {
            Console.WriteLine("ETW 60s: Microsoft.Windows.Audio.Client (6e7b1892-...). Launch a game now.");
            Console.WriteLine("Looking for events whose pid = the game, with an HRESULT in the payload.");
            EtwMonitor.Start(enableLevel: 3, extraProvider: EtwMonitor.AudioClientProvider, captureRaw: true);
            var start = DateTime.Now;
            int total = 0, shown = 0;
            while ((DateTime.Now - start).TotalSeconds < 60)
            {
                while (EtwMonitor.RawEvents.TryDequeue(out var ev))
                {
                    total++;
                    bool isInit = ev.Name.IndexOf("Initialize", StringComparison.OrdinalIgnoreCase) >= 0;
                    // Show every Initialize-family event in full; sample the rest (they're chatty).
                    if (isInit || shown < 80)
                    {
                        shown++;
                        string hex = BitConverter.ToString(ev.Payload).Replace("-", " ");
                        if (!isInit && hex.Length > 72) hex = hex.Substring(0, 72) + " ...";
                        // Scan the payload for an embedded AUDCLNT HRESULT (0x8889xxxx, little-endian).
                        string hr = "";
                        for (int k = 0; k + 4 <= ev.Payload.Length; k++)
                            if (ev.Payload[k + 3] == 0x88 && ev.Payload[k + 2] == 0x89)
                            { hr = $" AUDCLNT=0x{BitConverter.ToUInt32(ev.Payload, k):X8}@{k}"; break; }
                        string nm = ev.Name.Length > 0 ? ev.Name : "(unnamed)";
                        Console.WriteLine($"  {(isInit ? "**" : "  ")}[{ev.When:HH:mm:ss.fff}] pid={ev.Pid,-6} {nm,-28}{hr}  [{ev.Payload.Length}B] {hex}");
                    }
                }
                Thread.Sleep(200);
            }
            EtwMonitor.Stop();
            Console.WriteLine($"Done. Captured {total} Audio.Client events (showed first {shown}).");
            Console.WriteLine("If pids match your game and an HRESULT is visible, this becomes the up-probe's result sensor.");
            return 0;
        }

        private static int LockProfile(string exe, int tier)
        {
            var cfg = StateStore.LoadConfig();
            if (tier < 0 || tier >= cfg.ProfileTiers.Count)
            {
                Console.Error.WriteLine($"Tier must be 0..{cfg.ProfileTiers.Count - 1}");
                return 1;
            }
            var ov = StateStore.LoadOverrides();
            var t = cfg.ProfileTiers[tier];
            ov[exe] = new OverrideEntry
            {
                TierIdx = tier, Rate = t.Rate, Bits = t.Bits,
                Locked = true, Reason = "manual lock",
                Updated = DateTime.Now.ToString("o")
            };
            StateStore.SaveOverrides(ov);
            Console.WriteLine($"Locked {exe} -> tier {tier} ({t.Rate}/{t.Bits})");
            return 0;
        }

        // Exclusions: add/remove an exe from IgnoreProcesses (the "never manage this" denylist that
        // every detection path already honours). Excluding also drops any learned override and game-
        // allowlist entry, so the app is genuinely left alone. Signals the running daemon to reload.
        private static int SetExcluded(string exe, bool add)
        {
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            var cfg = StateStore.LoadConfig();
            bool has = cfg.IgnoreProcesses.Any(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                if (!has) cfg.IgnoreProcesses.Add(exe);
                cfg.GameProcesses.RemoveAll(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase));
                StateStore.Save(cfg, StateStore.ConfigFile);
                var ov = StateStore.LoadOverrides();
                if (ov.Remove(exe)) StateStore.SaveOverrides(ov);
                Console.WriteLine(has ? $"{exe} is already excluded." : $"Excluded {exe} - AudioSwitcher will never manage it.");
            }
            else
            {
                if (has) { cfg.IgnoreProcesses.RemoveAll(n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)); StateStore.Save(cfg, StateStore.ConfigFile); }
                Console.WriteLine(has ? $"Un-excluded {exe}." : $"{exe} was not excluded.");
            }
            SendPipe("reload");   // apply to the running daemon immediately (no restart needed)
            return 0;
        }

        private static int ListExcluded()
        {
            var cfg = StateStore.LoadConfig();
            if (cfg.IgnoreProcesses.Count == 0) { Console.WriteLine("No exclusions."); return 0; }
            Console.WriteLine("Excluded (never managed):");
            foreach (var n in cfg.IgnoreProcesses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine("  " + n);
            return 0;
        }

        // Fire-and-forget a one-word command to the running daemon (e.g. "showgui").
        private static void SendPipe(string cmd)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "AudioSwitcherPipe", PipeDirection.InOut);
                client.Connect(2000);
                using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                writer.WriteLine(cmd);
            }
            catch { }
        }

        private static int QueryStatus()
        {
            try
            {
                using var client = new NamedPipeClientStream(".", "AudioSwitcherPipe", PipeDirection.InOut);
                client.Connect(2000);
                using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8);
                writer.WriteLine("status");
                var kv = new Dictionary<string, string>();
                var games = new List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null && line != "END")
                {
                    if (line.StartsWith("game=")) games.Add(line.Substring(5));
                    else { int eq = line.IndexOf('='); if (eq > 0) kv[line.Substring(0, eq)] = line.Substring(eq + 1); }
                }
                kv.TryGetValue("endpoint", out var ep);
                kv.TryGetValue("current", out var cur);
                Console.WriteLine($"  Device     : {ep}");
                Console.WriteLine($"  Now playing: {cur} Hz/bit");
                if (games.Count == 0)
                    Console.WriteLine("  Games      : (none - idle profile active)");
                else
                {
                    Console.WriteLine($"  Games      : {games.Count} running");
                    foreach (var g in games)
                    {
                        var f = g.Split(';').Select(p => p.Split('=')).Where(a => a.Length == 2)
                                 .ToDictionary(a => a[0], a => a[1]);
                        f.TryGetValue("game", out var name); f.TryGetValue("engine", out var eng);
                        f.TryGetValue("rate", out var r); f.TryGetValue("bits", out var b); f.TryGetValue("tier", out var t);
                        Console.WriteLine($"    - {name,-32} {eng,-12} tier {t}  {r}/{b}");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Daemon not reachable: {ex.Message}");
                return 1;
            }
        }

        // Generate an offline release-signing keypair. Run once, keep the .key file secret/offline,
        // and paste the printed public key into Signing.PublicKeyB64.
        private static int GenSigningKey()
        {
            var (priv, pub) = Signing.GenerateKeyPair();
            string keyPath = Path.Combine(Directory.GetCurrentDirectory(), "AudioSwitcher-signing.key");
            File.WriteAllText(keyPath, priv);
            Console.WriteLine($"Private key written to: {keyPath}");
            Console.WriteLine("KEEP THIS FILE OFFLINE AND SECRET - never commit it or put it in CI/GitHub.");
            Console.WriteLine();
            Console.WriteLine("Embed this public key in Signing.PublicKeyB64:");
            Console.WriteLine(pub);
            return 0;
        }

        // Sign a release asset (the .zip): writes manifest.json + manifest.sig to attach to the
        // GitHub release. Run locally with your offline key, e.g.:
        //   AudioSwitcher.exe --sign-release AudioSwitcher-win-x64.zip AudioSwitcher-signing.key 1.2.1
        private static int SignRelease(string assetPath, string keyFile, string version)
        {
            try
            {
                byte[] asset = File.ReadAllBytes(assetPath);
                string manifest = JsonSerializer.Serialize(new
                {
                    version = version.TrimStart('v', 'V'),
                    asset = Path.GetFileName(assetPath),
                    sha256 = Signing.Sha256Hex(asset)
                });
                string sig = Signing.Sign(Encoding.UTF8.GetBytes(manifest), File.ReadAllText(keyFile).Trim());
                File.WriteAllText("manifest.json", manifest);
                File.WriteAllText("manifest.sig", sig);
                Console.WriteLine("Wrote manifest.json and manifest.sig.");
                Console.WriteLine("Attach BOTH to the GitHub release (alongside the .zip).");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"Sign failed: {ex.Message}"); return 1; }
        }
    }
}
