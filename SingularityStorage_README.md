# Singularity Storage

A revolutionary quantum storage system for Rust that transcends server wipes, allowing players to preserve their items across time and space.

## Features

- **Persistent Storage**: Items stored in the Singularity persist through server wipes and map changes
- **Quantum Terminals**: Access your items from terminals strategically placed at major monuments
- **48 Storage Slots**: Generous storage capacity for your most valuable items
- **Smart Blacklisting**: Configurable item restrictions to maintain server balance
- **Advanced Administration**: Comprehensive admin tools for terminal and data management

## Player Commands

### `/singularity`
Base command that shows available options and system information.

### `/singularity info`
Display your personal storage metrics:
- Number of quantum items stored
- Storage capacity percentage
- Last synchronization time

### `/singularity terminals`
List all active Singularity terminals grouped by monument location.

### `/singularity help`
Show detailed help information including:
- How to use the system
- Current storage capacity
- Item restrictions (if any)

## Admin Commands

### `/singularityadmin`
Display comprehensive admin command help menu.

### Terminal Management

#### `/singularityadmin spawn [nosnap]`
Deploy a new terminal at your current position.
- Add `nosnap` to place at exact position without ground snapping
- Terminal automatically faces the player
- Snaps to cardinal directions relative to nearest monument

#### `/singularityadmin remove`
Remove the nearest terminal within 10 meters.

#### `/singularityadmin list`
List all active terminals with:
- Monument locations
- World positions
- Distance from your current position

#### `/singularityadmin savepos`
Save current terminal positions for automatic respawn after wipes.
- Positions are saved relative to monuments
- Only the closest terminal to each monument type is saved
- Prevents duplicate terminals at the same monument

### Data Management

#### `/singularityadmin wipe <player>`
Clear a specific player's storage data.
- Shows number of items removed
- Irreversible action

#### `/singularityadmin stats [player]`
View storage statistics:
- Without player name: Shows global statistics and top 5 users
- With player name: Shows detailed stats for that player including item categories

### Danger Zone Commands

#### `/singularityadmin wipeterminals`
Remove all terminals that aren't in saved positions and respawn saved ones.

#### `/singularityadmin wipeall`
**WARNING**: Removes ALL terminals and clears all saved positions.

## Configuration

The plugin creates a configuration file at `oxide/config/SingularityStorage.json`:

```json
{
  "Storage Slots": 48,
  "Allow Blacklisted Items": false,
  "Blacklisted Items (shortnames)": [
    "explosive.timed",
    "explosive.satchel",
    "ammo.rocket.basic",
    "ammo.rocket.hv",
    "ammo.rocket.fire",
    "explosive.c4"
  ],
  "Terminal Spawn Locations": {},
  "Terminal Interaction Distance": 3.0,
  "Auto-spawn Terminals": true,
  "Terminal Display Name": "Singularity Storage Terminal",
  "Terminal Skin ID": 1491990387
}
```

### Configuration Options

- **Storage Slots**: Number of inventory slots (default: 48)
- **Allow Blacklisted Items**: Whether to allow storing of blacklisted items
- **Blacklisted Items**: List of item shortnames that cannot be stored
- **Terminal Spawn Locations**: Saved terminal positions (managed via commands)
- **Terminal Interaction Distance**: How close players must be to use terminals
- **Auto-spawn Terminals**: Whether to spawn saved terminals on server start
- **Terminal Display Name**: Name shown on terminals
- **Terminal Skin ID**: Visual skin for the terminal (vending machine skin)

## Permissions

- `singularitystorage.use` - Allows players to use Singularity Storage
- `singularitystorage.admin` - Allows access to admin commands

## Terminal Placement Tips

1. **Monument Placement**: Terminals work best when placed near monument centers
2. **Indoor Placement**: Use `/singularityadmin spawn nosnap` for precise indoor placement
3. **Save Positions**: Always use `/singularityadmin savepos` after placing terminals
4. **Multiple Monuments**: The system automatically spawns terminals at all instances of each monument type

## Data Storage

- Player data is stored in `oxide/data/SingularityStorage_Data.json`
- This file persists through wipes and should be backed up regularly
- Terminal positions are stored in the main configuration file

## Troubleshooting

1. **Terminals not spawning**: Ensure you have saved positions using `/singularityadmin savepos`
2. **Can't access terminal**: Check that you have the `singularitystorage.use` permission
3. **Items disappearing**: Check the blacklist configuration - some items may be restricted
4. **Terminal at wrong height**: Use `/singularityadmin spawn nosnap` for manual height control

## Version History

- **3.0.0**: Complete rebranding to Singularity Storage with enhanced commands and features
- **2.0.7**: Previous version as RustyCloud