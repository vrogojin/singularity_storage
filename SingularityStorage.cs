using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;
using Newtonsoft.Json;
using Rust;
using System.Collections;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("SingularityStorage", "YourServer", "5.0.1")]
    [Description("Advanced quantum storage system that transcends server wipes")]
    public class SingularityStorage : RustPlugin
    {
        [PluginReference] private Plugin ImageLibrary;
        #region Fields
        
        private Dictionary<ulong, PlayerStorageData> playerStorage = new Dictionary<ulong, PlayerStorageData>();
        private Dictionary<ulong, StorageTerminal> activeTerminals = new Dictionary<ulong, StorageTerminal>();
        private Dictionary<ulong, BaseEntity> playerActiveStorage = new Dictionary<ulong, BaseEntity>();
        private Dictionary<ulong, uint> terminalTextureIds = new Dictionary<ulong, uint>(); // Track correct texture IDs
        private Dictionary<ulong, string> playerStorageUI = new Dictionary<ulong, string>(); // Track UI for each player
        
        private const string STORAGE_PREFAB = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private const string TERMINAL_PREFAB = "assets/prefabs/deployable/vendingmachine/vendingmachine.deployed.prefab";
        private const string PICTURE_FRAME_PREFAB = "assets/prefabs/deployable/signs/sign.pictureframe.portrait.prefab";
        private const string PERMISSION_USE = "singularitystorage.use";
        private const string PERMISSION_ADMIN = "singularitystorage.admin";
        private const string PERMISSION_TIER2 = "singularitystorage.tier2";
        private const string PERMISSION_TIER3 = "singularitystorage.tier3";
        private const string PERMISSION_TIER4 = "singularitystorage.tier4";
        private const string PERMISSION_TIER5 = "singularitystorage.tier5";
        private const string TERMINAL_FACE_IMAGE = "singularity_terminal_face";
        
        private Configuration config;
        
        #endregion
        
        #region Configuration
        
        private class Configuration
        {
            
            [JsonProperty("Allow Blacklisted Items")]
            public bool AllowBlacklistedItems { get; set; } = false;
            
            [JsonProperty("Blacklisted Items (shortnames)")]
            public List<string> BlacklistedItems { get; set; } = new List<string>
            {
                "explosive.timed",
                "explosive.satchel",
                "ammo.rocket.basic",
                "ammo.rocket.hv",
                "ammo.rocket.fire"
            };
            
            [JsonProperty("Terminal Spawn Locations")]
            public Dictionary<string, List<TerminalLocation>> TerminalLocations { get; set; } = new Dictionary<string, List<TerminalLocation>>();
            
            [JsonProperty("Terminal Interaction Distance")]
            public float InteractionDistance { get; set; } = 3f;
            
            [JsonProperty("Auto-spawn Terminals")]
            public bool AutoSpawnTerminals { get; set; } = true;
            
            [JsonProperty("Terminal Display Name")]
            public string TerminalDisplayName { get; set; } = "Singularity Storage Terminal";
            
            [JsonProperty("Terminal Skin ID")]
            public ulong TerminalSkinId { get; set; } = 1920833688; // Green vending machine
            
            [JsonProperty("Terminal Face Texture URL")]
            public string TerminalFaceTextureUrl { get; set; } = "https://vrogojin.github.io/singularity_storage/singularity_terminal_sign.png";
            
            [JsonProperty("Use Custom Face Texture (Deprecated)")]
            public bool UseCustomFaceTexture { get; set; } = false; // No longer used - using green skin instead
            
            [JsonProperty("Only Allow Resources And Components")]
            public bool OnlyAllowResourcesAndComponents { get; set; } = true;
        }
        
        private class TerminalLocation
        {
            public Vector3 Position { get; set; }
            public float Rotation { get; set; }
        }
        
        #endregion
        
        #region Wipe Detection
        
        private void CheckForWipeAndUpdateCounters()
        {
            // Get current save name to detect wipes
            var currentSaveName = World.SaveFileName;
            
            // Use a wrapper class for storing the save name
            var saveData = Interface.Oxide.DataFileSystem.ReadObject<SaveNameData>("SingularityStorage_LastSave");
            var lastKnownSave = saveData?.SaveName ?? "";
            
            bool isNewWipe = false;
            bool isFirstRun = string.IsNullOrEmpty(lastKnownSave); // First time plugin has run
            
            // Check if it's a new save (wipe detected) - but not on first run
            if (!string.IsNullOrEmpty(currentSaveName) && currentSaveName != lastKnownSave)
            {
                if (!isFirstRun)
                {
                    isNewWipe = true;
                    Puts($"[WIPE DETECTED] New save detected: {currentSaveName} (previous: {lastKnownSave})");
                }
                else
                {
                    Puts($"[FIRST RUN] Initial save name stored: {currentSaveName}");
                }
                
                // Save the new save name
                Interface.Oxide.DataFileSystem.WriteObject("SingularityStorage_LastSave", new SaveNameData { SaveName = currentSaveName });
            }
            
            // Alternative wipe detection: Check if player count is very low and buildings are gone
            // This catches manual wipes that keep the same save name
            var timeSinceLastCheck = DateTime.UtcNow;
            
            if (isNewWipe)
            {
                // Update wipe counters for all stored player data
                foreach (var playerData in playerStorage.Values)
                {
                    // Only count as a wipe if it's been at least 12 hours since last wipe
                    // This prevents false positives from server restarts
                    if (playerData.LastWipeDate == DateTime.MinValue || 
                        (timeSinceLastCheck - playerData.LastWipeDate).TotalHours > 12)
                    {
                        playerData.WipesSurvived++;
                        playerData.CurrentTierWipes++;
                        playerData.LastWipeDate = timeSinceLastCheck;
                        
                        // Check if tier needs to be downgraded (after 2 wipes at current tier)
                        if (playerData.CurrentTierWipes >= 2 && playerData.StorageTier > 1)
                        {
                            // Downgrade tier
                            var oldTier = playerData.StorageTier;
                            playerData.StorageTier = 1;
                            playerData.CurrentTierWipes = 0;
                            
                            // Revoke higher tier permissions for this player
                            var playerId = playerData.PlayerId.ToString();
                            permission.RevokeUserPermission(playerId, PERMISSION_TIER2);
                            permission.RevokeUserPermission(playerId, PERMISSION_TIER3);
                            permission.RevokeUserPermission(playerId, PERMISSION_TIER4);
                            permission.RevokeUserPermission(playerId, PERMISSION_TIER5);
                            
                            Puts($"[TIER DOWNGRADE] Player {playerData.PlayerId} downgraded from Tier {oldTier} to Tier 1 (exceeded 2 wipes without upkeep)");
                        }
                        else
                        {
                            Puts($"[WIPE] Player {playerData.PlayerId} has survived {playerData.WipesSurvived} wipes total, {playerData.CurrentTierWipes} at current tier");
                        }
                    }
                }
                
                // Save the updated data
                SaveData();
                
                // Announce wipe survival in console
                Puts($"[SINGULARITY STORAGE] Wipe detected! {playerStorage.Count} players' storage survived the wipe.");
            }
        }
        
        #endregion
        
        #region Data
        
        private class PlayerStorageData
        {
            public ulong PlayerId { get; set; }
            public List<ItemData> Items { get; set; } = new List<ItemData>();
            public DateTime LastAccessed { get; set; }
            public int StorageTier { get; set; } = 1; // Default tier 1
            public int WipesSurvived { get; set; } = 0; // How many wipes this storage has survived
            public DateTime FirstCreated { get; set; } = DateTime.UtcNow; // When the storage was first created
            public DateTime LastWipeDate { get; set; } = DateTime.MinValue; // Last wipe date tracked
            public int CurrentTierWipes { get; set; } = 0; // Wipes survived at current tier
            public DateTime TierUpgradeDate { get; set; } = DateTime.UtcNow; // When tier was last upgraded
        }
        
        private class ItemData
        {
            public int ItemId { get; set; }
            public int Amount { get; set; }
            public ulong Skin { get; set; }
            public float Condition { get; set; }
            public int Position { get; set; }
            public string Text { get; set; }
            public string Name { get; set; }
            public int Ammo { get; set; }
            public string AmmoType { get; set; }
            public List<ItemData> Contents { get; set; } = new List<ItemData>();
        }
        
        private class StorageTerminal
        {
            public BaseEntity Entity { get; set; }
            public string MonumentName { get; set; }
            public Vector3 Position { get; set; }
            public MonumentInfo Monument { get; set; }
            public Vector3 RelativePosition { get; set; }
            public BaseEntity DisplayEntity { get; set; }
        }
        
        private class SaveNameData
        {
            public string SaveName { get; set; }
        }
        
        #endregion
        
        #region Oxide Hooks
        
        private void Init()
        {
            permission.RegisterPermission(PERMISSION_USE, this);
            permission.RegisterPermission(PERMISSION_ADMIN, this);
            permission.RegisterPermission(PERMISSION_TIER2, this);
            permission.RegisterPermission(PERMISSION_TIER3, this);
            permission.RegisterPermission(PERMISSION_TIER4, this);
            permission.RegisterPermission(PERMISSION_TIER5, this);
            
            LoadData();
        }
        
        private void OnServerInitialized()
        {
            LoadConfig();
            Puts($"[DEBUG] Config loaded - Terminal Skin ID: {config.TerminalSkinId}");
            
            // Check for wipe and update counters
            CheckForWipeAndUpdateCounters();
            
            // Clean up any leftover UI from previous sessions
            CleanupLeftoverUI();
            
            // Load custom texture if enabled
            if (config.UseCustomFaceTexture && ImageLibrary != null)
            {
                ImageLibrary.Call<bool>("AddImage", config.TerminalFaceTextureUrl, TERMINAL_FACE_IMAGE, 0UL);
                Puts($"[DEBUG] Loading custom terminal face texture from: {config.TerminalFaceTextureUrl}");
            }
            
            if (config.AutoSpawnTerminals)
            {
                timer.Once(3f, () => SpawnAllTerminals());
            }
            
            // Picture frame texture check no longer needed - using green vending machine skin
            // timer.Every(30f, () => CheckAndLoadNearbyTextures());
            
            // Schedule first periodic redraw
            SchedulePeriodicRedraw();
        }
        
        // Picture frame handling removed - using green vending machine skin
        /*
        private void OnSignUpdated(Signage sign, BasePlayer player)
        {
            // Removed - no longer using picture frames
        }
        */
        
        // Prevent players from rotating singularity terminals and displays
        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (player == null || info?.HitEntity == null) return null;
            
            // Check if it's a vending machine
            var vendingMachine = info.HitEntity as VendingMachine;
            if (vendingMachine != null && activeTerminals.ContainsKey(vendingMachine.net.ID.Value))
            {
                SendReply(player, "<color=#ff0000>You cannot modify Singularity Storage terminals!</color>");
                return true; // Return true to cancel the hammer action
            }
            
            // Check if it's a sign/picture frame
            var sign = info.HitEntity as Signage;
            if (sign != null)
            {
                foreach (var terminal in activeTerminals.Values)
                {
                    if (terminal?.DisplayEntity == sign)
                    {
                        SendReply(player, "<color=#ff0000>You cannot modify Singularity Storage displays!</color>");
                        return true; // Block the hammer hit
                    }
                }
            }
            
            return null;
        }
        
        // Specific hook for vending machine rotation
        private object OnRotateVendingMachine(VendingMachine vendingMachine, BasePlayer player)
        {
            if (vendingMachine == null || player == null) return null;
            
            // Check if this is one of our terminals
            if (activeTerminals.ContainsKey(vendingMachine.net.ID.Value))
            {
                SendReply(player, "<color=#ff0000>Singularity terminals cannot be rotated!</color>");
                return true; // Return non-null to block rotation
            }
            
            return null;
        }
        
        // Additional protection against rotation
        private object CanRotateEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return null;
            
            var vendingMachine = entity as VendingMachine;
            if (vendingMachine == null) return null;
            
            // Check if this is one of our terminals
            if (activeTerminals.ContainsKey(vendingMachine.net.ID.Value))
            {
                SendReply(player, "<color=#ff0000>Singularity terminals cannot be rotated!</color>");
                return false; // Block rotation
            }
            
            return null;
        }
        
        // Prevent pickup of singularity terminals
        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return null;
            
            var vendingMachine = entity as VendingMachine;
            if (vendingMachine == null) return null;
            
            // Check if this is one of our terminals
            if (activeTerminals.ContainsKey(vendingMachine.net.ID.Value))
            {
                SendReply(player, "<color=#ff0000>Singularity terminals cannot be picked up!</color>");
                return false; // Block pickup
            }
            
            return null;
        }
        
        private void Unload()
        {
            SaveData();
            
            Puts($"[DEBUG] Unloading plugin - cleaning up {activeTerminals.Count} terminals");
            
            // Clean up all player UIs before unloading
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyStorageTierUI(player);
            }
            playerStorageUI.Clear();
            
            // Clean up any active storage containers
            foreach (var kvp in playerActiveStorage.ToList())
            {
                if (kvp.Value != null && !kvp.Value.IsDestroyed)
                {
                    kvp.Value.Kill();
                }
            }
            playerActiveStorage.Clear();
            
            // Note: Individual timers will be automatically cleaned up when plugin unloads
            
            // Clean up terminals and their display entities
            int removedTerminals = 0;
            int removedDisplays = 0;
            
            foreach (var terminal in activeTerminals.Values.ToList())
            {
                // Clean up display entity first
                if (terminal?.DisplayEntity != null && !terminal.DisplayEntity.IsDestroyed)
                {
                    Puts($"[DEBUG] Removing display entity at {terminal.DisplayEntity.transform.position}");
                    terminal.DisplayEntity.Kill();
                    removedDisplays++;
                }
                else if (terminal?.DisplayEntity == null)
                {
                    Puts($"[DEBUG] Terminal has no DisplayEntity tracked");
                }
                
                // Then clean up the terminal itself
                if (terminal?.Entity != null && !terminal.Entity.IsDestroyed)
                {
                    terminal.Entity.Kill();
                    removedTerminals++;
                }
            }
            
            // Picture frame cleanup no longer needed - using green vending machine skin
            
            Puts($"[DEBUG] Removed {removedTerminals} terminals");
            activeTerminals.Clear();
            
            // Clean up any open storage containers
            foreach (var storage in playerActiveStorage.Values.ToList())
            {
                if (storage != null && !storage.IsDestroyed)
                {
                    storage.Kill();
                }
            }
            playerActiveStorage.Clear();
        }
        
        private void OnNewSave(string filename)
        {
            Puts("Server wipe detected - Singularity storage data preserved across time and space");
        }
        
        #endregion
        
        #region Core Methods
        
        private void SpawnAllTerminals()
        {
            // Only spawn terminals that have been explicitly saved
            if (config.TerminalLocations.Count == 0)
            {
                Puts("No saved terminal locations found. Use /singularityadmin spawn to create terminals.");
                return;
            }
            
            var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
            var spawnedCount = 0;
            var monumentCounts = new Dictionary<string, int>();
            
            // Group monuments by type to count instances
            foreach (var monument in monuments)
            {
                string monumentName = GetMonumentName(monument);
                if (!monumentCounts.ContainsKey(monumentName))
                    monumentCounts[monumentName] = 0;
                monumentCounts[monumentName]++;
            }
            
            // Spawn terminals at all instances of each monument type
            foreach (var monument in monuments)
            {
                string monumentName = GetMonumentName(monument);
                
                if (config.TerminalLocations.ContainsKey(monumentName))
                {
                    foreach (var location in config.TerminalLocations[monumentName])
                    {
                        // Skip default positions (0,0,0)
                        if (location.Position == Vector3.zero)
                        {
                            Puts($"Skipping default position for {monumentName}");
                            continue;
                        }
                        
                        // Convert relative position to world position
                        Vector3 worldPosition = monument.transform.TransformPoint(location.Position);
                        
                        // Convert relative rotation to world rotation
                        float worldRotation = location.Rotation + monument.transform.eulerAngles.y;
                        worldRotation = worldRotation % 360;
                        if (worldRotation < 0) worldRotation += 360;
                        
                        // For saved terminals, only snap to ground if they're too high or too low
                        // This preserves terminals placed inside buildings at specific heights
                        if (location.Position.y < -5f || location.Position.y > 50f)
                        {
                            worldPosition = SnapToGround(worldPosition);
                        }
                        
                        SpawnTerminal(worldPosition, worldRotation, monumentName, monument);
                        spawnedCount++;
                    }
                }
            }
            
            if (spawnedCount > 0)
            {
                Puts($"Spawned {spawnedCount} singularity storage terminals from saved positions");
                
                // Show summary of spawned terminals by monument type
                var summary = new Dictionary<string, int>();
                foreach (var terminal in activeTerminals.Values)
                {
                    if (!summary.ContainsKey(terminal.MonumentName))
                        summary[terminal.MonumentName] = 0;
                    summary[terminal.MonumentName]++;
                }
                
                foreach (var kvp in summary)
                {
                    if (monumentCounts.ContainsKey(kvp.Key) && monumentCounts[kvp.Key] > 1)
                    {
                        Puts($"  - {kvp.Key}: {kvp.Value} terminals at {monumentCounts[kvp.Key]} monument instances");
                    }
                    else
                    {
                        Puts($"  - {kvp.Key}: {kvp.Value} terminal(s)");
                    }
                }
            }
            else
            {
                Puts("No valid terminal positions found. Use /singularityadmin spawn to create terminals.");
            }
        }
        
        private Vector3 CalculateTerminalPosition(MonumentInfo monument)
        {
            var bounds = monument.Bounds;
            var center = bounds.center;
            return new Vector3(center.x + bounds.extents.x * 0.8f, center.y, center.z);
        }
        
        private void SpawnTerminal(Vector3 position, float rotation, string monumentName, MonumentInfo monument = null)
        {
            Puts($"[DEBUG] SpawnTerminal called with position: {position}, rotation: {rotation}");
            var entity = GameManager.server.CreateEntity(TERMINAL_PREFAB, position, Quaternion.Euler(0, rotation, 0));
            if (entity == null) 
            {
                Puts("[DEBUG] Failed to create entity!");
                return;
            }
            
            entity.skinID = config.TerminalSkinId;
            entity.enableSaving = false;
            
            entity.Spawn();
            
            var vendingMachine = entity as VendingMachine;
            if (vendingMachine != null)
            {
                vendingMachine.shopName = config.TerminalDisplayName;
                
                // Make the terminal non-modifiable
                entity.SetFlag(BaseEntity.Flags.Reserved8, true); // This flag often prevents rotation
                vendingMachine.skinID = config.TerminalSkinId;
                Puts($"[DEBUG] Post-spawn - Setting skinID to: {config.TerminalSkinId}");
                vendingMachine.SetFlag(BaseEntity.Flags.Reserved1, true);
                vendingMachine.SendNetworkUpdateImmediate();
                Puts($"[DEBUG] Final skinID after network update: {vendingMachine.skinID}");
            }
            
            var terminal = new StorageTerminal
            {
                Entity = entity,
                MonumentName = monumentName,
                Position = position,
                Monument = monument
            };
            
            activeTerminals[entity.net.ID.Value] = terminal;
        }
        
        private string GetCardinalName(float rotation)
        {
            // Normalize rotation to 0-360
            rotation = rotation % 360;
            if (rotation < 0) rotation += 360;
            
            if (rotation < 45 || rotation >= 315)
                return "North";
            else if (rotation < 135)
                return "East";
            else if (rotation < 225)
                return "South";
            else
                return "West";
        }
        
        private float SnapToCardinal(float rotation)
        {
            // Normalize rotation to 0-360
            rotation = rotation % 360;
            if (rotation < 0) rotation += 360;
            
            // Snap to nearest 90 degrees (N=0, E=90, S=180, W=270)
            if (rotation < 45 || rotation >= 315)
                return 0f;      // North
            else if (rotation < 135)
                return 90f;     // East
            else if (rotation < 225)
                return 180f;    // South
            else
                return 270f;    // West
        }
        
        // Picture frame display removed - using green vending machine skin instead
        /*
        private void SpawnTerminalDisplay(BaseEntity terminal, float rotation)
        {
            // Removed - no longer using picture frames
        }
        */
        
        private Vector3 SnapToGround(Vector3 position)
        {
            RaycastHit hit;
            
            Puts($"[DEBUG] SnapToGround called with position: {position}");
            
            // Start from slightly below the position to ensure we're not starting inside a collider
            Vector3 rayStart = position + Vector3.down * 0.1f;
            
            // First try raycasting down from the current position
            // This allows spawning under roofs and inside buildings
            if (Physics.Raycast(rayStart, Vector3.down, out hit, 50f, LayerMask.GetMask("Terrain", "World", "Construction", "Deployed")))
            {
                Puts($"[DEBUG] First raycast hit at: {hit.point}, hit object: {hit.collider?.name}");
                // Add a small offset to prevent clipping into ground
                return hit.point + Vector3.up * 0.1f;
            }
            
            // If no ground found below, try from the exact position
            if (Physics.Raycast(position, Vector3.down, out hit, 50f, LayerMask.GetMask("Terrain", "World", "Construction", "Deployed")))
            {
                Puts($"[DEBUG] Second raycast hit at: {hit.point}, hit object: {hit.collider?.name}");
                return hit.point + Vector3.up * 0.1f;
            }
            
            // Last resort: try from slightly above (but only 0.5m, not 2m)
            // This is only for edge cases where the position might be slightly embedded
            rayStart = position + Vector3.up * 0.5f;
            if (Physics.Raycast(rayStart, Vector3.down, out hit, 50f, LayerMask.GetMask("Terrain", "World", "Construction", "Deployed")))
            {
                Puts($"[DEBUG] Third raycast hit at: {hit.point}, hit object: {hit.collider?.name}");
                return hit.point + Vector3.up * 0.1f;
            }
            
            // Fallback to original position
            Puts($"[DEBUG] No ground found, using original position");
            return position;
        }
        
        private string GetMonumentName(MonumentInfo monument)
        {
            // Common monument name mappings for consistency across instances
            if (monument.name.Contains("bandit", System.StringComparison.OrdinalIgnoreCase))
                return "Bandit Camp";
            else if (monument.name.Contains("compound", System.StringComparison.OrdinalIgnoreCase) || 
                     monument.name.Contains("outpost", System.StringComparison.OrdinalIgnoreCase))
                return "Outpost";
            else if (monument.name.Contains("fishing", System.StringComparison.OrdinalIgnoreCase))
                return "Fishing Village";
            else if (monument.name.Contains("lighthouse", System.StringComparison.OrdinalIgnoreCase))
                return "Lighthouse";
            else if (monument.name.Contains("gas_station", System.StringComparison.OrdinalIgnoreCase) ||
                     monument.name.Contains("gasstation", System.StringComparison.OrdinalIgnoreCase))
                return "Gas Station";
            else if (monument.name.Contains("supermarket", System.StringComparison.OrdinalIgnoreCase))
                return "Supermarket";
            else if (monument.name.Contains("mining_quarry", System.StringComparison.OrdinalIgnoreCase))
                return "Mining Outpost";
            else if (monument.name.Contains("warehouse", System.StringComparison.OrdinalIgnoreCase))
                return "Warehouse";
            else if (monument.name.Contains("water_treatment", System.StringComparison.OrdinalIgnoreCase))
                return "Water Treatment Plant";
            else if (monument.name.Contains("airfield", System.StringComparison.OrdinalIgnoreCase))
                return "Airfield";
            else if (monument.name.Contains("powerplant", System.StringComparison.OrdinalIgnoreCase))
                return "Power Plant";
            else if (monument.name.Contains("trainyard", System.StringComparison.OrdinalIgnoreCase))
                return "Train Yard";
            else if (monument.name.Contains("junkyard", System.StringComparison.OrdinalIgnoreCase))
                return "Junkyard";
            else if (monument.name.Contains("radtown_small", System.StringComparison.OrdinalIgnoreCase))
                return "Radtown";
            else if (monument.name.Contains("sphere_tank", System.StringComparison.OrdinalIgnoreCase))
                return "Sphere Tank";
            else if (monument.name.Contains("satellite", System.StringComparison.OrdinalIgnoreCase))
                return "Satellite Dish";
            else if (monument.name.Contains("cave", System.StringComparison.OrdinalIgnoreCase))
                return "Cave";
            else if (monument.name.Contains("harbor", System.StringComparison.OrdinalIgnoreCase))
                return "Harbor";
            else if (monument.name.Contains("oilrig", System.StringComparison.OrdinalIgnoreCase))
            {
                if (monument.name.Contains("small"))
                    return "Small Oil Rig";
                else
                    return "Large Oil Rig";
            }
            
            // Fallback to display name
            string displayName = monument.displayPhrase?.english ?? monument.name;
            
            // Clean up the display name to remove instance numbers
            displayName = System.Text.RegularExpressions.Regex.Replace(displayName, @"\s*\d+$", "");
            
            return displayName;
        }
        
        // Image download removed - using green vending machine skin instead
        /*
        private IEnumerator DownloadAndApplyImage(BaseEntity signEntity, string url)
        {
            // Removed - no longer using picture frames
        }
        */
        
        private void CheckAndLoadNearbyTextures()
        {
            if (!config.UseCustomFaceTexture || activeTerminals.Count == 0) return;
            
            foreach (var terminal in activeTerminals.Values)
            {
                if (terminal?.DisplayEntity == null || terminal.DisplayEntity.IsDestroyed) continue;
                
                var sign = terminal.DisplayEntity as Signage;
                if (sign == null) continue;
                
                // Ensure sign stays locked to prevent player drawing
                if (!sign.HasFlag(BaseEntity.Flags.Locked))
                {
                    sign.SetFlag(BaseEntity.Flags.Locked, true);
                    sign.SendNetworkUpdate();
                }
                
                // Just keep it locked
                sign.SetFlag(BaseEntity.Flags.Locked, true);
                
                // Check if texture needs to be loaded or restored
                bool needsTexture = false;
                bool forceRestore = false;
                
                if (sign.textureIDs == null || sign.textureIDs.Length == 0 || sign.textureIDs[0] == 0)
                {
                    // No texture loaded yet
                    needsTexture = true;
                }
                else if (terminalTextureIds.ContainsKey(sign.net.ID.Value))
                {
                    // Check if texture has been modified by a player
                    if (sign.textureIDs[0] != terminalTextureIds[sign.net.ID.Value])
                    {
                        // Texture was changed - restore it immediately
                        needsTexture = true;
                        forceRestore = true;
                    }
                }
                else
                {
                    // We don't have a record of the correct texture ID yet
                    needsTexture = true;
                }
                
                if (needsTexture)
                {
                    // Picture frames no longer used - using green vending machine skin
                    // ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(sign, config.TerminalFaceTextureUrl));
                }
            }
        }
        
        private void SchedulePeriodicRedraw()
        {
            return; // Picture frames no longer used - using green vending machine skin
            if (!config.UseCustomFaceTexture) return;
            
            // Random time between 10-13 minutes (600-780 seconds)
            var randomDelay = UnityEngine.Random.Range(600f, 780f);
            
            Puts($"[DEBUG] Scheduling next periodic texture redraw in {randomDelay:F0} seconds ({randomDelay/60f:F1} minutes)");
            
            timer.Once(randomDelay, () =>
            {
                PeriodicRedrawAllTextures();
                // Schedule the next redraw
                SchedulePeriodicRedraw();
            });
        }
        
        private void PeriodicRedrawAllTextures()
        {
            if (!config.UseCustomFaceTexture || activeTerminals.Count == 0) return;
            
            Puts($"[DEBUG] Performing periodic texture redraw for all {activeTerminals.Count} terminals");
            
            foreach (var terminal in activeTerminals.Values)
            {
                if (terminal?.DisplayEntity == null || terminal.DisplayEntity.IsDestroyed) continue;
                
                var sign = terminal.DisplayEntity as Signage;
                if (sign == null) continue;
                
                // Picture frames no longer used - using green vending machine skin
                // ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(sign, config.TerminalFaceTextureUrl));
            }
            
            Puts($"[DEBUG] Periodic texture redraw completed");
        }
        
        #endregion
        
        #region Storage Management
        
        private void OpenCloudStorage(BasePlayer player, StorageTerminal terminal)
        {
            // Clean up any existing storage
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                var oldStorage = playerActiveStorage[player.userID];
                if (oldStorage != null && !oldStorage.IsDestroyed)
                {
                    oldStorage.Kill();
                }
                playerActiveStorage.Remove(player.userID);
            }
            
            // Create invisible storage container near the player
            var storage = GameManager.server.CreateEntity(STORAGE_PREFAB, player.transform.position + new Vector3(0, -5, 0)) as StorageContainer;
            if (storage == null) return;
            
            storage.enableSaving = false;
            storage.globalBroadcast = false;
            storage.SendNetworkUpdate();
            storage.Spawn();
            
            // Set inventory size based on player's tier
            var tier = GetPlayerStorageTier(player);
            var slots = GetTierSlots(tier);
            storage.inventory.capacity = slots;
            storage.panelName = "generic_resizable";
            storage.isLootable = true;
            
            // Load saved items
            var playerData = GetPlayerStorage(player.userID);
            LoadItemsIntoContainer(storage, playerData);
            
            // Track active storage
            playerActiveStorage[player.userID] = storage;
            
            // Open the storage UI
            timer.Once(0.1f, () =>
            {
                if (storage != null && !storage.IsDestroyed && player != null && player.IsConnected)
                {
                    player.inventory.loot.Clear();
                    player.inventory.loot.PositionChecks = false;
                    player.inventory.loot.entitySource = storage;
                    player.inventory.loot.itemSource = null;
                    player.inventory.loot.containers = new List<ItemContainer> { storage.inventory };
                    player.inventory.loot.MarkDirty();
                    player.inventory.loot.SendImmediate();
                    player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", "generic_resizable");
                    
                    // Create and show the tier info UI
                    CreateStorageTierUI(player);
                    
                    // Listen for inventory changes
                    storage.inventory.onDirty += () => OnStorageChanged(player, storage);
                }
            });
        }
        
        private void OnStorageChanged(BasePlayer player, StorageContainer storage)
        {
            if (player == null || !player.IsConnected || storage == null || storage.IsDestroyed)
                return;
            
            // Force UI refresh
            player.inventory.loot.SendImmediate();
            storage.SendNetworkUpdate();
            
            // Update the tier UI to reflect current item count and scrap
            CreateStorageTierUI(player);
        }
        
        private PlayerStorageData GetPlayerStorage(ulong playerId)
        {
            if (!playerStorage.ContainsKey(playerId))
            {
                playerStorage[playerId] = new PlayerStorageData
                {
                    PlayerId = playerId,
                    Items = new List<ItemData>(),
                    LastAccessed = DateTime.UtcNow,
                    StorageTier = 1,
                    WipesSurvived = 0,
                    FirstCreated = DateTime.UtcNow,
                    LastWipeDate = DateTime.MinValue,
                    CurrentTierWipes = 0,  // Ensure this starts at 0
                    TierUpgradeDate = DateTime.UtcNow
                };
            }
            
            return playerStorage[playerId];
        }
        
        private void LoadItemsIntoContainer(StorageContainer container, PlayerStorageData data)
        {
            Puts($"Loading {data.Items.Count} items for player");
            foreach (var itemData in data.Items)
            {
                var itemDef = ItemManager.FindItemDefinition(itemData.ItemId);
                if (itemDef == null) continue;
                
                var item = ItemManager.Create(itemDef, itemData.Amount, itemData.Skin);
                if (item == null) continue;
                
                item.condition = itemData.Condition;
                item.text = itemData.Text;
                item.name = itemData.Name;
                
                // Handle weapon ammo
                if (item.GetHeldEntity() is BaseProjectile projectile && itemData.Ammo > 0)
                {
                    projectile.primaryMagazine.contents = itemData.Ammo;
                    if (!string.IsNullOrEmpty(itemData.AmmoType))
                    {
                        var ammoDef = ItemManager.FindItemDefinition(itemData.AmmoType);
                        if (ammoDef != null)
                        {
                            projectile.primaryMagazine.ammoType = ammoDef;
                        }
                    }
                }
                
                // Handle container contents
                if (item.contents != null && itemData.Contents.Count > 0)
                {
                    foreach (var contentData in itemData.Contents)
                    {
                        var contentDef = ItemManager.FindItemDefinition(contentData.ItemId);
                        if (contentDef == null) continue;
                        
                        var contentItem = ItemManager.Create(contentDef, contentData.Amount, contentData.Skin);
                        if (contentItem == null) continue;
                        
                        contentItem.condition = contentData.Condition;
                        contentItem.MoveToContainer(item.contents, contentData.Position);
                    }
                }
                
                item.MoveToContainer(container.inventory, itemData.Position);
            }
        }
        
        private void SaveContainerToStorage(BasePlayer player, StorageContainer container)
        {
            var data = GetPlayerStorage(player.userID);
            data.Items.Clear();
            data.LastAccessed = DateTime.UtcNow;
            
            var blacklistedItems = new List<Item>();
            
            for (int i = 0; i < container.inventory.capacity; i++)
            {
                var item = container.inventory.GetSlot(i);
                if (item == null) continue;
                
                if (!CanStoreItem(player, item))
                {
                    blacklistedItems.Add(item);
                    continue;
                }
                
                var itemData = new ItemData
                {
                    ItemId = item.info.itemid,
                    Amount = item.amount,
                    Skin = item.skin,
                    Condition = item.condition,
                    Position = i,
                    Text = item.text,
                    Name = item.name
                };
                
                // Save weapon ammo
                if (item.GetHeldEntity() is BaseProjectile projectile)
                {
                    itemData.Ammo = projectile.primaryMagazine.contents;
                    itemData.AmmoType = projectile.primaryMagazine.ammoType?.shortname;
                }
                
                // Save container contents
                if (item.contents != null && item.contents.itemList.Count > 0)
                {
                    foreach (var contentItem in item.contents.itemList)
                    {
                        itemData.Contents.Add(new ItemData
                        {
                            ItemId = contentItem.info.itemid,
                            Amount = contentItem.amount,
                            Skin = contentItem.skin,
                            Condition = contentItem.condition,
                            Position = contentItem.position
                        });
                    }
                }
                
                data.Items.Add(itemData);
            }
            
            Puts($"Saved {data.Items.Count} items for player {player.displayName}");
            SaveData();
            
            // Return blacklisted items to player
            if (blacklistedItems.Count > 0)
            {
                SendReply(player, $"<color=#ff0000>Warning:</color> {blacklistedItems.Count} item(s) cannot be stored (blacklisted)");
                foreach (var item in blacklistedItems)
                {
                    player.GiveItem(item);
                }
            }
        }
        
        private int GetPlayerStorageTier(BasePlayer player)
        {
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_TIER5))
                return 5;
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_TIER4))
                return 4;
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_TIER3))
                return 3;
            if (permission.UserHasPermission(player.UserIDString, PERMISSION_TIER2))
                return 2;
            return 1; // Default tier 1 for basic use permission
        }
        
        private int GetTierSlots(int tier)
        {
            switch (tier)
            {
                case 1: return 6;
                case 2: return 12;
                case 3: return 24;
                case 4: return 48;
                case 5: return 96;
                default: return 6;
            }
        }
        
        private int GetTierScrapLimit(int tier)
        {
            switch (tier)
            {
                case 1: return 1000;
                case 2: return 5000;
                case 3: return 10000;
                case 4: return 25000;
                case 5: return -1; // No limit
                default: return 1000;
            }
        }
        
        private int GetTierUpgradeCost(int currentTier)
        {
            switch (currentTier)
            {
                case 1: return 2000; // Tier 1 to 2
                case 2: return 4000; // Tier 2 to 3
                case 3: return 8000; // Tier 3 to 4
                case 4: return 16000; // Tier 4 to 5
                default: return -1; // Max tier or invalid
            }
        }
        
        private int GetTierUpkeepCost(int tier)
        {
            switch (tier)
            {
                case 2: return 2000; // Keep tier 2
                case 3: return 6000; // Keep tier 3 (2000 + 4000)
                case 4: return 14000; // Keep tier 4 (2000 + 4000 + 8000)
                case 5: return 30000; // Keep tier 5 (2000 + 4000 + 8000 + 16000)
                default: return 0; // Tier 1 has no upkeep
            }
        }
        
        private int GetPlayerInventoryScrap(BasePlayer player)
        {
            if (player == null) return 0;
            
            int totalScrap = 0;
            
            // Check main inventory
            foreach (var item in player.inventory.containerMain.itemList)
            {
                if (item.info.shortname == "scrap")
                {
                    totalScrap += item.amount;
                }
            }
            
            // Check belt
            foreach (var item in player.inventory.containerBelt.itemList)
            {
                if (item.info.shortname == "scrap")
                {
                    totalScrap += item.amount;
                }
            }
            
            return totalScrap;
        }
        
        private int GetCurrentScrapTotal(ulong playerId, ItemContainer container)
        {
            // When a container is open, the saved items have already been loaded into it
            // So we should ONLY count what's in the container, not the saved storage
            var containerScrap = 0;
            if (container != null)
            {
                for (int i = 0; i < container.capacity; i++)
                {
                    var containerItem = container.GetSlot(i);
                    if (containerItem != null && containerItem.info.shortname == "scrap")
                    {
                        containerScrap += containerItem.amount;
                    }
                }
            }
            
            Puts($"[DEBUG] GetCurrentScrapTotal - Container scrap: {containerScrap}");
            
            return containerScrap;
        }
        
        private int GetPlayerScrapInStorage(ulong playerId)
        {
            var playerData = GetPlayerStorage(playerId);
            var scrapCount = 0;
            
            foreach (var item in playerData.Items)
            {
                if (item.ItemId == -932201673) // Scrap item ID
                {
                    scrapCount += item.Amount;
                }
            }
            
            // Also check if player has active storage open and count scrap in there
            if (playerActiveStorage.ContainsKey(playerId))
            {
                var storage = playerActiveStorage[playerId] as StorageContainer;
                if (storage != null && !storage.IsDestroyed)
                {
                    for (int i = 0; i < storage.inventory.capacity; i++)
                    {
                        var containerItem = storage.inventory.GetSlot(i);
                        if (containerItem != null && containerItem.info.shortname == "scrap")
                        {
                            scrapCount += containerItem.amount;
                        }
                    }
                }
            }
            
            return scrapCount;
        }
        
        private bool CanStoreItem(BasePlayer player, Item item)
        {
            Puts($"[DEBUG] CanStoreItem called for {item.info.shortname}");
            
            if (!config.AllowBlacklistedItems && config.BlacklistedItems.Contains(item.info.shortname))
            {
                return false;
            }
            
            // Check if filtering is enabled
            if (config.OnlyAllowResourcesAndComponents)
            {
                var category = item.info.category;
                var shortname = item.info.shortname;
                
                // Allow resources category
                if (category == ItemCategory.Resources)
                {
                    return true;
                }
                
                // Allow components category
                if (category == ItemCategory.Component)
                {
                    return true;
                }
                
                // Special case: Scrap is categorized as "Items" but we want to allow it
                if (shortname == "scrap")
                {
                    // Check scrap limit for player's tier
                    var tier = GetPlayerStorageTier(player);
                    var scrapLimit = GetTierScrapLimit(tier);
                    
                    Puts($"[DEBUG] Scrap check - Player: {player.displayName}, Tier: {tier}, Limit: {scrapLimit}");
                    
                    if (scrapLimit != -1) // If there's a limit
                    {
                        // Get current scrap amount in storage
                        var currentScrap = GetPlayerScrapInStorage(player.userID);
                        var totalAfterAdd = currentScrap + item.amount;
                        
                        Puts($"[DEBUG] Current scrap: {currentScrap}, Adding: {item.amount}, Total would be: {totalAfterAdd}");
                        
                        if (totalAfterAdd > scrapLimit)
                        {
                            SendReply(player, $"<color=#ff0000>Scrap limit for Tier {tier} is {scrapLimit:N0}. You currently have {currentScrap:N0} stored. Cannot add {item.amount} more.</color>");
                            return false;
                        }
                    }
                    
                    return true;
                }
                
                // Reject everything else
                SendReply(player, $"<color=#ff0000>Only resources and components can be stored in the Singularity Terminal. '{item.info.displayName.english}' is not allowed.</color>");
                return false;
            }
            
            return true;
        }
        
        #endregion
        
        #region Hooks
        
        // Hook to check if container can accept item BEFORE it's added
        private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
        {
            if (container == null || item == null) return null;
            
            // Check if this container belongs to one of our quantum storage entities
            var storage = container.entityOwner as StorageContainer;
            if (storage == null) return null;
            
            // Find which player has this storage open
            BasePlayer player = null;
            foreach (var kvp in playerActiveStorage)
            {
                if (kvp.Value == storage)
                {
                    player = BasePlayer.FindByID(kvp.Key);
                    break;
                }
            }
            
            if (player == null) return null;
            
            Puts($"[DEBUG] CanAcceptItem called for {item.info.shortname}, amount: {item.amount}");
            
            // Check scrap limits
            if (item.info.shortname == "scrap")
            {
                var tier = GetPlayerStorageTier(player);
                var scrapLimit = GetTierScrapLimit(tier);
                
                if (scrapLimit != -1)
                {
                    // Get current scrap (saved + in container)
                    var currentScrap = GetCurrentScrapTotal(player.userID, container);
                    var totalAfterAdd = currentScrap + item.amount;
                    
                    Puts($"[DEBUG] Scrap limit check - Current: {currentScrap}, Adding: {item.amount}, Total: {totalAfterAdd}, Limit: {scrapLimit}");
                    
                    if (totalAfterAdd > scrapLimit)
                    {
                        // Calculate how much we can accept
                        var canAccept = scrapLimit - currentScrap;
                        
                        if (canAccept <= 0)
                        {
                            SendReply(player, $"<color=#ff0000>Scrap storage full! Tier {tier} limit is {scrapLimit:N0}.</color>");
                            return ItemContainer.CanAcceptResult.CannotAccept;
                        }
                        
                        // We'll handle partial transfers in OnItemAddedToContainer
                        return null; // Allow it to be added, then we'll split it
                    }
                }
            }
            
            // Check general item restrictions
            if (!CanStoreItem(player, item))
            {
                return ItemContainer.CanAcceptResult.CannotAccept;
            }
            
            return null;
        }
        
        // Handle partial scrap transfers
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (container == null || item == null) return;
            
            // Check if this container belongs to one of our quantum storage entities
            var storage = container.entityOwner as StorageContainer;
            if (storage == null) return;
            
            // Only handle scrap
            if (item.info.shortname != "scrap") return;
            
            // Find which player has this storage open
            BasePlayer player = null;
            foreach (var kvp in playerActiveStorage)
            {
                if (kvp.Value == storage)
                {
                    player = BasePlayer.FindByID(kvp.Key);
                    break;
                }
            }
            
            if (player == null) return;
            
            var tier = GetPlayerStorageTier(player);
            var scrapLimit = GetTierScrapLimit(tier);
            
            if (scrapLimit == -1) return; // No limit
            
            // Count total scrap in container only (saved items are already loaded)
            var totalScrap = 0;
            for (int i = 0; i < container.capacity; i++)
            {
                var containerItem = container.GetSlot(i);
                if (containerItem != null && containerItem.info.shortname == "scrap")
                {
                    totalScrap += containerItem.amount;
                }
            }
            
            Puts($"[DEBUG] OnItemAddedToContainer - Total scrap in container: {totalScrap}, Limit: {scrapLimit}");
            
            if (totalScrap > scrapLimit)
            {
                // Calculate excess
                var excess = totalScrap - scrapLimit;
                var keepAmount = item.amount - excess;
                
                if (keepAmount > 0)
                {
                    // Partial transfer
                    item.amount = keepAmount;
                    item.MarkDirty();
                    
                    // Give back excess
                    var excessItem = ItemManager.Create(item.info, excess, item.skin);
                    if (excessItem != null)
                    {
                        timer.Once(0.1f, () =>
                        {
                            if (player != null && player.IsConnected)
                            {
                                player.GiveItem(excessItem);
                                SendReply(player, $"<color=#ffff00>Scrap limit reached. Stored {keepAmount} scrap, returned {excess} to inventory.</color>");
                            }
                        });
                    }
                }
                else
                {
                    // Remove entire item
                    item.RemoveFromContainer();
                    timer.Once(0.1f, () =>
                    {
                        if (player != null && player.IsConnected && item != null)
                        {
                            player.GiveItem(item);
                            SendReply(player, $"<color=#ff0000>Scrap storage full! Returned {item.amount} scrap to inventory.</color>");
                        }
                    });
                }
            }
        }
        
        private object CanUseVending(BasePlayer player, VendingMachine machine)
        {
            if (activeTerminals.ContainsKey(machine.net.ID.Value))
            {
                if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
                {
                    SendReply(player, "You don't have permission to access the Singularity Storage.");
                    return false;
                }
                
                // Update vending machine display with player's tier info
                UpdateVendingDisplay(player, machine);
                
                OpenCloudStorage(player, activeTerminals[machine.net.ID.Value]);
                return false;
            }
            
            return null;
        }
        
        private void UpdateVendingDisplay(BasePlayer player, VendingMachine machine)
        {
            if (machine == null || player == null) return;
            
            var tier = GetPlayerStorageTier(player);
            var playerData = GetPlayerStorage(player.userID);
            var itemCount = playerData.Items.Count;
            var maxSlots = GetTierSlots(tier);
            var scrapCapacity = GetTierScrapLimit(tier);
            var currentScrap = GetPlayerScrapInStorage(player.userID);
            
            // Update shop name with tier and usage info
            machine.shopName = $"Singularity T{tier} [{itemCount}/{maxSlots}]";
            
            // Note: We can't modify sell orders on vending machines easily
            // The shop name will show the important info
            
            // Don't broadcast on map
            machine.SetFlag(BaseEntity.Flags.Reserved4, false);
            
            machine.SendNetworkUpdateImmediate();
        }
        
        private void OnLootEntityEnd(BasePlayer player, BaseEntity entity)
        {
            if (playerActiveStorage.ContainsKey(player.userID) && playerActiveStorage[player.userID] == entity)
            {
                
                var container = entity as StorageContainer;
                if (container != null)
                {
                    SaveContainerToStorage(player, container);
                }
                
                // Remove the UI
                DestroyStorageTierUI(player);
                
                // Delay the kill to ensure save completes
                timer.Once(0.1f, () =>
                {
                    if (entity != null && !entity.IsDestroyed)
                    {
                        entity.Kill();
                    }
                });
                
                playerActiveStorage.Remove(player.userID);
            }
        }
        
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            
            // Clean up any leftover UI for this player
            // Use a small delay to ensure player is fully connected
            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected)
                {
                    CuiHelper.DestroyUi(player, "SingularityStorageTierPanel");
                    CuiHelper.DestroyUi(player, "SingularityStorageTierPanel_upgrade");
                    CuiHelper.DestroyUi(player, "SingularityStorageTierPanel_warning");
                    CuiHelper.DestroyUi(player, "SingularityStorageTierPanel_upkeep");
                }
            });
            
            // Check if player needs to pay upkeep and notify them
            timer.Once(5f, () =>
            {
                if (player != null && player.IsConnected && permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
                {
                    var tier = GetPlayerStorageTier(player);
                    if (tier > 1)
                    {
                        var playerData = GetPlayerStorage(player.userID);
                        if (playerData.CurrentTierWipes >= 1)
                        {
                            var upkeepCost = GetTierUpkeepCost(tier);
                            SendReply(player, $"<color=#ff0000>⚠️ SINGULARITY STORAGE UPKEEP WARNING!</color>");
                            SendReply(player, $"<color=#ffff00>Your Tier {tier} storage needs upkeep payment!</color>");
                            SendReply(player, $"<color=#ffff00>Cost: {upkeepCost:N0} scrap</color>");
                            SendReply(player, $"<color=#ff0000>If not paid, you will be downgraded to Tier 1 next wipe!</color>");
                            SendReply(player, "Access any Singularity Terminal to pay upkeep.");
                        }
                    }
                }
            });
        }
        
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                var storage = playerActiveStorage[player.userID];
                if (storage != null && !storage.IsDestroyed)
                {
                    var container = storage as StorageContainer;
                    if (container != null)
                    {
                        SaveContainerToStorage(player, container);
                    }
                    storage.Kill();
                }
                
                // Remove the UI
                DestroyStorageTierUI(player);
                
                playerActiveStorage.Remove(player.userID);
            }
        }
        
        #endregion
        
        #region Commands
        
        [ConsoleCommand("singularity.upkeep")]
        private void CmdPayUpkeep(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            var currentTier = GetPlayerStorageTier(player);
            if (currentTier <= 1)
            {
                SendReply(player, "<color=#ff0000>Tier 1 doesn't require upkeep!</color>");
                return;
            }
            
            var playerData = GetPlayerStorage(player.userID);
            if (playerData.CurrentTierWipes < 1)
            {
                SendReply(player, "<color=#ff0000>Upkeep is not due yet!</color>");
                return;
            }
            
            var upkeepCost = GetTierUpkeepCost(currentTier);
            var playerScrap = GetPlayerInventoryScrap(player);
            
            if (playerScrap < upkeepCost)
            {
                SendReply(player, $"<color=#ff0000>Insufficient scrap! You need {upkeepCost:N0} scrap to pay upkeep for Tier {currentTier}. You have {playerScrap:N0}.</color>");
                return;
            }
            
            // Remove scrap from player inventory
            int scrapToRemove = upkeepCost;
            
            // Remove from main inventory first
            foreach (var item in player.inventory.containerMain.itemList.ToList())
            {
                if (item.info.shortname == "scrap" && scrapToRemove > 0)
                {
                    if (item.amount <= scrapToRemove)
                    {
                        scrapToRemove -= item.amount;
                        item.Remove();
                    }
                    else
                    {
                        item.amount -= scrapToRemove;
                        scrapToRemove = 0;
                        item.MarkDirty();
                    }
                }
            }
            
            // Remove from belt if needed
            foreach (var item in player.inventory.containerBelt.itemList.ToList())
            {
                if (item.info.shortname == "scrap" && scrapToRemove > 0)
                {
                    if (item.amount <= scrapToRemove)
                    {
                        scrapToRemove -= item.amount;
                        item.Remove();
                    }
                    else
                    {
                        item.amount -= scrapToRemove;
                        scrapToRemove = 0;
                        item.MarkDirty();
                    }
                }
            }
            
            // Verify scrap was actually removed
            if (scrapToRemove > 0)
            {
                SendReply(player, "<color=#ff0000>Error: Failed to remove scrap from inventory. Upkeep payment cancelled.</color>");
                Puts($"[ERROR] Failed to remove {scrapToRemove} scrap from {player.displayName}'s inventory during upkeep payment");
                return;
            }
            
            // Reset the tier wipe counter
            playerData.CurrentTierWipes = 0;
            playerData.TierUpgradeDate = DateTime.UtcNow;
            SaveData();
            
            SendReply(player, $"<color=#00ff00>Upkeep paid! Your Tier {currentTier} status is secured for another wipe.</color>");
            SendReply(player, $"<color=#00ff00>You spent {upkeepCost:N0} scrap to maintain your tier.</color>");
            
            // Refresh the UI
            CreateStorageTierUI(player);
            
            // Log the upkeep payment
            Puts($"[UPKEEP] {player.displayName} ({player.UserIDString}) paid {upkeepCost} scrap to maintain Tier {currentTier}");
        }
        
        [ConsoleCommand("singularity.clearui")]
        private void CmdClearUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            DestroyStorageTierUI(player);
            SendReply(player, "UI cleared.");
        }
        
        [ConsoleCommand("singularity.forcerefreshui")]
        private void CmdForceRefreshUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            DestroyStorageTierUI(player);
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                CreateStorageTierUI(player);
                SendReply(player, "<color=#00ff00>UI refreshed</color>");
            }
            else
            {
                SendReply(player, "<color=#ffaa00>You need to open a storage terminal first</color>");
            }
        }
        
        [ConsoleCommand("singularity.setwipecounter")]
        private void CmdSetWipeCounter(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            
            // Allow RCON/Console execution
            if (player == null && (arg.IsRcon || arg.IsServerside))
            {
                if (arg.Args == null || arg.Args.Length < 2)
                {
                    Puts("Usage from console: singularity.setwipecounter <steamid> <value>");
                    return;
                }
                
                if (!ulong.TryParse(arg.Args[0], out ulong steamId))
                {
                    Puts("Invalid steam ID");
                    return;
                }
                
                if (!int.TryParse(arg.Args[1], out int consoleValue))
                {
                    Puts("Invalid value. Must be a number.");
                    return;
                }
                
                var consoleData = GetPlayerStorage(steamId);
                var oldConsoleValue = consoleData.CurrentTierWipes;
                Puts($"[RCON/Console] Setting wipe counter from {oldConsoleValue} to {consoleValue} for player {steamId}");
                consoleData.CurrentTierWipes = consoleValue;
                SaveData();
                Puts($"[RCON/Console] After SaveData, counter is: {consoleData.CurrentTierWipes}");
                
                // Force reload and verify
                LoadData();
                var verifyConsoleData = GetPlayerStorage(steamId);
                Puts($"[RCON/Console] After reload from disk, counter is: {verifyConsoleData.CurrentTierWipes}");
                return;
            }
            
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You need admin permission to use this command.");
                return;
            }
            
            if (arg.Args == null || arg.Args.Length < 1)
            {
                SendReply(player, "Usage: singularity.setwipecounter <value>");
                return;
            }
            
            if (!int.TryParse(arg.Args[0], out int value))
            {
                SendReply(player, "Invalid value. Must be a number.");
                return;
            }
            
            var playerData = GetPlayerStorage(player.userID);
            var oldValue = playerData.CurrentTierWipes;
            Puts($"[DEBUG] Setting wipe counter from {oldValue} to {value} for player {player.displayName}");
            playerData.CurrentTierWipes = value;
            SaveData();
            Puts($"[DEBUG] After setting, counter is now: {playerData.CurrentTierWipes}");
            
            // Force reload from disk to verify it saved
            LoadData();
            var verifyData = GetPlayerStorage(player.userID);
            Puts($"[DEBUG] After reload from disk, counter is: {verifyData.CurrentTierWipes}");
            
            SendReply(player, $"<color=#00ff00>Wipe counter set to {value} (was {oldValue}, verified as {verifyData.CurrentTierWipes})</color>");
            
            // Refresh UI if storage is open
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                DestroyStorageTierUI(player);
                CreateStorageTierUI(player);
            }
        }
        
        [ConsoleCommand("singularity.reloaddata")]
        private void CmdReloadData(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You need admin permission to use this command.");
                return;
            }
            
            LoadData();
            var playerData = GetPlayerStorage(player.userID);
            SendReply(player, $"<color=#00ff00>Data reloaded from disk. Your counter: {playerData.CurrentTierWipes}</color>");
        }
        
        [ConsoleCommand("singularity.forceresetallcounters")]
        private void CmdForceResetAllCounters(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You need admin permission to use this command.");
                return;
            }
            
            // Force reset ALL counters to 0
            foreach (var kvp in playerStorage)
            {
                kvp.Value.CurrentTierWipes = 0;
                kvp.Value.TierUpgradeDate = DateTime.UtcNow;
            }
            
            // Save multiple times to ensure it persists
            SaveData();
            SaveData();
            
            // Verify
            LoadData();
            var checkData = GetPlayerStorage(player.userID);
            
            SendReply(player, $"<color=#00ff00>FORCE RESET: All counters set to 0. Your counter after reload: {checkData.CurrentTierWipes}</color>");
            
            // Refresh UI
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                DestroyStorageTierUI(player);
                CreateStorageTierUI(player);
            }
        }
        
        [ChatCommand("singularity")]
        private void CmdCloud(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                SendReply(player, "You don't have permission to use cloud storage.");
                return;
            }
            
            if (args.Length == 0)
            {
                SendReply(player, "<color=#00ff00>Singularity Storage System</color>");
                SendReply(player, "Your items exist in a quantum state, transcending time and server wipes.");
                SendReply(player, "Commands:");
                SendReply(player, "  /singularity info - Display quantum storage metrics");
                SendReply(player, "  /singularity terminals - List active singularity terminals");
                SendReply(player, "  /singularity help - Show detailed help");
                return;
            }
            
            switch (args[0].ToLower())
            {
                case "info":
                    var storage = GetPlayerStorage(player.userID);
                    var tier = GetPlayerStorageTier(player);
                    var slots = GetTierSlots(tier);
                    SendReply(player, $"<color=#00ff00>Singularity Storage Metrics:</color>");
                    SendReply(player, $"Storage Tier: {tier} ({slots} slots)");
                    SendReply(player, $"Quantum items stored: {storage.Items.Count}/{slots}");
                    SendReply(player, $"Last quantum sync: {storage.LastAccessed:yyyy-MM-dd HH:mm} UTC");
                    SendReply(player, $"Storage capacity: {(storage.Items.Count * 100.0 / slots):F1}%");
                    
                    // Show wipe survival stats
                    SendReply(player, $"<color=#ffff00>Wipe Survival Stats:</color>");
                    SendReply(player, $"Total wipes survived: {storage.WipesSurvived}");
                    SendReply(player, $"Wipes at current tier: {storage.CurrentTierWipes}");
                    if (storage.FirstCreated != DateTime.MinValue)
                    {
                        var daysSinceCreation = (DateTime.UtcNow - storage.FirstCreated).TotalDays;
                        SendReply(player, $"Storage age: {daysSinceCreation:F0} days");
                    }
                    
                    // Show scrap limit info
                    var scrapLimit = GetTierScrapLimit(tier);
                    var currentScrap = GetPlayerScrapInStorage(player.userID);
                    if (scrapLimit != -1)
                    {
                        SendReply(player, $"Scrap stored: {currentScrap:N0}/{scrapLimit:N0} ({(currentScrap * 100.0 / scrapLimit):F1}%)");
                    }
                    else
                    {
                        SendReply(player, $"Scrap stored: {currentScrap:N0} (no limit)");
                    }
                    break;
                    
                case "terminals":
                    SendReply(player, "<color=#00ff00>Active Singularity Terminals:</color>");
                    if (activeTerminals.Count == 0)
                    {
                        SendReply(player, "No terminals currently active. Contact an admin.");
                    }
                    else
                    {
                        var terminalsByMonument = activeTerminals.Values
                            .GroupBy(t => t.MonumentName)
                            .OrderBy(g => g.Key);
                        foreach (var group in terminalsByMonument)
                        {
                            SendReply(player, $"- {group.Key}: {group.Count()} terminal(s)");
                        }
                    }
                    break;
                    
                case "help":
                    SendReply(player, "<color=#00ff00>Singularity Storage - Help</color>");
                    SendReply(player, "Access terminals at major monuments to store items.");
                    SendReply(player, "Items persist through server wipes and map changes.");
                    var currentTier = GetPlayerStorageTier(player);
                    var currentSlots = GetTierSlots(currentTier);
                    var currentScrapLimit = GetTierScrapLimit(currentTier);
                    var scrapLimitText = currentScrapLimit == -1 ? "no limit" : $"{currentScrapLimit:N0}";
                    SendReply(player, $"Your storage tier: {currentTier} ({currentSlots} slots, {scrapLimitText} scrap limit)");
                    if (currentTier < 5)
                    {
                        var nextScrapLimit = GetTierScrapLimit(currentTier + 1);
                        var nextScrapText = nextScrapLimit == -1 ? "no limit" : $"{nextScrapLimit:N0}";
                        SendReply(player, $"<color=#ffff00>Next tier: {currentTier + 1} ({GetTierSlots(currentTier + 1)} slots, {nextScrapText} scrap)</color>");
                    }
                    if (!config.AllowBlacklistedItems && config.BlacklistedItems.Count > 0)
                    {
                        SendReply(player, "Note: Explosive items cannot be stored.");
                    }
                    break;
                    
                default:
                    SendReply(player, "Unknown command. Use: /singularity info, terminals, or help");
                    break;
            }
        }
        
        [ConsoleCommand("singularity.upgrade")]
        private void CmdUpgradeTier(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_USE))
            {
                SendReply(player, "You don't have permission to use singularity storage.");
                return;
            }
            
            var currentTier = GetPlayerStorageTier(player);
            if (currentTier >= 5)
            {
                SendReply(player, "<color=#ff0000>You are already at the maximum tier!</color>");
                return;
            }
            
            var upgradeCost = GetTierUpgradeCost(currentTier);
            var playerScrap = GetPlayerInventoryScrap(player);
            
            if (playerScrap < upgradeCost)
            {
                SendReply(player, $"<color=#ff0000>Insufficient scrap! You need {upgradeCost:N0} scrap to upgrade to Tier {currentTier + 1}. You have {playerScrap:N0}.</color>");
                return;
            }
            
            // Remove scrap from player inventory
            int scrapToRemove = upgradeCost;
            
            // Remove from main inventory first
            foreach (var item in player.inventory.containerMain.itemList.ToList())
            {
                if (item.info.shortname == "scrap" && scrapToRemove > 0)
                {
                    if (item.amount <= scrapToRemove)
                    {
                        scrapToRemove -= item.amount;
                        item.Remove();
                    }
                    else
                    {
                        item.amount -= scrapToRemove;
                        scrapToRemove = 0;
                        item.MarkDirty();
                    }
                }
            }
            
            // Remove from belt if needed
            foreach (var item in player.inventory.containerBelt.itemList.ToList())
            {
                if (item.info.shortname == "scrap" && scrapToRemove > 0)
                {
                    if (item.amount <= scrapToRemove)
                    {
                        scrapToRemove -= item.amount;
                        item.Remove();
                    }
                    else
                    {
                        item.amount -= scrapToRemove;
                        scrapToRemove = 0;
                        item.MarkDirty();
                    }
                }
            }
            
            // Verify scrap was actually removed
            if (scrapToRemove > 0)
            {
                SendReply(player, "<color=#ff0000>Error: Failed to remove scrap from inventory. Upgrade cancelled.</color>");
                Puts($"[ERROR] Failed to remove {scrapToRemove} scrap from {player.displayName}'s inventory during upgrade");
                return;
            }
            
            // Grant the new tier permission
            string newPermission = GetTierPermission(currentTier + 1);
            if (!string.IsNullOrEmpty(newPermission))
            {
                permission.GrantUserPermission(player.UserIDString, newPermission, this);
                
                // Update tier tracking data
                var playerData = GetPlayerStorage(player.userID);
                playerData.CurrentTierWipes = 0; // Reset tier-specific wipe counter
                playerData.TierUpgradeDate = DateTime.UtcNow;
                SaveData();
                
                SendReply(player, $"<color=#00ff00>Congratulations! You have upgraded to Tier {currentTier + 1}!</color>");
                
                var newSlots = GetTierSlots(currentTier + 1);
                var newScrapLimit = GetTierScrapLimit(currentTier + 1);
                var scrapLimitText = newScrapLimit == -1 ? "Unlimited" : $"{newScrapLimit:N0}";
                
                SendReply(player, $"<color=#00ff00>New limits: {newSlots} slots, {scrapLimitText} scrap capacity</color>");
                
                // If storage is currently open, refresh it
                if (playerActiveStorage.ContainsKey(player.userID))
                {
                    var storage = playerActiveStorage[player.userID] as StorageContainer;
                    if (storage != null && !storage.IsDestroyed)
                    {
                        // Update storage capacity
                        storage.inventory.capacity = newSlots;
                        storage.SendNetworkUpdate();
                        player.inventory.loot.SendImmediate();
                    }
                }
                
                // Refresh the UI to show the new tier
                CreateStorageTierUI(player);
                
                // Log the upgrade
                Puts($"[UPGRADE] {player.displayName} ({player.UserIDString}) upgraded from Tier {currentTier} to Tier {currentTier + 1}");
            }
        }
        
        private string GetTierPermission(int tier)
        {
            switch (tier)
            {
                case 2: return PERMISSION_TIER2;
                case 3: return PERMISSION_TIER3;
                case 4: return PERMISSION_TIER4;
                case 5: return PERMISSION_TIER5;
                default: return null;
            }
        }
        
        [ConsoleCommand("singularity.resetwipecounter")]
        private void CmdResetWipeCounter(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                // Allow server console to reset all counters
                if (arg.IsServerside || arg.IsRcon)
                {
                    foreach (var data in playerStorage.Values)
                    {
                        data.CurrentTierWipes = 0;
                        data.TierUpgradeDate = DateTime.UtcNow;
                    }
                    SaveData();
                    Puts("Reset all player wipe counters to 0");
                }
                return;
            }
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You need admin permission to use this command.");
                return;
            }
            
            var playerData = GetPlayerStorage(player.userID);
            playerData.CurrentTierWipes = 0;
            playerData.TierUpgradeDate = DateTime.UtcNow;
            SaveData();
            
            SendReply(player, $"<color=#00ff00>Your tier wipe counter has been reset to 0.</color>");
            
            // Refresh UI if storage is open
            if (playerActiveStorage.ContainsKey(player.userID))
            {
                DestroyStorageTierUI(player);
                CreateStorageTierUI(player);
            }
        }
        
        [ConsoleCommand("singularity.checkwipestatus")]
        private void CmdCheckWipeStatus(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You need admin permission to use this command.");
                return;
            }
            
            var playerData = GetPlayerStorage(player.userID);
            SendReply(player, $"<color=#ffff00>Wipe Status for {player.displayName}:</color>");
            SendReply(player, $"Current Tier: {playerData.StorageTier}");
            SendReply(player, $"Wipes at current tier: {playerData.CurrentTierWipes}");
            SendReply(player, $"Total wipes survived: {playerData.WipesSurvived}");
            SendReply(player, $"Last wipe date: {playerData.LastWipeDate}");
            SendReply(player, $"Tier upgrade date: {playerData.TierUpgradeDate}");
            SendReply(player, $"Will show warning: {(playerData.CurrentTierWipes > 0 ? "Yes" : "No")}");
            SendReply(player, $"Will downgrade on next wipe: {(playerData.CurrentTierWipes >= 1 ? "Yes" : "No")}");
            SendReply(player, $"<color=#ffaa00>Debug: Counter == {playerData.CurrentTierWipes}, > 0? {playerData.CurrentTierWipes > 0}</color>");
            
            // Check save file status
            var saveData = Interface.Oxide.DataFileSystem.ReadObject<SaveNameData>("SingularityStorage_LastSave");
            SendReply(player, $"Current save: {World.SaveFileName}");
            SendReply(player, $"Last known save: {saveData?.SaveName ?? "(none)"}");
        }
        
        [ChatCommand("singularityadmin")]
        private void CmdCloudAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PERMISSION_ADMIN))
            {
                SendReply(player, "You don't have permission to use this command.");
                return;
            }
            
            if (args.Length == 0)
            {
                SendReply(player, "<color=#ffff00>Singularity Storage Admin Commands:</color>");
                SendReply(player, "Usage: /singularityadmin <command> [args]");
                SendReply(player, "");
                SendReply(player, "<color=#00ff00>Terminal Management:</color>");
                SendReply(player, "  spawn [nosnap] - Deploy terminal at current position");
                SendReply(player, "  remove - Remove nearest terminal (within 10m)");
                SendReply(player, "  list - List all active terminals with positions");
                SendReply(player, "  savepos - Save terminal positions for auto-spawn");
                SendReply(player, "");
                SendReply(player, "<color=#00ff00>Data Management:</color>");
                SendReply(player, "  wipe <player> - Clear player's storage data");
                SendReply(player, "  stats [player] - View storage statistics");
                SendReply(player, "");
                SendReply(player, "<color=#ff0000>Danger Zone:</color>");
                SendReply(player, "  wipeterminals - Remove unsaved terminals");
                SendReply(player, "  wipeall - Remove ALL terminals and positions");
                return;
            }
            
            switch (args[0].ToLower())
            {
                case "spawn":
                    bool snapToGround = true;
                    if (args.Length > 1 && args[1].ToLower() == "nosnap")
                    {
                        snapToGround = false;
                        Puts("[DEBUG] NoSnap argument detected - snapToGround = false");
                    }
                    else
                    {
                        Puts("[DEBUG] No nosnap argument - snapToGround = true");
                    }
                    var terminal = SpawnTerminalAtPlayer(player, snapToGround);
                    if (terminal != null)
                    {
                        SendReply(player, $"<color=#00ff00>Singularity terminal deployed successfully!</color>");
                        SendReply(player, $"Location: {terminal.MonumentName}{(snapToGround ? "" : " (exact position)")}.");
                    }
                    break;
                    
                case "remove":
                    RemoveNearestTerminal(player);
                    break;
                    
                case "list":
                    ListTerminals(player);
                    break;
                    
                case "wipe":
                    if (args.Length > 1)
                    {
                        var target = FindPlayer(args[1]);
                        if (target != null)
                        {
                            if (playerStorage.ContainsKey(target.userID))
                            {
                                playerStorage.Remove(target.userID);
                                SaveData();
                                SendReply(player, $"<color=#ff0000>Wiped singularity storage for {target.displayName}</color>");
                                SendReply(player, $"Removed {playerStorage[target.userID].Items.Count} quantum items.");
                            }
                        }
                        else
                        {
                            SendReply(player, "Player not found.");
                        }
                    }
                    else
                    {
                        SendReply(player, "Usage: /singularityadmin wipe <player>");
                    }
                    break;
                    
                case "savepos":
                    SaveTerminalPositions(player);
                    break;
                    
                case "wipeterminals":
                    WipeUnsavedTerminals(player);
                    break;
                    
                case "wipeall":
                    WipeAllTerminals(player);
                    break;
                    
                case "stats":
                    ShowStorageStats(player, args.Length > 1 ? args[1] : null);
                    break;
                    
                default:
                    SendReply(player, "Unknown command. Type /singularityadmin for help.");
                    break;
            }
        }
        
        private BasePlayer FindPlayer(string nameOrId)
        {
            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (activePlayer.UserIDString == nameOrId)
                    return activePlayer;
                if (activePlayer.displayName.Contains(nameOrId, System.StringComparison.OrdinalIgnoreCase))
                    return activePlayer;
            }
            return null;
        }
        
        private StorageTerminal SpawnTerminalAtPlayer(BasePlayer player, bool snapToGround = true)
        {
            Puts($"[DEBUG] SpawnTerminalAtPlayer called with snapToGround = {snapToGround}");
            Puts($"[DEBUG] Player position: {player.transform.position}");
            
            // Calculate position in front of player
            var forward = player.eyes.HeadForward();
            forward.y = 0; // Keep it horizontal
            forward.Normalize();
            var position = player.transform.position + (forward * 2f);
            Puts($"[DEBUG] Initial spawn position (2m forward): {position}");
            
            // Keep the Y position at player's feet level to avoid spawning too high
            position.y = player.transform.position.y;
            Puts($"[DEBUG] Position after Y adjustment: {position}");
            
            // Find nearest monument
            var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
            MonumentInfo nearestMonument = null;
            float nearestDistance = float.MaxValue;
            
            foreach (var monument in monuments)
            {
                float distance = Vector3.Distance(position, monument.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestMonument = monument;
                }
            }
            
            // Calculate rotation facing the player
            var directionToPlayer = (player.transform.position - position).normalized;
            directionToPlayer.y = 0;
            var rotation = Quaternion.LookRotation(directionToPlayer).eulerAngles.y;
            
            // If we're near a monument, snap relative to monument's rotation
            if (nearestMonument != null && nearestDistance < 100f) // Within 100m of a monument
            {
                // Get monument's rotation
                float monumentRotation = nearestMonument.transform.eulerAngles.y;
                
                // Convert world rotation to monument-relative rotation
                float relativeRotation = rotation - monumentRotation;
                
                // Snap to cardinal relative to monument
                relativeRotation = SnapToCardinal(relativeRotation);
                
                // Convert back to world rotation
                rotation = relativeRotation + monumentRotation;
                
                // Normalize
                rotation = rotation % 360;
                if (rotation < 0) rotation += 360;
                
                SendReply(player, $"Monument rotation: {monumentRotation:F0}°, Terminal facing: {GetCardinalName(relativeRotation)} (relative to monument)");
            }
            else
            {
                // Not near a monument, use world cardinal directions
                rotation = SnapToCardinal(rotation);
                SendReply(player, $"Terminal facing: {GetCardinalName(rotation)} (world direction)");
            }
            
            string monumentName = nearestMonument != null ? GetMonumentName(nearestMonument) : "Custom";
            
            // Apply ground snapping if requested
            if (snapToGround)
            {
                Puts($"[DEBUG] Applying ground snapping from position: {position}");
                position = SnapToGround(position);
                Puts($"[DEBUG] Position after SnapToGround: {position}");
            }
            else
            {
                Puts($"[DEBUG] NoSnap - keeping position as is: {position}");
            }
            
            Puts($"[DEBUG] Final spawn position: {position}");
            SpawnTerminal(position, rotation, monumentName, nearestMonument);
            return activeTerminals.Values.LastOrDefault();
        }
        
        private void RemoveNearestTerminal(BasePlayer player)
        {
            var nearest = activeTerminals.Values
                .OrderBy(t => Vector3.Distance(t.Position, player.transform.position))
                .FirstOrDefault();
                
            if (nearest != null && Vector3.Distance(nearest.Position, player.transform.position) < 10f)
            {
                // Clean up display entity first
                if (nearest.DisplayEntity != null && !nearest.DisplayEntity.IsDestroyed)
                {
                    nearest.DisplayEntity.Kill();
                }
                
                // Also find and remove any orphaned picture frames nearby
                var nearbyFrames = new List<BaseEntity>();
                Vis.Entities(nearest.Position, 5f, nearbyFrames, LayerMask.GetMask("Deployed"));
                
                foreach (var entity in nearbyFrames)
                {
                    if (entity is Signage && entity.PrefabName.Contains("pictureframe"))
                    {
                        entity.Kill();
                    }
                }
                
                activeTerminals.Remove(nearest.Entity.net.ID.Value);
                nearest.Entity.Kill();
                SendReply(player, "<color=#ff0000>Terminal and display deactivated and removed.</color>");
            }
            else
            {
                SendReply(player, "<color=#ffff00>No terminal found within 10 meters.</color>");
            }
        }
        
        private void ListTerminals(BasePlayer player)
        {
            SendReply(player, $"<color=#00ff00>Active Singularity Terminals: {activeTerminals.Count}</color>");
            if (activeTerminals.Count == 0)
            {
                SendReply(player, "No terminals currently deployed.");
            }
            else
            {
                var terminalsByMonument = activeTerminals.Values
                    .GroupBy(t => t.MonumentName)
                    .OrderBy(g => g.Key);
                foreach (var group in terminalsByMonument)
                {
                    SendReply(player, $"<color=#ffff00>{group.Key}:</color>");
                    foreach (var terminal in group)
                    {
                        var distance = Vector3.Distance(player.transform.position, terminal.Position);
                        SendReply(player, $"  - Position: {terminal.Position} (Distance: {distance:F1}m)");
                    }
                }
            }
        }
        
        private void WipeUnsavedTerminals(BasePlayer player)
        {
            SendReply(player, "<color=#ffff00>Wiping all unsaved terminals...</color>");
            
            // First, destroy all active terminals and their display entities
            var terminalsToRemove = activeTerminals.Values.ToList();
            var removedTerminals = 0;
            var removedDisplays = 0;
            
            foreach (var terminal in terminalsToRemove)
            {
                // Clean up display entity first
                if (terminal?.DisplayEntity != null && !terminal.DisplayEntity.IsDestroyed)
                {
                    terminal.DisplayEntity.Kill();
                    removedDisplays++;
                }
                
                // Then clean up the terminal itself
                if (terminal?.Entity != null && !terminal.Entity.IsDestroyed)
                {
                    terminal.Entity.Kill();
                    removedTerminals++;
                }
            }
            
            // Also find and remove any orphaned picture frames near terminals
            foreach (var terminal in terminalsToRemove)
            {
                if (terminal?.Entity == null) continue;
                
                var terminalPos = terminal.Entity.transform.position;
                var nearbyFrames = new List<BaseEntity>();
                Vis.Entities(terminalPos, 5f, nearbyFrames, LayerMask.GetMask("Deployed"));
                
                foreach (var entity in nearbyFrames)
                {
                    if (entity is Signage && entity.PrefabName.Contains("pictureframe"))
                    {
                        entity.Kill();
                        removedDisplays++;
                    }
                }
            }
            
            activeTerminals.Clear();
            
            SendReply(player, $"<color=#ff0000>Removed {removedTerminals} terminals and {removedDisplays} picture frames.</color>");
            
            // Now respawn only the saved terminals
            if (config.TerminalLocations.Count > 0)
            {
                SendReply(player, "<color=#00ff00>Respawning saved terminals...</color>");
                timer.Once(1f, () => 
                {
                    SpawnAllTerminals();
                    SendReply(player, $"<color=#00ff00>Respawned {activeTerminals.Count} saved terminals.</color>");
                });
            }
            else
            {
                SendReply(player, "No saved terminal positions found.");
            }
        }
        
        private void WipeAllTerminals(BasePlayer player)
        {
            SendReply(player, "<color=#ff0000>WIPING ALL TERMINALS AND SAVED POSITIONS!</color>");
            
            // Destroy all active terminals and their display entities
            var terminalsToRemove = activeTerminals.Values.ToList();
            var removedTerminals = 0;
            var removedDisplays = 0;
            
            foreach (var terminal in terminalsToRemove)
            {
                // Clean up display entity first
                if (terminal?.DisplayEntity != null && !terminal.DisplayEntity.IsDestroyed)
                {
                    terminal.DisplayEntity.Kill();
                    removedDisplays++;
                }
                
                // Then clean up the terminal itself
                if (terminal?.Entity != null && !terminal.Entity.IsDestroyed)
                {
                    terminal.Entity.Kill();
                    removedTerminals++;
                }
            }
            
            // Also find and remove any orphaned picture frames near terminals
            foreach (var terminal in terminalsToRemove)
            {
                if (terminal?.Entity == null) continue;
                
                var terminalPos = terminal.Entity.transform.position;
                var nearbyFrames = new List<BaseEntity>();
                Vis.Entities(terminalPos, 5f, nearbyFrames, LayerMask.GetMask("Deployed"));
                
                foreach (var entity in nearbyFrames)
                {
                    if (entity is Signage && entity.PrefabName.Contains("pictureframe"))
                    {
                        entity.Kill();
                        removedDisplays++;
                    }
                }
            }
            
            activeTerminals.Clear();
            
            // Clear all saved positions
            config.TerminalLocations.Clear();
            SaveConfig();
            
            SendReply(player, $"<color=#ff0000>Removed {removedTerminals} terminals and {removedDisplays} picture frames.</color>");
            SendReply(player, "<color=#ff0000>Cleared all saved terminal positions.</color>");
            SendReply(player, "Use /singularityadmin spawn to create new terminals.");
        }
        
        private void SaveTerminalPositions(BasePlayer player)
        {
            var monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>();
            var savedCount = 0;
            var processedMonuments = new HashSet<string>();
            
            // Clear existing locations
            config.TerminalLocations.Clear();
            
            // Group terminals by their nearest monument to prevent duplicates
            var terminalsByMonument = new Dictionary<string, List<(StorageTerminal terminal, MonumentInfo monument, float distance)>>();
            
            foreach (var terminal in activeTerminals.Values)
            {
                // Find the nearest monument
                MonumentInfo nearestMonument = null;
                float nearestDistance = float.MaxValue;
                
                foreach (var monument in monuments)
                {
                    float distance = Vector3.Distance(terminal.Position, monument.transform.position);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearestMonument = monument;
                    }
                }
                
                if (nearestMonument != null)
                {
                    string monumentName = GetMonumentName(nearestMonument);
                    
                    if (!terminalsByMonument.ContainsKey(monumentName))
                    {
                        terminalsByMonument[monumentName] = new List<(StorageTerminal, MonumentInfo, float)>();
                    }
                    
                    terminalsByMonument[monumentName].Add((terminal, nearestMonument, nearestDistance));
                }
            }
            
            // Save only the closest terminal for each monument type
            foreach (var kvp in terminalsByMonument)
            {
                string monumentName = kvp.Key;
                var terminals = kvp.Value;
                
                // Sort by distance and take only the first (closest) terminal
                var closestTerminal = terminals.OrderBy(t => t.distance).First();
                
                if (terminals.Count > 1)
                {
                    SendReply(player, $"<color=#ffff00>Warning: Found {terminals.Count} terminals at {monumentName}, saving only the closest one.</color>");
                }
                
                // Calculate relative position (accounts for monument rotation and scale)
                Vector3 relativePos = closestTerminal.monument.transform.InverseTransformPoint(closestTerminal.terminal.Position);
                
                // Calculate relative rotation
                float worldRotation = closestTerminal.terminal.Entity.transform.eulerAngles.y;
                float monumentRotation = closestTerminal.monument.transform.eulerAngles.y;
                float relativeRotation = worldRotation - monumentRotation;
                
                // Normalize relative rotation
                relativeRotation = relativeRotation % 360;
                if (relativeRotation < 0) relativeRotation += 360;
                
                // Add to config
                if (!config.TerminalLocations.ContainsKey(monumentName))
                {
                    config.TerminalLocations[monumentName] = new List<TerminalLocation>();
                }
                
                config.TerminalLocations[monumentName].Add(new TerminalLocation
                {
                    Position = relativePos,
                    Rotation = relativeRotation
                });
                
                savedCount++;
                SendReply(player, $"Saved terminal at {monumentName}:");
                SendReply(player, $"  Relative position: {relativePos}");
                SendReply(player, $"  Relative rotation: {relativeRotation:F0}° (Monument at {monumentRotation:F0}°)");
            }
            
            SaveConfig();
            SendReply(player, $"<color=#00ff00>Saved {savedCount} terminal position(s) to config!</color>");
            SendReply(player, "These positions will be restored after wipes.");
        }
        
        private void ShowStorageStats(BasePlayer player, string targetName)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                // Show overall stats
                SendReply(player, "<color=#00ff00>Singularity Storage - Global Statistics</color>");
                SendReply(player, $"Total users: {playerStorage.Count}");
                
                int totalItems = 0;
                var topUsers = playerStorage
                    .OrderByDescending(kvp => kvp.Value.Items.Count)
                    .Take(5);
                    
                foreach (var kvp in playerStorage)
                {
                    totalItems += kvp.Value.Items.Count;
                }
                
                SendReply(player, $"Total quantum items stored: {totalItems}");
                SendReply(player, "");
                SendReply(player, "<color=#ffff00>Top 5 Users by Item Count:</color>");
                
                foreach (var kvp in topUsers)
                {
                    var playerName = covalence.Players.FindPlayerById(kvp.Key.ToString())?.Name ?? "Unknown";
                    SendReply(player, $"- {playerName}: {kvp.Value.Items.Count} items (last sync: {kvp.Value.LastAccessed:yyyy-MM-dd})");
                }
            }
            else
            {
                // Show specific player stats
                var target = FindPlayer(targetName);
                if (target == null)
                {
                    SendReply(player, "Player not found.");
                    return;
                }
                
                if (!playerStorage.ContainsKey(target.userID))
                {
                    SendReply(player, $"{target.displayName} has no stored items.");
                    return;
                }
                
                var storage = playerStorage[target.userID];
                var targetPlayer = BasePlayer.FindByID(target.userID);
                var targetTier = targetPlayer != null ? GetPlayerStorageTier(targetPlayer) : 1;
                var targetSlots = GetTierSlots(targetTier);
                SendReply(player, $"<color=#00ff00>Storage Stats for {target.displayName}:</color>");
                SendReply(player, $"Storage Tier: {targetTier} ({targetSlots} slots)");
                SendReply(player, $"Items stored: {storage.Items.Count}/{targetSlots}");
                SendReply(player, $"Storage usage: {(storage.Items.Count * 100.0 / targetSlots):F1}%");
                SendReply(player, $"Last accessed: {storage.LastAccessed:yyyy-MM-dd HH:mm} UTC");
                
                // Show item breakdown by category
                var itemCategories = new Dictionary<string, int>();
                foreach (var item in storage.Items)
                {
                    var itemDef = ItemManager.FindItemDefinition(item.ItemId);
                    if (itemDef != null)
                    {
                        string category = itemDef.category.ToString();
                        if (!itemCategories.ContainsKey(category))
                            itemCategories[category] = 0;
                        itemCategories[category] += item.Amount;
                    }
                }
                
                if (itemCategories.Count > 0)
                {
                    SendReply(player, "");
                    SendReply(player, "<color=#ffff00>Items by Category:</color>");
                    foreach (var kvp in itemCategories.OrderByDescending(k => k.Value))
                    {
                        SendReply(player, $"- {kvp.Key}: {kvp.Value} items");
                    }
                }
            }
        }
        
        #endregion
        
        #region Data Management
        
        private void LoadData()
        {
            try
            {
                playerStorage = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerStorageData>>("SingularityStorage_Data") ?? new Dictionary<ulong, PlayerStorageData>();
                
                // Migrate existing data - initialize new fields for old data
                foreach (var kvp in playerStorage)
                {
                    var data = kvp.Value;
                    
                    // Initialize FirstCreated if it's default
                    if (data.FirstCreated == DateTime.MinValue || data.FirstCreated == default(DateTime))
                    {
                        data.FirstCreated = DateTime.UtcNow;
                    }
                    
                    // Initialize TierUpgradeDate if it's default
                    if (data.TierUpgradeDate == DateTime.MinValue || data.TierUpgradeDate == default(DateTime))
                    {
                        data.TierUpgradeDate = DateTime.UtcNow;
                    }
                    
                    // CurrentTierWipes should start at 0 for existing players
                    // It will only increment when a wipe is detected
                    // No need to change it here as it defaults to 0
                }
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load data: {ex.Message}");
                playerStorage = new Dictionary<ulong, PlayerStorageData>();
            }
        }
        
        private void SaveData()
        {
            try
            {
                Puts($"[DEBUG SaveData] Saving data for {playerStorage.Count} players");
                foreach (var kvp in playerStorage.Take(3)) // Show first 3 for debugging
                {
                    Puts($"[DEBUG SaveData] Player {kvp.Key}: CurrentTierWipes={kvp.Value.CurrentTierWipes}, Tier={kvp.Value.StorageTier}");
                }
                
                Interface.Oxide.DataFileSystem.WriteObject("SingularityStorage_Data", playerStorage);
                Puts($"[DEBUG SaveData] Data saved successfully");
            }
            catch (Exception ex)
            {
                PrintError($"Failed to save data: {ex.Message}");
            }
        }
        
        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception();
                SaveConfig();
            }
            catch
            {
                PrintWarning("Failed to load config, using default values");
                config = new Configuration();
                SaveConfig();
            }
        }
        
        protected override void SaveConfig()
        {
            Config.WriteObject(config);
        }
        
        protected override void LoadDefaultConfig()
        {
            config = new Configuration();
        }
        
        #endregion
        
        #region UI Management
        
        private void CleanupLeftoverUI()
        {
            // Clean up any leftover UI elements from previous plugin sessions
            // This handles cases where the server crashed or plugin was improperly unloaded
            int cleanedPlayers = 0;
            
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                
                // Directly destroy known UI element names
                // These are the hardcoded panel names we use
                CuiHelper.DestroyUi(player, "SingularityStorageTierPanel");
                CuiHelper.DestroyUi(player, "SingularityStorageTierPanel_upgrade");
                cleanedPlayers++;
            }
            
            if (cleanedPlayers > 0)
            {
                Puts($"[CLEANUP] Cleaned up leftover UI for {cleanedPlayers} players");
            }
        }
        
        private void CreateStorageTierUI(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            
            // Remove any existing UI first
            DestroyStorageTierUI(player);
            
            var tier = GetPlayerStorageTier(player);
            var slots = GetTierSlots(tier);
            var scrapLimit = GetTierScrapLimit(tier);
            var currentScrap = GetPlayerScrapInStorage(player.userID);
            var playerData = GetPlayerStorage(player.userID);
            
            // Build tier information text with current tier wipe info
            string wipeInfo = "";
            if (tier > 1)
            {
                // Show current tier wipes and when downgrade will happen
                if (playerData.CurrentTierWipes == 0)
                    wipeInfo = " | Fresh Tier";
                else if (playerData.CurrentTierWipes == 1)
                    wipeInfo = " | 1 Wipe (Pay upkeep or downgrade next wipe!)";
                else
                    wipeInfo = $" | {playerData.CurrentTierWipes} Wipes at Tier {tier}";
            }
            string tierText = $"Singularity Storage - Tier {tier}{wipeInfo}";
            
            string tierWipeInfo = playerData.CurrentTierWipes > 0
                ? $" ({playerData.CurrentTierWipes} wipes at current tier)"
                : " (New Tier)";
            
            string slotsText = scrapLimit == -1 
                ? $"Scrap Capacity: Unlimited{tierWipeInfo}" 
                : $"Scrap Capacity: {scrapLimit:N0}{tierWipeInfo}";
            string scrapText = scrapLimit == -1 
                ? "Scrap Storage: Unlimited" 
                : $"Scrap Storage: {currentScrap:N0}/{scrapLimit:N0}";
            
            // Get tier restrictions info
            string restrictionsText = GetTierRestrictionsText(tier);
            
            var elements = new CuiElementContainer();
            string panelName = "SingularityStorageTierPanel";
            
            // Main panel background
            elements.Add(new CuiPanel
            {
                Image =
                {
                    Color = "0 0 0 0.85",
                    Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                },
                RectTransform =
                {
                    AnchorMin = "0.35 0.72",
                    AnchorMax = "0.65 0.82"
                },
                CursorEnabled = false
            }, "Overlay", panelName);
            
            // Tier title
            elements.Add(new CuiLabel
            {
                Text =
                {
                    Text = tierText,
                    FontSize = 22,
                    Align = TextAnchor.MiddleCenter,
                    Color = GetTierColor(tier)
                },
                RectTransform =
                {
                    AnchorMin = "0 0.65",
                    AnchorMax = "1 1"
                }
            }, panelName);
            
            // Slots info
            elements.Add(new CuiLabel
            {
                Text =
                {
                    Text = slotsText,
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.8 0.8 0.8 1"
                },
                RectTransform =
                {
                    AnchorMin = "0 0.35",
                    AnchorMax = "1 0.65"
                }
            }, panelName);
            
            // Scrap limit info
            elements.Add(new CuiLabel
            {
                Text =
                {
                    Text = scrapText,
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = currentScrap >= scrapLimit && scrapLimit != -1 ? "1 0.3 0.3 1" : "0.8 0.8 0.8 1"
                },
                RectTransform =
                {
                    AnchorMin = "0 0.15",
                    AnchorMax = "1 0.35"
                }
            }, panelName);
            
            // Restrictions text
            elements.Add(new CuiLabel
            {
                Text =
                {
                    Text = restrictionsText,
                    FontSize = 12,
                    Align = TextAnchor.MiddleCenter,
                    Color = "0.7 0.7 0.7 1"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 0.15"
                }
            }, panelName);
            
            // Add decorative border
            elements.Add(new CuiPanel
            {
                Image =
                {
                    Color = GetTierColor(tier),
                    Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 0.02"
                }
            }, panelName);
            
            elements.Add(new CuiPanel
            {
                Image =
                {
                    Color = GetTierColor(tier),
                    Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                },
                RectTransform =
                {
                    AnchorMin = "0 0.98",
                    AnchorMax = "1 1"
                }
            }, panelName);
            
            // Add upgrade button if player can upgrade
            if (tier < 5)
            {
                var upgradeCost = GetTierUpgradeCost(tier);
                var playerScrap = GetPlayerInventoryScrap(player);
                
                if (playerScrap >= upgradeCost)
                {
                    // Add upgrade button panel
                    elements.Add(new CuiPanel
                    {
                        Image =
                        {
                            Color = "0.2 0.8 0.2 0.9",
                            Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.68 0.85",
                            AnchorMax = "0.92 0.95"
                        },
                        CursorEnabled = true
                    }, "Overlay", panelName + "_upgrade");
                    
                    // Add upgrade button
                    elements.Add(new CuiButton
                    {
                        Button =
                        {
                            Command = $"singularity.upgrade",
                            Color = "0 0 0 0"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1"
                        },
                        Text =
                        {
                            Text = $"UPGRADE TO TIER {tier + 1}\nCost: {upgradeCost:N0} scrap",
                            FontSize = 16,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1"
                        }
                    }, panelName + "_upgrade");
                    
                    // Add glow effect border
                    elements.Add(new CuiPanel
                    {
                        Image =
                        {
                            Color = "0.4 1 0.4 0.5"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 0.03"
                        }
                    }, panelName + "_upgrade");
                    
                    elements.Add(new CuiPanel
                    {
                        Image =
                        {
                            Color = "0.4 1 0.4 0.5"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0.97",
                            AnchorMax = "1 1"
                        }
                    }, panelName + "_upgrade");
                }
            }
            
            // Add upkeep button or warning if needed
            // Only show warning if we've actually survived at least 1 wipe at this tier
            Puts($"[DEBUG UI] Player {player.displayName}: Tier={tier}, CurrentTierWipes={playerData.CurrentTierWipes}, Showing warning? {tier > 1 && playerData.CurrentTierWipes >= 1}");
            
            // Show warning after 1 wipe (counter >= 1)
            if (tier > 1 && playerData.CurrentTierWipes >= 1)
            {
                var upkeepCost = GetTierUpkeepCost(tier);
                var playerScrap = GetPlayerInventoryScrap(player);
                
                Puts($"[DEBUG UI] SHOWING WARNING for {player.displayName} - Counter is {playerData.CurrentTierWipes}");
                
                // Show warning panel
                elements.Add(new CuiPanel
                {
                    Image =
                    {
                        Color = playerData.CurrentTierWipes >= 1 ? "0.8 0.2 0.2 0.9" : "0.8 0.8 0.2 0.9",
                        Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0.08 0.85",
                        AnchorMax = "0.32 0.95"
                    },
                    CursorEnabled = false
                }, "Overlay", panelName + "_warning");
                
                // Warning text
                elements.Add(new CuiLabel
                {
                    Text =
                    {
                        Text = "⚠️ TIER UPKEEP DUE!\nWill downgrade next wipe!",
                        FontSize = 12,
                        Align = TextAnchor.MiddleCenter,
                        Color = "1 1 1 1"
                    },
                    RectTransform =
                    {
                        AnchorMin = "0 0",
                        AnchorMax = "1 1"
                    }
                }, panelName + "_warning");
                
                // Add upkeep payment button if player has enough scrap
                if (playerScrap >= upkeepCost)
                {
                    elements.Add(new CuiPanel
                    {
                        Image =
                        {
                            Color = "0.8 0.6 0.2 0.9",
                            Material = "assets/content/ui/uibackgroundblur-ingamemenu.mat"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0.35 0.85",
                            AnchorMax = "0.65 0.95"
                        },
                        CursorEnabled = true
                    }, "Overlay", panelName + "_upkeep");
                    
                    elements.Add(new CuiButton
                    {
                        Button =
                        {
                            Command = "singularity.upkeep",
                            Color = "0 0 0 0"
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1"
                        },
                        Text =
                        {
                            Text = $"PAY UPKEEP\n{upkeepCost:N0} scrap",
                            FontSize = 14,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1"
                        }
                    }, panelName + "_upkeep");
                }
            }
            
            CuiHelper.AddUi(player, elements);
            playerStorageUI[player.userID] = panelName;
        }
        
        private void DestroyStorageTierUI(BasePlayer player)
        {
            if (player == null) return;
            
            if (playerStorageUI.ContainsKey(player.userID))
            {
                // Destroy main panel
                CuiHelper.DestroyUi(player, playerStorageUI[player.userID]);
                // Destroy upgrade button panel if it exists
                CuiHelper.DestroyUi(player, playerStorageUI[player.userID] + "_upgrade");
                // Destroy warning panel if it exists
                CuiHelper.DestroyUi(player, playerStorageUI[player.userID] + "_warning");
                // Destroy upkeep button panel if it exists
                CuiHelper.DestroyUi(player, playerStorageUI[player.userID] + "_upkeep");
                playerStorageUI.Remove(player.userID);
            }
        }
        
        private string GetTierColor(int tier)
        {
            switch (tier)
            {
                case 1: return "0.6 0.6 0.6 1"; // Gray
                case 2: return "0.3 0.8 0.3 1"; // Green
                case 3: return "0.3 0.5 0.9 1"; // Blue
                case 4: return "0.7 0.3 0.9 1"; // Purple
                case 5: return "0.9 0.7 0.2 1"; // Gold
                default: return "0.6 0.6 0.6 1";
            }
        }
        
        private string GetTierLimitsText(int tier)
        {
            var scrapLimit = GetTierScrapLimit(tier);
            var slots = GetTierSlots(tier);
            
            if (scrapLimit == -1)
            {
                return $"{slots} slots, Unlimited scrap";
            }
            else
            {
                return $"{slots} slots, {scrapLimit:N0} scrap max";
            }
        }
        
        private string GetTierRestrictionsText(int tier)
        {
            var restrictions = new List<string>();
            
            // Add slot capacity info
            var slots = GetTierSlots(tier);
            restrictions.Add($"{slots} Storage Slots");
            
            if (config.OnlyAllowResourcesAndComponents)
            {
                restrictions.Add("Resources & Components Only");
            }
            
            if (!config.AllowBlacklistedItems && config.BlacklistedItems.Count > 0)
            {
                restrictions.Add("No Explosives");
            }
            
            if (tier < 5)
            {
                restrictions.Add($"Upgrade to Tier {tier + 1}");
            }
            
            return string.Join(" | ", restrictions);
        }
        
        #endregion
    }
}