using arena_dma_radar.Arena.ArenaPlayer;
using arena_dma_radar.UI.Radar; // For MainForm
using arena_dma_radar.UI.Misc; // For Config
using eft_dma_shared.Common.Features; // For IFeature
using eft_dma_shared.Common.Misc; // For LoneLogging, Utils, etc.
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Unity;
using arena_dma_radar.Arena.Features.MemoryWrites; // For Aimbot
using eft_dma_shared.Common.Misc.Data; // For GameData
using eft_dma_shared.Common.Misc.Commercial; // For No Obfuscation attribute
using System.Collections.Concurrent; // For ConcurrentDictionary (if used directly, though _rgtPlayers handles it)
using System.Threading; // For CancellationTokenSource, Thread, Interlocked
using SDK; // For Offsets

namespace arena_dma_radar.Arena.GameWorld {

    public sealed class LocalGameWorld : IDisposable {

        #region Fields/Properties/Constructor(s)

        public static implicit operator ulong(LocalGameWorld x) => x?.Base ?? 0x0;

        public const int MAX_DIST = 500;

        private ulong Base { get; }

        private static readonly WaitTimer _refreshWait = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly RegisteredPlayers _rgtPlayers;
        private readonly GrenadeManager _grenadeManager;
        private readonly Thread _t1; // RealtimeWorker (Player Positions, Camera)
        private readonly Thread _t2; // MiscWorker (Gear, Player Transforms Validation)
        private readonly Thread _t3; // GrenadesWorker
        private readonly Thread _t4; // FastWorker (Hands, Firearm Updates)

        public static Enums.ERaidMode MatchMode { get; private set; }

        public static bool MatchHasTeams {
            get {
                switch (MatchMode) {
                    case Enums.ERaidMode.LastHero:
                        return false;

                    default:
                        return true;
                }
            }
        }

        public bool IsSafeToWriteMem {
            get {
                try {
                    if (MainForm.Window is null || !InRaid)
                        return false;
                    if (Memory.ReadValue<ulong>(LocalPlayer + Offsets.Player.Corpse, false) != 0x0)
                        return false;
                    return IsGameWorldActive();
                } catch {
                    return false;
                }
            }
        }

        public string MapID { get; private set; }
        public bool InRaid => !_disposed;
        public Enums.ERaidMode matchMode => MatchMode;

        public CameraManager CameraManager { get; }
        public IReadOnlyCollection<Player> Players => _rgtPlayers;
        public LocalPlayer LocalPlayer => _rgtPlayers?.LocalPlayer;
        public IReadOnlyCollection<Grenade> Grenades => _grenadeManager;

        private LocalGameWorld(ulong localGameWorld, string mapID) {
            var ct = _cts.Token;
            Base = localGameWorld;
            MapID = mapID;
            _t1 = new Thread(() => { RealtimeWorker(ct); }) {
                IsBackground = true
            };
            _t2 = new Thread(() => { MiscWorker(ct); }) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _t3 = new Thread(() => { GrenadesWorker(ct); }) {
                IsBackground = true
            };
            _t4 = new Thread(() => { FastWorker(ct); }) {
                IsBackground = true
            };
            Player.Reset();
            var rgtPlayersAddr = Memory.ReadPtr(localGameWorld + Offsets.ClientLocalGameWorld.RegisteredPlayers, false);
            _rgtPlayers = new RegisteredPlayers(rgtPlayersAddr, this);
            if (_rgtPlayers.GetPlayerCount() < 1)
                throw new ArgumentOutOfRangeException(nameof(_rgtPlayers));
            CameraManager = new();
            _grenadeManager = new(localGameWorld);
        }

        public void Start() {
            _t1.Start();
            _t2.Start();
            _t3.Start();
            _t4.Start();
        }

        public static LocalGameWorld CreateGameInstance(ulong unityBase) {
            while (true) {
                ResourceJanitor.Run();
                Memory.ThrowIfNotInGame();
                try {
                    var instance = GetLocalGameWorld(unityBase);
                    LoneLogging.WriteLine("Match has started!");
                    return instance;
                } catch (Exception ex) {
                    LoneLogging.WriteLine($"ERROR Instantiating Game Instance: {ex}");
                } finally {
                    Thread.Sleep(1000);
                }
            }
        }

        #endregion Fields/Properties/Constructor(s)

        #region Methods

