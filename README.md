# The Lucky Spriggull

A coin slot machine for **A Township Tale**, by **Epic**. MelonLoader 0.7 (Mono).

## Getting one

The machine is a fixed structure. An admin spawns it using:

```
spawn <player> 21251
```

## How to play

1. **Put coins in:**
   - Drop coins into the slot.
   - Or hold a coin pouch over it, and it empties in.
   - Changed your mind? Reach in and grab coins back out before you pull the lever.
2. **Pull the lever:** grab the gold ball and pull it down toward you.
3. **The reels spin** and stop on the result.
4. **Collect your winnings:** a win plays fireworks, flashing lights and the quest-complete jingle. Then the coins drop into the Winnings bubble on the tray. Nothing is ever dropped on the ground, and the tray only lets coins be taken out.

### Prizes (default odds)

| Three of | Chance | Pays |
|---|---|---|
| **Spriggull** (jackpot) | 1% | 20 × bet |
| Phantom | 3% | 5 × bet |
| Wyrm | 6% | 3 × bet |
| Gotera | 20% | 2 × bet |

The machine pays out about 93% of what goes in, on average.

### Bonus rows

A bonus row also hands your bet back.

| Three of | Chance | Bonus |
|---|---|---|
| Schmeechee | 2% | Full heal |
| Babu | 2% | Full hunger |
| Dais | 2% | 2× speed for 5 minutes |
| Firebug | 2% | Glow in the dark for 5 minutes |
| Turabada | 2% | 5× damage and damage protection for 8 minutes |

Bonus chances come out of the losing spins, so they don't change the prize odds.

## Install

**Everyone needs it:** the server *and* every player.

1. Get `TheLuckySpriggull.dll` from the [latest release](../../releases/latest).
2. Put it in the `Mods` folder of the server and of each player's game.

The server does the betting, spinning and paying. Each game animates the reels, lights and fireworks for itself.

## Settings

Settings are on the server, in `UserData/MelonPreferences.cfg` under `[LuckySpriggull]`. Only edit them while the server is closed: the server rewrites this file when it shuts down.

| Key | Default | Meaning |
|---|---|---|
| `Payouts` | `2x:20, 3x:6, 5x:3, 20x:1` | `multiplier:percent` for each prize. Whatever's left over is a loss. |
| `Bonuses` | `Schmeechee:2, Babu:2, Dais:2, Firebug:2, Turabada:2` | `creature:percent` for each bonus row. |
| `MaxBet` | `0` | The most coins one spin can take. 0 means no limit. |
| `AutoSpinSeconds` | `0` | 0 means only the lever spins. Above 0, it also spins automatically this many seconds after the last coin goes in. |
| `DebugButton` | `false` | Logs where hands are near the lever, for tuning. |

**Testing a result:** set its chance to 100 (for example `Payouts = "20x:100"` or `Bonuses = "Turabada:100"`), and every spin will land on it.

Per-player coins in and out are kept in `UserData/LuckySpriggull/stats.txt`.

## Building from source

The project references the game's own DLLs, so it expects to sit inside the game folder:

```
<A Township Tale>/ModSource/TheLuckySpriggull/
```

Build it:

```powershell
dotnet build -c Release
```

## How it works

| File | What it does |
|---|---|
| `SlotMachine.cs` | Builds and registers the structure. Runs the lever, reels, lights, fireworks and labels. |
| `SlotDocks.cs` | Copies the game's docks onto the machine: the coin slot (from the market stall), the Winnings bubble (from the ATM), and a hidden "result signal" dock. The server writes each spin's result there, so every game's reels stop on the same creatures before anything is paid. |
| `SlotIcons.cs` | Turns the creatures' models into reel symbols. |
| `SlotSign.cs` | The glowing sign. |
| `SlotGlyphs.cs` | The game's book drawings used on the paytable and as engravings. |
| `Bonus.cs` | The bonus-row effects. They use the game's own effects (heal, hunger, speed, damage, damage protection, glow). |
| `Core.cs` | Settings, bets, coins, spins and payouts. |
| `SpawnPlacement.cs` | Puts a spawned machine on the ground, facing the player. |
| `WinSound.cs` | Win and firework sounds. |

The prefab ID `21251` is fixed. Don't change it, or machines already placed in a world will break.
