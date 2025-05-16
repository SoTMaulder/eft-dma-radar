using arena_dma_radar.UI.ESP;
using arena_dma_radar.UI.Radar; // For MainForm
using arena_dma_radar.UI.Misc; // For Config, SKPaints, CustomFonts
using arena_dma_radar.Arena.ArenaPlayer.Plugins; // For HighAlert, GearManager (though direct use here is being removed for observed players)
using arena_dma_radar.Arena.GameWorld;
using eft_dma_shared.Common.Features; // For IMemWriteFeature
using eft_dma_shared.Common.Misc; // For LoneLogging, Utils, etc.
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Unity;
using eft_dma_shared.Common.Unity.Collections;
using eft_dma_shared.Common.Unity.LowLevel;
using eft_dma_shared.Common.Players;
using eft_dma_shared.Common.Maps; // For IMapEntity, LoneMapParams
using arena_dma_radar.Arena.Features.MemoryWrites; // For Aimbot
using eft_dma_shared.Common.ESP;
using eft_dma_shared.Common.Misc.Commercial;
using eft_dma_shared.Common.Misc.Pools; // For SharedArray
using eft_dma_shared.Common.DMA; // For Memory
using System.Collections.Frozen; // For ToFrozenSet
using System; // For NotImplementedException, ArgumentOutOfRangeException, etc.
using System.Collections.Concurrent; // For ConcurrentDictionary
using System.Diagnostics; // For Stopwatch
using System.Runtime.CompilerServices; // For MethodImplOptions
using System.Collections.Generic; // For List, IReadOnlySet
using System.Linq; // For LINQ extensions like Where, Any, Max
using SkiaSharp; // For SKCanvas, SKPoint, SKRect, SKPaints (though SKPaints is in UI.Misc)

namespace arena_dma_radar.Arena.ArenaPlayer {

    public abstract class Player : IWorldEntity, IMapEntity, IMouseoverEntity, IESPEntity, IPlayer {

        #region Static Interfaces

        public static implicit operator ulong(Player x) => x?.Base ?? 0x0;

        private static readonly ConcurrentDictionary<ulong, Stopwatch> _rateLimit = new();

        public static void Reset() {
            _rateLimit.Clear();
            lock (_focusedPlayers) {
                _focusedPlayers.Clear();
            }
        }

        #endregion Static Interfaces

        #region Allocation

        public static void Allocate(ConcurrentDictionary<ulong, Player> playerDict, ulong playerBase, Vector3? initialPosition = null) {
            var sw = _rateLimit.AddOrUpdate(playerBase,
                (key) => Stopwatch.StartNew(), // Start stopwatch on new entry
                (key, oldValue) => { oldValue.Restart(); return oldValue; }); // Restart for existing
            if (sw.Elapsed.TotalMilliseconds < 500f && playerDict.ContainsKey(playerBase)) // Check if already processed recently
                return;
            try {
                var player = AllocateInternal(playerBase, initialPosition);
                playerDict[player] = player;
                LoneLogging.WriteLine($"Player '{player.Name}' allocated.");
            } catch (Exception ex) {
                LoneLogging.WriteLine($"ERROR during Player Allocation for player @ 0x{playerBase.ToString("X")}: {ex}");
            }
            // No finally sw.Restart() here, as it's handled in AddOrUpdate or if an exception occurs before allocation.
        }

        private static Player AllocateInternal(ulong playerBase, Vector3? initialPosition) {
            var className = ObjectClass.ReadName(playerBase, 64);
            if (className != "ArenaObservedPlayerView") // this check might need to be adjusted if other player types are expected
                throw new ArgumentOutOfRangeException(nameof(className), $"expected arenaobservedplayerview but got {className}");
            var newPlayer = new ArenaObservedPlayer(playerBase);
            if (initialPosition.HasValue) {
                newPlayer.Position = initialPosition.Value; // Set initial position if provided (e.g. for reallocations)
            }
            return newPlayer;
        }

        protected Player(ulong playerBase) {
            ArgumentOutOfRangeException.ThrowIfZero(playerBase, nameof(playerBase));
            Base = playerBase;
        }

        #endregion Allocation