        private static LocalGameWorld GetLocalGameWorld(ulong unityBase) {
            try {
                var localGameWorld = Memory.ReadPtr(MonoLib.GameWorldField, false);
                var mapPtr = Memory.ReadValue<ulong>(localGameWorld + Offsets.GameWorld.Location, false);
                var map = Memory.ReadUnityString(mapPtr, 64, false);
                LoneLogging.WriteLine("Detected Map " + map);
                if (!GameData.MapNames.ContainsKey(map)) // Ensure GameData is accessible or pass it if needed
                    throw new Exception("Invalid Map ID!");
                var inMatch = Memory.ReadValue<bool>(localGameWorld + Offsets.ClientLocalGameWorld.IsInRaid, false);
                if (!inMatch)
                    throw new Exception("Invalid Match Instance (Hideout?)");
                var networkGame = Memory.ReadPtr(MonoLib.AbstractGameField, false);
                var networkGameData = Memory.ReadPtr(networkGame + Offsets.NetworkGame.NetworkGameData, false);
                var raidMode = Memory.ReadValue<int>(networkGameData + Offsets.NetworkGameData.raidMode, false);
                if (raidMode < 0 || raidMode > 20) // Simplified check
                    throw new ArgumentOutOfRangeException(nameof(raidMode));
                MatchMode = (Enums.ERaidMode)raidMode;
                return new LocalGameWorld(localGameWorld, map);
            } catch (Exception ex) {
                throw new Exception("ERROR Getting LocalGameWorld", ex);
            }
        }

        public void Refresh() {
            try {
                ThrowIfMatchEnded();
                _rgtPlayers.Refresh();
            } catch (RaidEnded) {
                LoneLogging.WriteLine("Match has ended!");
                Dispose();
            } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR - Match ended due to unhandled exception: {ex}");
                throw;
            }
        }

        private void ThrowIfMatchEnded() {
            for (int i = 0; i < 5; i++) {
                try {
                    if (_rgtPlayers.GetPlayerCount() < 1)
                        throw new Exception("Not in match!");
                    return;
                } catch { Thread.Sleep(10); }
            }
            throw new RaidEnded();
        }

        private bool IsGameWorldActive() {
            try {
                var localGameWorld = Memory.ReadPtr(MonoLib.GameWorldField, false);
                ArgumentOutOfRangeException.ThrowIfNotEqual(localGameWorld, this, nameof(localGameWorld));
                var mainPlayer = Memory.ReadPtr(localGameWorld + Offsets.ClientLocalGameWorld.MainPlayer, false);
                ArgumentOutOfRangeException.ThrowIfNotEqual(mainPlayer, _rgtPlayers.LocalPlayer, nameof(mainPlayer));
                return _rgtPlayers.GetPlayerCount() > 0;
            } catch {
                return false;
            }
        }

        #endregion Methods

        #region Realtime Thread T1

