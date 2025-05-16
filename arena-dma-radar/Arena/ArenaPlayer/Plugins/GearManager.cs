using eft_dma_shared.Common.Misc.Data; // For Types.MongoID, EftDataManager
using eft_dma_shared.Common.Unity.Collections; // For MemArray
using System.Collections.Frozen;
using SDK; // For Offsets

namespace arena_dma_radar.Arena.ArenaPlayer.Plugins {

    public sealed class GearManager {

        private static readonly FrozenSet<string> _skipSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SecuredContainer", "Dogtag", "Compass", "Eyewear", "ArmBand", "Scabbard"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private readonly ulong _inventoryControllerObjectAddr; // Stores the actual address of the InventoryController object

        public IReadOnlyDictionary<string, string> Equipment { get; private set; }
        public bool HasBomb { get; private set; }

        /// <summary>
        /// constructor now takes the direct address of the player's inventory controller object.
        /// </summary>
        /// <param name="inventoryControllerObjectAddress">the actual memory address of the inventorycontroller instance.</param>
        public GearManager(ulong inventoryControllerObjectAddress) {
            _inventoryControllerObjectAddr = inventoryControllerObjectAddress;
            // initialize with empty collections to prevent null issues before the first refresh.
            Equipment = new Dictionary<string, string>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            HasBomb = false;
        }

        /// <summary>
        /// refreshes the gear information from memory.
        /// </summary>
        public void Refresh() {
            if (_inventoryControllerObjectAddr == 0) // if the address is null, nothing to refresh.
            {
                Equipment = new Dictionary<string, string>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase); // Ensure it's empty
                HasBomb = false;
                return;
            }

            try {
                var inventory = Memory.ReadPtr(_inventoryControllerObjectAddr + Offsets.InventoryController.Inventory);
                if (inventory == 0) throw new Exception("inventory ptr is null.");
                var equipment = Memory.ReadPtr(inventory + Offsets.Inventory.Equipment);
                if (equipment == 0) throw new Exception("equipment ptr is null.");
                var slots = Memory.ReadPtr(equipment + Offsets.Equipment.Slots);
                if (slots == 0) throw new Exception("slots ptr is null.");

                using var slotsArray = MemArray<ulong>.Get(slots);
                var gearDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var slot in slotsArray) {
                    if (slot == 0) continue;
                    try {
                        var namePtr = Memory.ReadPtr(slot + Offsets.Slot.ID);
                        if (namePtr == 0) continue;
                        var name = Memory.ReadUnityString(namePtr);
                        if (string.IsNullOrEmpty(name) || _skipSlots.Contains(name))
                            continue;

                        var containedItem = Memory.ReadPtr(slot + Offsets.Slot.ContainedItem);
                        if (containedItem == 0) continue;

                        var itemTemplate = Memory.ReadPtr(containedItem + Offsets.LootItem.Template);
                        if (itemTemplate == 0) continue;

                        var idPtr = Memory.ReadValue<Types.MongoID>(itemTemplate + Offsets.ItemTemplate._id);
                        string id = Memory.ReadUnityString(idPtr.StringID);

                        if (EftDataManager.AllItems.TryGetValue(id, out var entry) && entry is not null)
                            gearDict.TryAdd(name, entry.Name);
                    } catch { /* Skip over empty/invalid slots or read errors for a specific slot */ }
                }
                Equipment = gearDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase); // Update with new gear
                HasBomb = Equipment.ContainsKey("Backpack");
            } catch (Exception ex) {
                // on error, clear equipment to avoid stale data and log the issue.
                // consider if specific exceptions should be handled differently or re-thrown.
                // LoneLogging.WriteLine($"error refreshing gearmanager: {ex.Message}"); // uncomment if logging is desired here
                Equipment = new Dictionary<string, string>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                HasBomb = false;
            }
        }
    }
}