        #region Fields / Properties

        public ulong Base { get; }
        private Config Config { get; set; } = Program.Config;
        public bool IsActive { get; private set; }
        public PlayerType Type { get; protected set; }
        public string TwitchChannelURL { get; private set; } // this might need to be populated if used
        public Vector2 Rotation { get; private set; }

        public float MapRotation {
            get {
                float mapRotation = Rotation.X;
                mapRotation -= 90f;
                while (mapRotation < 0f)
                    mapRotation += 360f;
                return mapRotation;
            }
        }

        public ulong? Corpse { get; private set; }
        public Stopwatch HighAlertSw { get; } = new();
        public virtual Skeleton Skeleton => throw new NotImplementedException(nameof(Skeleton));
        public Stopwatch ErrorTimer { get; } = new();
        public bool IsAimbotLocked { get; set; }
        public bool IsFocused { get; protected set; }

        #endregion Fields / Properties

        #region Virtual Properties

        public virtual string Name { get; }
        public virtual string AccountID { get; }
        public virtual int TeamID { get; } = -1;
        public virtual Enums.EPlayerSide PlayerSide { get; }
        public virtual bool IsHuman { get; }
        public virtual ulong MovementContext { get; }
        public virtual ulong Body { get; }
        public virtual ulong InventoryControllerAddr { get; }
        public virtual ulong HandsControllerAddr { get; }
        public virtual ulong CorpseAddr { get; }
        public virtual ulong RotationAddress { get; }

        #endregion Virtual Properties

        #region Boolean Getters

        public bool IsPmc => PlayerSide is Enums.EPlayerSide.Usec || PlayerSide is Enums.EPlayerSide.Bear;
        public bool IsAlive => Corpse is null;
        public bool IsFriendly => this is LocalPlayer || Type is PlayerType.Teammate;
        public bool IsHostile => !IsFriendly;
        public bool IsStreaming => TwitchChannelURL is not null;
        public bool IsHostileActive => IsHostile && IsActive && IsAlive;
        public bool IsHumanActive => IsHuman && IsActive && IsAlive;
        public bool IsHumanHostile => IsHuman && IsHostile;
        public bool IsHumanHostileActive => IsHumanHostile && IsActive && IsAlive;
        public bool HasExfild => !IsActive && IsAlive; // This logic might need review based on game state

        #endregion Boolean Getters

        #region Methods

        protected static ulong ValidateRotationAddr(ulong rotationAddr) {
            var rotation = Memory.ReadValue<Vector2>(rotationAddr, false);
            if (!rotation.IsNormalOrZero() ||
                Math.Abs(rotation.X) > 360f ||
                Math.Abs(rotation.Y) > 90f)
                throw new ArgumentOutOfRangeException(nameof(rotationAddr), $"invalid rotation value: {rotation}");
            return rotationAddr;
        }