        private void RealtimeWorker(CancellationToken ct) {
            if (_disposed) return;
            try {
                LoneLogging.WriteLine("Realtime thread starting...");
                while (InRaid) {
                    if (Program.Config.RatelimitRealtimeReads || !CameraManagerBase.EspRunning || (MemWriteFeature<Aimbot>.Instance.Enabled && Aimbot.Engaged))
                        _refreshWait.AutoWait(TimeSpan.FromMilliseconds(1), 1000);
                    ct.ThrowIfCancellationRequested();
                    RealtimeLoop();
                }
            } catch (OperationCanceledException) { } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR on Realtime Thread: {ex}");
                Dispose();
            } finally {
                LoneLogging.WriteLine("Realtime thread stopping...");
            }
        }

        public void RealtimeLoop() {
            try {
                var players = _rgtPlayers?
                    .Where(x => x.IsActive && x.IsAlive); // Added null check for _rgtPlayers
                var localPlayer = LocalPlayer;
                if (players is null || !players.Any()) {
                    Thread.Sleep(1);
                    return;
                }

                using var scatterMap = ScatterReadMap.Get();
                var round1 = scatterMap.AddRound(false);
                if (CameraManager is CameraManager cm) {
                    cm.OnRealtimeLoop(round1[-1], localPlayer);
                }
                int i = 0;
                foreach (var player in players) {
                    player.OnRealtimeLoop(round1[i++]);
                }

                scatterMap.Execute();
            } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR - UpdatePlayers Loop FAILED: {ex}");
            }
        }

        #endregion Realtime Thread T1

        #region Misc Thread T2

        private void MiscWorker(CancellationToken ct) {
            if (_disposed) return;
            try {
                LoneLogging.WriteLine("Misc thread starting...");
                while (InRaid) {
                    ct.ThrowIfCancellationRequested();
                    UpdateMisc();
                    Thread.Sleep(250); // Refresh gear info etc. at a slower pace
                }
            } catch (OperationCanceledException) { } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR on Misc Thread: {ex}");
                Dispose();
            } finally {
                LoneLogging.WriteLine("Misc thread stopping...");
            }
        }

        /// <summary>
        /// validates player transforms and refreshes player gear.
        /// </summary>
        private void UpdateMisc() {
            try {
                ValidatePlayerTransforms();
                RefreshPlayersFullGear(); // Renamed from GetGear and now calls the correct refresh logic
            } catch (Exception ex) {
                // Consider if the log message should be more generic or specific to UpdateMisc
                LoneLogging.WriteLine($"[MiscWorker] CRITICAL ERROR in UpdateMisc: {ex}");
            }
        }

        public void ValidatePlayerTransforms() {
            try {
                var players = _rgtPlayers?
                    .Where(x => x.IsActive && x.IsAlive); // Added null check
                if (players is not null && players.Any()) {
                    using var scatterMap = ScatterReadMap.Get();
                    var round1 = scatterMap.AddRound();
                    var round2 = scatterMap.AddRound();
                    int i = 0;
                    foreach (var player in players) {
                        player.OnValidateTransforms(round1[i], round2[i]);
                        i++;
                    }
                    scatterMap.Execute();
                }
            } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR - ValidatePlayerTransforms Loop FAILED: {ex}");
            }
        }

        /// <summary>
        /// execute gear manager for observed players.
        /// this method is now responsible for refreshing the gear of all relevant players.
        /// </summary>
        private void RefreshPlayersFullGear() {
            try {
                var players = _rgtPlayers?
                    .Where(x => x.IsActive && x.IsAlive); // Added null check
                if (players is not null) // Ensure players collection is not null
                {
                    foreach (var player in players) {
                        if (player is ArenaObservedPlayer observed) {
                            observed.RefreshGear(); // Call the updated RefreshGear method
                        }
                    }
                }
            } catch (Exception ex) {
                LoneLogging.WriteLine($"[LocalGameWorld] ERROR in RefreshPlayersFullGear: {ex.Message}");
            }
        }

        #endregion Misc Thread T2

        #region Grenades Thread T3

        private void GrenadesWorker(CancellationToken ct) {
            if (_disposed) return;
            try {
                LoneLogging.WriteLine("Grenades thread starting...");
                while (InRaid) {
                    ct.ThrowIfCancellationRequested();
                    _grenadeManager.Refresh();
                    Thread.Sleep(10);
                }
            } catch (OperationCanceledException) { } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR on Grenades Thread: {ex}");
                Dispose();
            } finally {
                LoneLogging.WriteLine("Grenades thread stopping...");
            }
        }

        #endregion Grenades Thread T3

        #region Fast Thread T4

        private void FastWorker(CancellationToken ct) {
            if (_disposed) return;
            try {
                LoneLogging.WriteLine("FastWorker thread starting...");
                while (InRaid) {
                    ct.ThrowIfCancellationRequested();
                    CameraManager.Refresh();
                    RefreshFast(); // Handles Hands and LocalPlayer Firearm updates
                    Thread.Sleep(100);
                }
            } catch (OperationCanceledException) { } catch (Exception ex) {
                LoneLogging.WriteLine($"CRITICAL ERROR on FastWorker Thread: {ex}");
                Dispose();
            } finally {
                LoneLogging.WriteLine("FastWorker thread stopping...");
            }
        }

        private void RefreshFast() {
            try {
                var players = _rgtPlayers?
                    .Where(x => x.IsActive && x.IsAlive); // Added null check
                if (players is not null) // Ensure players collection is not null
                {
                    foreach (var player in players) {
                        if (player is ArenaObservedPlayer observed) {
                            observed.RefreshHands();
                        } else if (player is LocalPlayer localPlayer) {
                            localPlayer.Firearm.Update();
                        }
                    }
                }
            } catch (Exception ex) {
                LoneLogging.WriteLine($"[LocalGameWorld] ERROR in RefreshFast: {ex.Message}");
            }
        }

        #endregion Fast Thread T4

        #region IDisposable

        private bool _disposed = false;

        public void Dispose() {
            bool disposed = Interlocked.Exchange(ref _disposed, true);
            if (!disposed) {
                _cts.Cancel();
                _cts.Dispose();
                // any other resources to dispose of can be added here
            }
        }

        #endregion IDisposable

        #region Types

        public sealed class RaidEnded : Exception {

            public RaidEnded() {
            }

            public RaidEnded(string message) : base(message) {
            }

            public RaidEnded(string message, Exception inner) : base(message, inner) {
            }
        }

        #endregion Types
    }
}