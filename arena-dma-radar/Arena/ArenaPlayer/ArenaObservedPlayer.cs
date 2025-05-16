using arena_dma_radar.Arena.ArenaPlayer.Plugins;
using arena_dma_radar.Arena.GameWorld;
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Misc.Commercial;
using eft_dma_shared.Common.Players;
using eft_dma_shared.Common.Unity;
using SDK; // For Offsets

namespace arena_dma_radar.Arena.ArenaPlayer {

    public sealed class ArenaObservedPlayer : Player {
        private ulong ObservedPlayerController { get; }
        private ulong ObservedHealthController { get; }

        public override string Name { get; }
        public override string AccountID { get; }
        public override int TeamID { get; } = -1;
        public override Enums.EPlayerSide PlayerSide { get; }
        public override bool IsHuman { get; }
        public override ulong MovementContext { get; }
        public override ulong Body { get; }
        public override ulong InventoryControllerAddr { get; } // Stores the address of the field containing the pointer
        public override ulong HandsControllerAddr { get; }
        public override ulong CorpseAddr { get; }
        public override ulong RotationAddress { get; }
        public override Skeleton Skeleton { get; }
        public Enums.ETagStatus HealthStatus { get; private set; } = Enums.ETagStatus.Healthy;

        public GearManager Gear { get; private set; }
        public HandsManager Hands { get; private set; }

        /// <summary>
        /// indicates if this player is carrying the bomb (backpack).
        /// </summary>
        public bool HasBomb => Gear?.HasBomb ?? false;

        internal ArenaObservedPlayer(ulong playerBase) : base(playerBase) {
            var side = (Enums.EPlayerSide)Memory.ReadValue<int>(this + Offsets.ObservedPlayerView.Side, false);
            var cameraType = Memory.ReadValue<int>(this + Offsets.ObservedPlayerView.VisibleToCameraType);
            ArgumentOutOfRangeException.ThrowIfNotEqual(cameraType, (int)Enums.ECameraType.Default, nameof(cameraType));

            ObservedPlayerController = Memory.ReadPtr(this + Offsets.ObservedPlayerView.ObservedPlayerController);
            ArgumentOutOfRangeException.ThrowIfNotEqual(this,
                Memory.ReadValue<ulong>(ObservedPlayerController + Offsets.ObservedPlayerController.Player),
                nameof(ObservedPlayerController));

            ObservedHealthController = Memory.ReadPtr(ObservedPlayerController + Offsets.ObservedPlayerController.HealthController);
            ArgumentOutOfRangeException.ThrowIfNotEqual(this,
                Memory.ReadValue<ulong>(ObservedHealthController + Offsets.ObservedHealthController.Player),
                nameof(ObservedHealthController));

            Body = Memory.ReadPtr(this + Offsets.ObservedPlayerView.PlayerBody);
            // InventoryControllerAddr is the location of the pointer to the InventoryController object
            InventoryControllerAddr = ObservedPlayerController + Offsets.ObservedPlayerController.InventoryController;
            HandsControllerAddr = ObservedPlayerController + Offsets.ObservedPlayerController.HandsController;
            CorpseAddr = ObservedHealthController + Offsets.ObservedHealthController.PlayerCorpse;

            // initialize gearmanager with the actual inventorycontroller object address
            try {
                ulong actualInventoryControllerObjectPtr = Memory.ReadPtr(InventoryControllerAddr);
                Gear = new GearManager(actualInventoryControllerObjectPtr);
            } catch (Exception ex) {
                // if gearmanager fails to initialize, log it and continue. gear will be null.
                LoneLogging.WriteLine($"failed to initialize gearmanager for player @ 0x{playerBase:x}: {ex.Message}");
                Gear = null; // Ensure Gear is null if initialization fails
            }

            AccountID = GetAccountID();
            IsFocused = CheckIfFocused();
            TeamID = GetTeamIDInternal(); // Renamed to avoid conflict if base Player has GetTeamID
            MovementContext = GetMovementContext();
            RotationAddress = ValidateRotationAddr(MovementContext + Offsets.ObservedMovementController.Rotation);

            this.Skeleton = new Skeleton(this, GetTransformInternalChain);

            bool isAI = Memory.ReadValue<bool>(this + Offsets.ObservedPlayerView.IsAI);
            IsHuman = !isAI;
            if (isAI) {
                Name = "AI";
                Type = PlayerType.AI;
            } else // Human Player
              {
                if (LocalGameWorld.MatchHasTeams)
                    ArgumentOutOfRangeException.ThrowIfEqual(TeamID, -1, nameof(TeamID));
                Name = GetName();
                Type = TeamID != -1 && Memory.LocalPlayer != null && TeamID == Memory.LocalPlayer.TeamID ?
                    PlayerType.Teammate : PlayerType.Player;
            }
        }

