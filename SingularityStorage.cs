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
    [Info("SingularityStorage", "YourServer", "4.6.2")]
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
            public ulong TerminalSkinId { get; set; } = 1751033540; // Red vending machine
            
            [JsonProperty("Terminal Face Texture URL")]
            public string TerminalFaceTextureUrl { get; set; } = "https://vrogojin.github.io/singularity_storage/singularity_terminal_sign.png";
            
            [JsonProperty("Use Custom Face Texture")]
            public bool UseCustomFaceTexture { get; set; } = true;
            
            [JsonProperty("Only Allow Resources And Components")]
            public bool OnlyAllowResourcesAndComponents { get; set; } = true;
        }
        
        private class TerminalLocation
        {
            public Vector3 Position { get; set; }
            public float Rotation { get; set; }
        }
        
        #endregion
        
        #region Data
        
        private class PlayerStorageData
        {
            public ulong PlayerId { get; set; }
            public List<ItemData> Items { get; set; } = new List<ItemData>();
            public DateTime LastAccessed { get; set; }
            public int StorageTier { get; set; } = 1; // Default tier 1
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
            
            // Start periodic texture check - check every 30 seconds for texture integrity
            timer.Every(30f, () => CheckAndLoadNearbyTextures());
            
            // Schedule first periodic redraw
            SchedulePeriodicRedraw();
        }
        
        // Simple approach - just restore texture whenever it changes
        private void OnSignUpdated(Signage sign, BasePlayer player)
        {
            if (sign == null) return;
            
            // Check if this sign belongs to a terminal
            foreach (var terminal in activeTerminals.Values)
            {
                if (terminal?.DisplayEntity == sign)
                {
                    // Immediately restore the texture without any delay
                    ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(sign, config.TerminalFaceTextureUrl));
                }
            }
        }
        
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
            
            // Also find and remove any orphaned picture frames near terminals
            Puts($"[DEBUG] Searching for orphaned picture frames near terminals");
            
            // Search near each terminal position for any picture frames
            foreach (var terminal in activeTerminals.Values)
            {
                if (terminal?.Entity == null) continue;
                
                var terminalPos = terminal.Entity.transform.position;
                var nearbyFrames = new List<BaseEntity>();
                Vis.Entities(terminalPos, 5f, nearbyFrames, LayerMask.GetMask("Deployed"));
                
                Puts($"[DEBUG] Found {nearbyFrames.Count} deployed entities near terminal at {terminalPos}");
                
                foreach (var entity in nearbyFrames)
                {
                    if (entity is Signage && entity.PrefabName.Contains("pictureframe"))
                    {
                        Puts($"[DEBUG] Found and removing orphaned picture frame at {entity.transform.position}");
                        entity.Kill();
                        removedDisplays++;
                    }
                }
            }
            
            Puts($"[DEBUG] Removed {removedTerminals} terminals and {removedDisplays} display entities");
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
            
            // Spawn picture frame with custom texture if enabled
            if (config.UseCustomFaceTexture && ImageLibrary != null)
            {
                timer.Once(0.5f, () => SpawnTerminalDisplay(entity, rotation));
            }
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
        
        private void SpawnTerminalDisplay(BaseEntity terminal, float rotation)
        {
            if (terminal == null || !terminal.IsValid()) return;
            
            var position = terminal.transform.position;
            var forward = Quaternion.Euler(0, rotation, 0) * Vector3.forward;
            var backward = -forward;
            
            // First calculate where the frame would be on the vending machine
            var defaultFramePosition = position + forward * 0.55f + Vector3.up * 2.2f;
            
            // Check for a wall behind the frame position
            RaycastHit wallHit;
            var rayStart = defaultFramePosition; // Start from where the frame would be
            var wallFound = Physics.Raycast(rayStart, backward, out wallHit, 3f, LayerMask.GetMask("Construction", "Deployed", "World", "Terrain"));
            
            // Debug raycast
            Puts($"[DEBUG] Raycast from {rayStart} in direction {backward}, hit: {wallFound}");
            
            Vector3 framePosition;
            float frameRotation = rotation;
            
            if (wallFound)
            {
                // Attach to the wall
                framePosition = wallHit.point + wallHit.normal * 0.05f; // Slightly off the wall
                
                // Calculate rotation to face away from the wall
                var wallNormal = wallHit.normal;
                frameRotation = Quaternion.LookRotation(wallNormal).eulerAngles.y;
                
                Puts($"[DEBUG] Wall found at {wallHit.point}, distance: {wallHit.distance}, normal: {wallHit.normal}, attaching sign to wall");
            }
            else
            {
                // No wall found, position on the vending machine as before
                framePosition = defaultFramePosition;
                Puts($"[DEBUG] No wall found, positioning sign on vending machine");
            }
            
            var frameEntity = GameManager.server.CreateEntity(PICTURE_FRAME_PREFAB, framePosition, Quaternion.Euler(0, frameRotation, 0));
            if (frameEntity == null) 
            {
                Puts($"[DEBUG] Failed to create picture frame entity");
                return;
            }
            
            frameEntity.enableSaving = false;
            frameEntity.Spawn();
            
            // Store reference to display entity immediately
            if (activeTerminals.TryGetValue(terminal.net.ID.Value, out var terminalData))
            {
                terminalData.DisplayEntity = frameEntity;
            }
            
            // Wait for the entity to fully spawn before applying texture
            timer.Once(1f, () =>
            {
                if (frameEntity == null || frameEntity.IsDestroyed) return;
                
                // Make the sign completely non-editable
                var sign = frameEntity as Signage;
                if (sign != null)
                {
                    // Just lock the frame
                    sign.SetFlag(BaseEntity.Flags.Locked, true);
                    sign.SendNetworkUpdate();
                }
                
                // Apply the custom texture using SignArtist API
                var url = config.TerminalFaceTextureUrl;
                Puts($"[DEBUG] Attempting to paint image from URL: {url}");
                
                // First ensure the image is loaded in ImageLibrary
                if (ImageLibrary != null)
                {
                    // Force ImageLibrary to download and store the image
                    ImageLibrary.Call("AddImage", url, TERMINAL_FACE_IMAGE, (ulong)0);
                    
                    // Try direct image loading approach
                    timer.Once(2f, () =>
                    {
                        if (frameEntity == null || frameEntity.IsDestroyed) return;
                        
                        // Start coroutine to download and apply image
                        ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(frameEntity, url));
                    });
                }
                
                Puts($"[DEBUG] Spawned picture frame at {framePosition}");
            });
        }
        
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
        
        private IEnumerator DownloadAndApplyImage(BaseEntity signEntity, string url)
        {
            Puts($"[DEBUG] Starting image download from: {url}");
            
            using (var www = new WWW(url))
            {
                yield return www;
                
                if (!string.IsNullOrEmpty(www.error))
                {
                    Puts($"[ERROR] Failed to download image: {www.error}");
                    yield break;
                }
                
                if (www.bytes == null || www.bytes.Length == 0)
                {
                    Puts($"[ERROR] Downloaded image is empty");
                    yield break;
                }
                
                Puts($"[DEBUG] Downloaded image, size: {www.bytes.Length} bytes");
                
                // Apply image to sign
                var sign = signEntity as Signage;
                if (sign != null && !sign.IsDestroyed)
                {
                    try
                    {
                        // Store the image data
                        var textureId = FileStorage.server.Store(www.bytes, FileStorage.Type.png, sign.net.ID);
                        
                        // Apply texture to sign - picture frames have only one texture slot
                        if (sign.textureIDs != null && sign.textureIDs.Length > 0)
                        {
                            sign.textureIDs[0] = textureId;
                            sign.SetFlag(BaseEntity.Flags.Locked, true);
                            sign.SendNetworkUpdate();
                            
                            // Store the correct texture ID for this terminal sign
                            terminalTextureIds[sign.net.ID.Value] = textureId;
                            
                            Puts($"[DEBUG] Applied texture ID {textureId} to sign entity {sign.net.ID}");
                        }
                        else
                        {
                            Puts($"[ERROR] Sign has no texture slots available");
                        }
                    }
                    catch (Exception ex)
                    {
                        Puts($"[ERROR] Failed to apply texture: {ex.Message}");
                    }
                }
            }
        }
        
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
                    // Always restore immediately if texture was changed
                    if (forceRestore || needsTexture)
                    {
                        ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(sign, config.TerminalFaceTextureUrl));
                    }
                }
            }
        }
        
        private void SchedulePeriodicRedraw()
        {
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
                
                // Force redraw the texture
                ServerMgr.Instance.StartCoroutine(DownloadAndApplyImage(sign, config.TerminalFaceTextureUrl));
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
                    LastAccessed = DateTime.UtcNow
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
                
                OpenCloudStorage(player, activeTerminals[machine.net.ID.Value]);
                return false;
            }
            
            return null;
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
                Interface.Oxide.DataFileSystem.WriteObject("SingularityStorage_Data", playerStorage);
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
            
            // Build tier information text
            string tierText = $"Singularity Storage - Tier {tier}";
            string slotsText = scrapLimit == -1 
                ? "Scrap Capacity: Unlimited" 
                : $"Scrap Capacity: {scrapLimit:N0}";
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
                    AnchorMin = "0.35 0.88",
                    AnchorMax = "0.65 0.98"
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
            
            CuiHelper.AddUi(player, elements);
            playerStorageUI[player.userID] = panelName;
        }
        
        private void DestroyStorageTierUI(BasePlayer player)
        {
            if (player == null) return;
            
            if (playerStorageUI.ContainsKey(player.userID))
            {
                CuiHelper.DestroyUi(player, playerStorageUI[player.userID]);
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