        public virtual void OnRegRefresh(ScatterReadIndex index, IReadOnlySet<ulong> registered, bool? isActiveParam = null) {
            if (isActiveParam is not bool isActive)
                isActive = registered.Contains(this);
            if (isActive) {
                this.SetAlive();
            } else if (this.IsAlive) {
                index.AddEntry<ulong>(0, this.CorpseAddr);
                index.Callbacks += x1 => {
                    if (x1.TryGetResult<ulong>(0, out var corpsePtr) && corpsePtr != 0x0)
                        this.SetDead(corpsePtr);
                    else
                        this.SetInactive();
                };
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetInactive() {
            Corpse = null;
            IsActive = false;
        }

        public void SetDead(ulong corpse) {
            Corpse = corpse;
            IsActive = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetAlive() {
            Corpse = null;
            IsActive = true;
        }

        public virtual void OnRealtimeLoop(ScatterReadIndex index) {
            index.AddEntry<Vector2>(-1, this.RotationAddress);
            foreach (var tr in Skeleton.Bones) {
                index.AddEntry<SharedArray<UnityTransform.TrsX>>((int)(uint)tr.Key, tr.Value.VerticesAddr,
                    (3 * tr.Value.Index + 3) * 16);
            }
            index.Callbacks += x1 => {
                bool p1 = false;
                bool p2 = true;
                if (x1.TryGetResult<Vector2>(-1, out var rotation))
                    p1 = this.SetRotation(ref rotation);
                foreach (var tr in Skeleton.Bones) {
                    if (x1.TryGetResult<SharedArray<UnityTransform.TrsX>>((int)(uint)tr.Key, out var vertices)) {
                        try {
                            try {
                                _ = tr.Value.UpdatePosition(vertices);
                            } catch (Exception ex) {
                                LoneLogging.WriteLine($"ERROR getting Player '{this.Name}' {tr.Key} Position: {ex}");
                                this.Skeleton.ResetTransform(tr.Key);
                            }
                        } catch {
                            p2 = false;
                        }
                    } else {
                        p2 = false;
                    }
                }

                if (p1 && p2)
                    this.ErrorTimer.Reset();
                else
                    this.ErrorTimer.Start();
            };
        }

        public void OnValidateTransforms(ScatterReadIndex round1, ScatterReadIndex round2) {
            foreach (var tr in Skeleton.Bones) {
                round1.AddEntry<MemPointer>((int)(uint)tr.Key,
                    tr.Value.TransformInternal +
                    UnityOffsets.TransformInternal.TransformAccess);
                round1.Callbacks += x1 => {
                    if (x1.TryGetResult<MemPointer>((int)(uint)tr.Key, out var tra))
                        round2.AddEntry<MemPointer>((int)(uint)tr.Key, tra + UnityOffsets.TransformAccess.Vertices);
                    round2.Callbacks += x2 => {
                        if (x2.TryGetResult<MemPointer>((int)(uint)tr.Key, out var verticesPtr)) {
                            if (tr.Value.VerticesAddr != verticesPtr) {
                                LoneLogging.WriteLine(
                                    $"WARNING - '{tr.Key}' Transform has changed for Player '{this.Name}'");
                                this.Skeleton.ResetTransform(tr.Key);
                            }
                        }
                    };
                };
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool SetRotation(ref Vector2 rotation) {
            try {
                rotation.ThrowIfAbnormalAndNotZero();
                rotation.X = rotation.X.NormalizeAngle();
                ArgumentOutOfRangeException.ThrowIfLessThan(rotation.X, 0f);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(rotation.X, 360f);
                ArgumentOutOfRangeException.ThrowIfLessThan(rotation.Y, -90f);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(rotation.Y, 90f);
                Rotation = rotation;
                return true;
            } catch {
                return false;
            }
        }

        protected static int GetTeamID(ulong inventoryControllerObjectAddr) {
            // this method expects the actual inventorycontroller object address, not a pointer to it.
            if (inventoryControllerObjectAddr == 0) return -1;

            var inventory = Memory.ReadPtr(inventoryControllerObjectAddr + Offsets.InventoryController.Inventory);
            if (inventory == 0) return -1;
            var equipment = Memory.ReadPtr(inventory + Offsets.Inventory.Equipment);
            if (equipment == 0) return -1;
            var slots = Memory.ReadPtr(equipment + Offsets.Equipment.Slots);
            if (slots == 0) return -1;

            using var slotsArray = MemArray<ulong>.Get(slots);

            foreach (var slotPtr in slotsArray) {
                if (slotPtr == 0) continue;
                var slotNamePtr = Memory.ReadPtr(slotPtr + Offsets.Slot.ID);
                if (slotNamePtr == 0) continue;
                string name = Memory.ReadUnityString(slotNamePtr);
                if (name == "ArmBand") {
                    var containedItem = Memory.ReadPtr(slotPtr + Offsets.Slot.ContainedItem);
                    if (containedItem == 0) continue;
                    var itemTemplate = Memory.ReadPtr(containedItem + Offsets.LootItem.Template);
                    if (itemTemplate == 0) continue;
                    var idPtr = Memory.ReadValue<Types.MongoID>(itemTemplate + Offsets.ItemTemplate._id);
                    string id = Memory.ReadUnityString(idPtr.StringID);

                    if (id == "63615c104bc92641374a97c8") return (int)Enums.ArmbandColorType.red;
                    else if (id == "63615bf35cb3825ded0db945") return (int)Enums.ArmbandColorType.fuchsia;
                    else if (id == "63615c36e3114462cd79f7c1") return (int)Enums.ArmbandColorType.yellow;
                    else if (id == "63615bfc5cb3825ded0db947") return (int)Enums.ArmbandColorType.green;
                    else if (id == "63615bc6ff557272023d56ac") return (int)Enums.ArmbandColorType.azure;
                    else if (id == "63615c225cb3825ded0db949") return (int)Enums.ArmbandColorType.white;
                    else if (id == "63615be82e60050cb330ef2f") return (int)Enums.ArmbandColorType.blue;
                    else return -1;
                }
            }
            return -1;
        }

        public virtual uint[] GetTransformInternalChain(Bones bone) =>
            throw new NotImplementedException();

        #endregion Methods

        #region Chams Feature

        public ChamsManager.ChamsMode ChamsMode { get; private set; }

        public void SetChams(ScatterWriteHandle writes, LocalGameWorld game, ChamsManager.ChamsMode chamsMode, int chamsMaterial) {
            try {
                if (ChamsMode != chamsMode) {
                    writes.Clear();
                    ApplyClothingChams(writes, chamsMaterial);
                    if (chamsMode is not ChamsManager.ChamsMode.Basic) {
                        ApplyGearChams(writes, chamsMaterial);
                    }
                    writes.Execute(DoWrite);
                    LoneLogging.WriteLine($"Chams set OK for Player '{Name}'");
                    ChamsMode = chamsMode;
                }
            } catch (Exception ex) {
                LoneLogging.WriteLine($"ERROR setting Chams for Player '{Name}': {ex}");
            }
            bool DoWrite() {
                if (Memory.ReadValue<ulong>(this.CorpseAddr, false) != 0) // Check if CorpseAddr itself is valid first
                    return false;
                if (!game.IsSafeToWriteMem)
                    return false;
                return true;
            }
        }

        private void ApplyClothingChams(ScatterWriteHandle writes, int chamsMaterial) {
            var pRendererContainersArray = Memory.ReadPtr(this.Body + Offsets.PlayerBody._bodyRenderers, false);
            using var rendererContainersArray = MemArray<Types.BodyRendererContainer>.Get(pRendererContainersArray, false);
            ArgumentOutOfRangeException.ThrowIfZero(rendererContainersArray.Count);

            foreach (var rendererContainer in rendererContainersArray) {
                using var renderersArray = MemArray<ulong>.Get(rendererContainer.Renderers, false);
                ArgumentOutOfRangeException.ThrowIfZero(renderersArray.Count);

                foreach (var skinnedMeshRenderer in renderersArray) {
                    var renderer = Memory.ReadPtr(skinnedMeshRenderer + UnityOffsets.SkinnedMeshRenderer.Renderer, false);
                    WriteChamsMaterial(writes, renderer, chamsMaterial);
                }
            }
        }

        private void ApplyGearChams(ScatterWriteHandle writes, int chamsMaterial) {
            var slotViews = Memory.ReadValue<ulong>(this.Body + Offsets.PlayerBody.SlotViews, false);
            if (!Utils.IsValidVirtualAddress(slotViews))
                return;

            var pSlotViewsDict = Memory.ReadValue<ulong>(slotViews + Offsets.SlotViewsContainer.Dict, false);
            if (!Utils.IsValidVirtualAddress(pSlotViewsDict))
                return;

            using var slotViewsDict = MemDictionary<ulong, ulong>.Get(pSlotViewsDict, false);
            if (slotViewsDict.Count == 0)
                return;

            foreach (var slot in slotViewsDict) {
                if (!Utils.IsValidVirtualAddress(slot.Value))
                    continue;

                var pDressesArray = Memory.ReadValue<ulong>(slot.Value + Offsets.PlayerBodySubclass.Dresses, false);
                if (!Utils.IsValidVirtualAddress(pDressesArray))
                    continue;

                using var dressesArray = MemArray<ulong>.Get(pDressesArray, false);
                if (dressesArray.Count == 0)
                    continue;

                foreach (var dress in dressesArray) {
                    if (!Utils.IsValidVirtualAddress(dress))
                        continue;

                    var pRenderersArray = Memory.ReadValue<ulong>(dress + Offsets.Dress.Renderers, false);
                    if (!Utils.IsValidVirtualAddress(pRenderersArray))
                        continue;

                    using var renderersArray = MemArray<ulong>.Get(pRenderersArray, false);
                    if (renderersArray.Count == 0)
                        continue;

                    foreach (var renderer in renderersArray) {
                        if (!Utils.IsValidVirtualAddress(renderer))
                            continue;

                        ulong rendererNative = Memory.ReadValue<ulong>(renderer + 0x10, false); // Assuming 0x10 is the offset to the native renderer object
                        if (!Utils.IsValidVirtualAddress(rendererNative))
                            continue;

                        WriteChamsMaterial(writes, rendererNative, chamsMaterial);
                    }
                }
            }
        }

        private static void WriteChamsMaterial(ScatterWriteHandle writes, ulong renderer, int chamsMaterial) {
            int materialsCount = Memory.ReadValue<int>(renderer + UnityOffsets.Renderer.Count, false);
            ArgumentOutOfRangeException.ThrowIfLessThan(materialsCount, 0, nameof(materialsCount));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(materialsCount, 30, nameof(materialsCount)); // Safety check
            if (materialsCount == 0)
                return;
            var materialsArrayPtr = Memory.ReadPtr(renderer + UnityOffsets.Renderer.Materials, false);
            var materials = Enumerable.Repeat<int>(chamsMaterial, materialsCount).ToArray();
            writes.AddBufferEntry(materialsArrayPtr, materials.AsSpan());
        }

        #endregion Chams Feature

        #region Interfaces

        public Vector2 MouseoverPosition { get; set; }
        public ref Vector3 Position => ref this.Skeleton.Root.Position;

        public void Draw(SKCanvas canvas, LoneMapParams mapParams, ILocalPlayer localPlayer) {
            try {
                var point = this.Position.ToMapPos(mapParams.Map).ToZoomedPos(mapParams);
                var showBomb = Config.ShowBomb; // Use the Radar-specific ShowBomb setting
                this.MouseoverPosition = new(point.X, point.Y);
                if (!this.IsAlive)
                    DrawDeathMarker(canvas, point);
                else {
                    DrawPlayerMarker(canvas, localPlayer, point);
                    if (this == localPlayer)
                        return;
                    var height = this.Position.Y - localPlayer.Position.Y;
                    var dist = Vector3.Distance(localPlayer.Position, this.Position);
                    var lines = new List<string>();
                    if (!MainForm.Config.HideNames) {
                        string name = null;
                        if (this.ErrorTimer.ElapsedMilliseconds > 100)
                            name = "ERROR";
                        else
                            name = this.Name;
                        string health = null;
                        if (this is ArenaObservedPlayer observed)
                            health = observed.HealthStatus is Enums.ETagStatus.Healthy ?
                            null : $" ({observed.HealthStatus.GetDescription()})";
                        lines.Add($"{name}{health}");
                        lines.Add($"H: {(int)Math.Round(height)} D: {(int)Math.Round(dist)}");
                    } else {
                        lines.Add($"{(int)Math.Round(height)},{(int)Math.Round(dist)}");
                        if (this.ErrorTimer.ElapsedMilliseconds > 100)
                            lines[0] = "ERROR";
                    }
                    // updated bomb carrier logic for radar
                    if (showBomb && this is ArenaObservedPlayer observedPlayerWithBomb) {
                        if (Memory.Game?.matchMode == Enums.ERaidMode.BlastGang) { // Added null check for Memory.Game
                            if (observedPlayerWithBomb.HasBomb) // use the new property
                                lines.Add("(BOMB)");
                        }
                    }
                    DrawPlayerText(canvas, point, lines);
                }
            } catch (Exception ex) {
                LoneLogging.WriteLine($"WARNING! Player Draw Error: {ex}");
            }
        }

        private void DrawPlayerMarker(SKCanvas canvas, IPlayer localPlayer, SKPoint point) {
            var radians = this.MapRotation.ToRadians();
            var paints = GetPaints();
            if (this != localPlayer && MainForm.MouseoverGroup is int grp && grp == this.TeamID)
                paints.Item1 = SKPaints.PaintMouseoverGroup;
            SKPaints.ShapeOutline.StrokeWidth = paints.Item1.StrokeWidth + (2f * MainForm.UIScale);

            var size = 6 * MainForm.UIScale;
            canvas.DrawCircle(point, size, SKPaints.ShapeOutline);
            canvas.DrawCircle(point, size, paints.Item1);

            int aimlineLength = this == localPlayer ?
                MainForm.Config.AimLineLength : 15;
            if (!this.IsFriendly && this.IsFacingTarget(localPlayer))
                aimlineLength = 9999;

            var aimlineEnd = GetAimlineEndpoint(point, radians, aimlineLength);
            canvas.DrawLine(point, aimlineEnd, SKPaints.ShapeOutline);
            canvas.DrawLine(point, aimlineEnd, paints.Item1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawDeathMarker(SKCanvas canvas, SKPoint point) {
            float length = 6 * MainForm.UIScale;
            canvas.DrawLine(new SKPoint(point.X - length, point.Y + length), new SKPoint(point.X + length, point.Y - length), SKPaints.PaintDeathMarker);
            canvas.DrawLine(new SKPoint(point.X - length, point.Y - length), new SKPoint(point.X + length, point.Y + length), SKPaints.PaintDeathMarker);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPoint GetAimlineEndpoint(SKPoint start, float radians, float aimlineLength) {
            aimlineLength *= MainForm.UIScale;
            return new SKPoint((float)(start.X + MathF.Cos(radians) * aimlineLength), (float)(start.Y + Math.Sin(radians) * aimlineLength));
        }

        private void DrawPlayerText(SKCanvas canvas, SKPoint point, List<string> lines) {
            var paints = GetPaints();
            if (MainForm.MouseoverGroup is int grp && grp == this.TeamID)
                paints.Item2 = SKPaints.TextMouseoverGroup;
            float spacing = 3 * MainForm.UIScale;
            point.Offset(9 * MainForm.UIScale, spacing);
            foreach (var line in lines) {
                if (string.IsNullOrEmpty(line?.Trim()))
                    continue;

                canvas.DrawText(line, point, SKPaints.TextOutline);
                canvas.DrawText(line, point, paints.Item2);
                point.Offset(0, 12 * MainForm.UIScale);
            }
        }

        private ValueTuple<SKPaint, SKPaint> GetPaints() {
            if (this.IsAimbotLocked)
                return new(SKPaints.PaintAimbotLocked, SKPaints.TextAimbotLocked);
            else if (this is LocalPlayer)
                return new(SKPaints.PaintLocalPlayer, null); // Text paint is null for local player as it's not typically drawn
            else if (this.IsFocused)
                return new(SKPaints.PaintFocused, SKPaints.TextFocused);

            if (this.Type == PlayerType.Teammate && !string.IsNullOrEmpty(this.AccountID) && Program.Config.CustomTeammateColors.TryGetValue(this.AccountID, out string colorHex)) {
                if (SKColor.TryParse(colorHex, out SKColor customColor)) {
                    var customPaint = new SKPaint {
                        Color = customColor,
                        StrokeWidth = SKPaints.PaintTeammate.StrokeWidth,
                        Style = SKPaintStyle.Stroke,
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High
                    };
                    var customTextPaint = new SKPaint {
                        SubpixelText = true,
                        Color = customColor,
                        IsStroke = false,
                        TextSize = SKPaints.TextTeammate.TextSize,
                        TextEncoding = SKTextEncoding.Utf8,
                        IsAntialias = true,
                        Typeface = CustomFonts.SKFontFamilyRegular,
                        FilterQuality = SKFilterQuality.High
                    };
                    return new(customPaint, customTextPaint);
                }
            }

            switch (this.Type) {
                case PlayerType.Teammate:
                    return new(SKPaints.PaintTeammate, SKPaints.TextTeammate);

                case PlayerType.Player:
                    return new(SKPaints.PaintPlayer, SKPaints.TextPlayer);

                case PlayerType.AI:
                    return new(SKPaints.PaintAI, SKPaints.TextAI);

                case PlayerType.Streamer:
                    return new(SKPaints.PaintStreamer, SKPaints.TextStreamer);

                default:
                    return new(SKPaints.PaintPlayer, SKPaints.TextPlayer);
            }
        }

        public void DrawMouseover(SKCanvas canvas, LoneMapParams mapParams, LocalPlayer localPlayer) {
            List<string> lines = new();
            string name = MainForm.Config.HideNames && this.IsHuman ?
                "<Hidden>" : this.Name;
            if (this.IsStreaming)
                lines.Add("[LIVE TTV - Double Click]");
            if (this is ArenaObservedPlayer observed) {
                string health = observed.HealthStatus is Enums.ETagStatus.Healthy ?
                    null : $" ({observed.HealthStatus.GetDescription()})";
                lines.Add($"{name}{health}");
                if (observed.TeamID != -1) {
                    lines.Add($" T:{observed.TeamID} ");
                }
                var equipment = observed.Gear?.Equipment; // Use the Gear property
                var hands = observed.Hands?.InHands;
                lines.Add($"Use:{(hands is null ? "--" : hands)}");
                if (equipment is not null) {
                    foreach (var item in equipment)
                        lines.Add($"{item.Key}: {item.Value}");
                }
            } else
                return; // Only draw mouseover for ArenaObservedPlayer
            this.Position.ToMapPos(mapParams.Map).ToZoomedPos(mapParams).DrawMouseoverText(canvas, lines);
        }

        public void DrawESP(SKCanvas canvas, LocalPlayer localPlayer) {
            if (this == localPlayer ||
                    !this.IsActive || !this.IsAlive)
                return;
            bool showInfo = ESP.Config.PlayerRendering.ShowLabels;
            bool showDist = ESP.Config.PlayerRendering.ShowDist;
            bool showWep = ESP.Config.PlayerRendering.ShowWeapons;
            bool showBomb = ESP.Config.PlayerRendering.ShowBomb; // Use the ESP-specific ShowBomb
            bool drawLabel = showInfo || showDist || showWep || showBomb; // Added showBomb to this condition
            var dist = Vector3.Distance(localPlayer.Position, this.Position);
            if (dist > LocalGameWorld.MAX_DIST)
                return;

            if (this.IsHostile && (ESP.Config.HighAlert && this.IsHuman)) {
                if (this.IsFacingTarget(localPlayer)) {
                    if (!this.HighAlertSw.IsRunning)
                        this.HighAlertSw.Start();
                    else if (this.HighAlertSw.Elapsed.TotalMilliseconds >= 500f)
                        HighAlert.DrawHighAlertESP(canvas, this);
                } else
                    this.HighAlertSw.Reset();
            }
            if (!CameraManagerBase.WorldToScreen(ref Position, out var baseScrPos))
                return;
            var paint = this.GetEspPlayerPaint();
            if (ESP.Config.PlayerRendering.RenderingMode is ESPPlayerRenderMode.Bones) {
                if (!this.Skeleton.UpdateESPBuffer())
                    return;
                canvas.DrawPoints(SKPointMode.Lines, Skeleton.ESPBuffer, paint.Item1);
            }
            if (drawLabel && this is ArenaObservedPlayer observed) {
                var lines = new List<string>();
                if (showInfo) {
                    string health = observed.HealthStatus is Enums.ETagStatus.Healthy ?
                    null : $" ({observed.HealthStatus.GetDescription()})";
                    lines.Add($"{observed.Name}{health}");
                }
                if (showWep)
                    lines.Add($"({observed.Hands?.InHands})");
                if (showDist) {
                    if (lines.Count == 0)
                        lines.Add($"{(int)dist}m");
                    else
                        lines[0] += $" ({(int)dist}m)";
                }
                // updated bomb carrier logic for esp
                if (showBomb && Memory.Game?.matchMode == Enums.ERaidMode.BlastGang) { // Added null check for Memory.Game
                    if (observed.HasBomb) // use the new property
                    {
                        lines.Add("(BOMB)");
                    }
                }
                var textPt = new SKPoint(baseScrPos.X,
                    baseScrPos.Y + (paint.Item2.TextSize * ESP.Config.FontScale));
                textPt.DrawESPText(canvas, observed, localPlayer, false, paint.Item2, lines.ToArray()); // printDist is false because we handle it above
            }
            if (ESP.Config.ShowAimLock && this.IsAimbotLocked) {
                var info = MemWriteFeature<Aimbot>.Instance.Cache;
                if (info is not null &&
                    info.LastFireportPos is Vector3 fpPos &&
                    info.LastPlayerPos is Vector3 playerPos) {
                    if (!CameraManagerBase.WorldToScreen(ref fpPos, out var fpScreen))
                        return;
                    if (!CameraManagerBase.WorldToScreen(ref playerPos, out var playerScreen))
                        return;
                    canvas.DrawLine(fpScreen, playerScreen, SKPaints.PaintBasicESP);
                }
            }
        }

        public ValueTuple<SKPaint, SKPaint> GetEspPlayerPaint() {
            if (this.IsAimbotLocked)
                return new(SKPaints.PaintAimbotLockedESP, SKPaints.TextAimbotLockedESP);
            else if (this.IsFocused)
                return new(SKPaints.PaintFocusedESP, SKPaints.TextFocusedESP);

            if (this.Type == PlayerType.Teammate && !string.IsNullOrEmpty(this.AccountID) && Program.Config.CustomTeammateColors.TryGetValue(this.AccountID, out string colorHex)) {
                if (SKColor.TryParse(colorHex, out SKColor customColor)) {
                    var customPaint = new SKPaint {
                        Color = customColor,
                        StrokeWidth = SKPaints.PaintTeammateESP.StrokeWidth,
                        Style = SKPaintStyle.StrokeAndFill,
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High
                    };
                    var customTextPaint = new SKPaint {
                        SubpixelText = true,
                        Color = customColor,
                        IsStroke = false,
                        TextSize = SKPaints.TextTeammateESP.TextSize,
                        TextAlign = SKTextAlign.Center,
                        TextEncoding = SKTextEncoding.Utf8,
                        IsAntialias = true,
                        Typeface = CustomFonts.SKFontFamilyMedium,
                        FilterQuality = SKFilterQuality.High
                    };
                    return new(customPaint, customTextPaint);
                }
            }

            switch (this.Type) {
                case Player.PlayerType.Teammate:
                    return new(SKPaints.PaintTeammateESP, SKPaints.TextTeammateESP);

                case Player.PlayerType.Player:
                    return new(SKPaints.PaintPlayerESP, SKPaints.TextPlayerESP);

                case Player.PlayerType.AI:
                    return new(SKPaints.PaintAIESP, SKPaints.TextAIESP);

                case Player.PlayerType.Streamer:
                    return new(SKPaints.PaintStreamerESP, SKPaints.TextStreamerESP);

                default:
                    return new(SKPaints.PaintPlayerESP, SKPaints.TextPlayerESP);
            }
        }

        #endregion Interfaces

        #region Focused Players

        private static readonly HashSet<string> _focusedPlayers = new(StringComparer.OrdinalIgnoreCase);

        public void ToggleFocus() {
            if (this is not ArenaObservedPlayer ||
                !this.IsHumanActive)
                return;
            string id = this.AccountID?.Trim();
            if (string.IsNullOrEmpty(id))
                return;
            lock (_focusedPlayers) {
                bool isFocused = _focusedPlayers.Contains(id);
                if (isFocused)
                    _focusedPlayers.Remove(id);
                else
                    _focusedPlayers.Add(id);
                IsFocused = !isFocused;
            }
        }

        protected bool CheckIfFocused() {
            string id = this.AccountID?.Trim();
            if (string.IsNullOrEmpty(id))
                return false;
            lock (_focusedPlayers) {
                return _focusedPlayers.Contains(id);
            }
        }

        #endregion Focused Players

        #region Types

        public enum PlayerType {

            [Description("Default")]
            Default,

            [Description("Teammate")]
            Teammate,

            [Description("AI")]
            AI,

            [Description("Player")]
            Player,

            [Description("Streamer")]
            Streamer
        }

        #endregion Types
    }
}