        private string GetAccountID() {
            var idPTR = Memory.ReadPtr(this + Offsets.ObservedPlayerView.AccountId);
            return Memory.ReadUnityString(idPTR);
        }

        /// <summary>
        /// gets player's team id.
        /// internal version to avoid potential naming conflicts with base class methods if any.
        /// </summary>
        private int GetTeamIDInternal() {
            try {
                // uses the already resolved InventoryControllerAddr (field holding the pointer)
                var inventoryControllerObjectPtr = Memory.ReadPtr(InventoryControllerAddr);
                if (inventoryControllerObjectPtr == 0) return -1;
                return GetTeamID(inventoryControllerObjectPtr); // Static method from Player.cs, expects actual controller obj addr
            } catch { return -1; }
        }

        private string GetName() {
            var namePtr = Memory.ReadPtr(this + Offsets.ObservedPlayerView.NickName);
            var name = Memory.ReadUnityString(namePtr)?.Trim();
            if (string.IsNullOrEmpty(name))
                name = "default";
            return name;
        }

        private ulong GetMovementContext() {
            return Memory.ReadPtrChain(ObservedPlayerController, Offsets.ObservedPlayerController.MovementController);
        }

        public override void OnRegRefresh(ScatterReadIndex index, IReadOnlySet<ulong> registered, bool? isActiveParam = null) {
            if (isActiveParam is not bool isActive)
                isActive = registered.Contains(this);

            if (isActive) {
                UpdateHealthStatus();
                // Gear refresh will be handled by LocalGameWorld's periodic update
            }
            base.OnRegRefresh(index, registered, isActive);
        }

        public void UpdateHealthStatus() {
            try {
                var tag = (Enums.ETagStatus)Memory.ReadValue<int>(ObservedHealthController + Offsets.ObservedHealthController.HealthStatus);
                if ((tag & Enums.ETagStatus.Dying) == Enums.ETagStatus.Dying)
                    HealthStatus = Enums.ETagStatus.Dying;
                else if ((tag & Enums.ETagStatus.BadlyInjured) == Enums.ETagStatus.BadlyInjured)
                    HealthStatus = Enums.ETagStatus.BadlyInjured;
                else if ((tag & Enums.ETagStatus.Injured) == Enums.ETagStatus.Injured)
                    HealthStatus = Enums.ETagStatus.Injured;
                else
                    HealthStatus = Enums.ETagStatus.Healthy;
            } catch (Exception ex) {
                LoneLogging.WriteLine($"ERROR updating Health Status for '{Name}': {ex}");
            }
        }

        public override uint[] GetTransformInternalChain(Bones bone) =>
            Offsets.ObservedPlayerView.GetTransformChain(bone);

        /// <summary>
        /// refreshes player's gear/equipment.
        /// </summary>
        public void RefreshGear() {
            try {
                // gear is initialized in constructor. if it was null due to an error, this won't run.
                Gear?.Refresh();
            } catch (Exception ex) {
                LoneLogging.WriteLine($"[GearManager] ERROR for Player {Name}: {ex}");
            }
        }

        public void RefreshHands() {
            try {
                if (IsActive && IsAlive) {
                    Hands ??= new HandsManager(this);
                    Hands?.Refresh();
                }
            } catch { }
        }
    }
}