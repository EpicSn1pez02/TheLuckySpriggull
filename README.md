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

By default a bonus row gives you a boost instead of coins (the bet is spent). Server owners can also pay coins on bonus rows, change each boost's strength and length, or turn the boosts off (see Settings).

| Three of | Chance | Bonus (default settings) |
|---|---|---|
| Schmeechee | 2% | Full heal |
| Babu | 2% | Full hunger |
| Dais | 2% | 2× speed for 5 minutes |
| Glowing Mushroom | 2% | Glow in the dark for 5 minutes |
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
| `Payouts` | `2x:20, 3x:6, 5x:3, 20x:1` | The four prizes: `multiplier:percent` (multipliers 1-99). The biggest is three Spriggulls, then Phantoms, Wyrms and Goteras. Whatever's left over is a loss. Every player's machine shows the server's amounts on its paytable. |
| `Bonuses` | `Schmeechee:2, Babu:2, Dais:2, Mushroom:2, Turabada:2` | `creature:percent` for each bonus row (`Mushroom` is the Glowing Mushroom). Add `:Nx` to pay that creature N × the bet, for example `Babu:2:1x`. |
| `BonusPayout` | `0` | Coins every bonus row pays, as a multiple of the bet: 0 = none, 1 = the bet back, 2 = double. A creature's own `:Nx` overrides it. |
| `BonusEffects` | `true` | `false` turns the boosts off, so bonus rows only pay `BonusPayout`. For "money back only", use `BonusEffects = false` with `BonusPayout = 1`. |
| `HealPercent` | `100` | Schmeechee: health given back, as a percent of full health. |
| `HungerPercent` | `100` | Babu: how much of the hunger bar it fills, as a percent. |
| `SpeedBoost` / `SpeedMinutes` | `2` / `5` | Dais: the speed multiplier (the game caps speed at 2×), and how long it lasts. |
| `GlowStrength` / `GlowMinutes` | `1` / `5` | Glowing Mushroom: how bright the glow is (1 = the game's normal glow), and how long it lasts. |
| `DamageBoost` / `DamageProtection` / `DamageMinutes` | `5` / `2` / `8` | Turabada: the damage multiplier, the damage-protection multiplier (0 = none), and how long both last. |
| `NearMissChance` | `25` | Percent of losing spins shown as a near miss: two big-prize symbols, then the third reel creeps onto the win line and rolls back off it. It's still a loss; 0 turns it off. |
| `MaxBet` | `0` | The most coins one spin can take. 0 means no limit. |
| `AutoSpinSeconds` | `0` | 0 means only the lever spins. Above 0, it also spins automatically this many seconds after the last coin goes in. |
| `DebugButton` | `false` | Logs where hands are near the lever, for tuning. |

The game may cap how high a stat like speed or damage can go. If it does, the server log says what the player actually got.

**Testing a result:** set its chance to 100 (for example `Payouts = "20x:100"` or `Bonuses = "Turabada:100"`), and every spin will land on it.

## Casino ledger

The server keeps the books in `UserData/LuckySpriggull/Ledger/` (times are the server's local time). Every file opens in Excel or Google Sheets:

| File | What's in it |
|---|---|
| `ledger-YYYY-MM.csv` | One row per event, as it happens: coins inserted, coins taken back, each spin (bet, result, payout), and winnings picked up. A new file starts each month. |
| `players.csv` | Each player's running totals: spins, wins, losses, bonus rows, jackpots, total bet, total won, net, return %, biggest win, and coins inserted, taken back and cashed out. Kept across restarts. |
| `machines.csv` | Each machine's totals: spins, coins bet, coins paid out, house profit, actual return against the expected return, and a verdict. |
| `summary.txt` | All machines together: coins bet and paid out, the house's total win/loss, what the odds should have paid back against what was actually paid, and a verdict on whether any difference is ordinary luck or more than luck explains. Then a line per machine, and the biggest winner and loser. |

If one of these files is open in Excel while people play, the server can't write to it. It logs a warning and tries again on the next spin.

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
| `SlotDocks.cs` | Copies the game's docks onto the machine: the coin slot (from the market stall), the Winnings bubble (from the ATM), and two hidden docks. The server writes each spin's result into one, so every game's reels stop on the same creatures before anything is paid. It writes its paytable into the other, so every machine shows the server's payouts. |
| `SlotIcons.cs` | The reel symbols: the game's book drawings of each creature. |
| `SlotSign.cs` | The glowing sign. |
| `SlotGlyphs.cs` | The game's book drawings used on the paytable and as engravings. |
| `Bonus.cs` | The bonus-row effects. They use the game's own effects (heal, hunger, speed, damage, damage protection, glow). |
| `Core.cs` | Settings, bets, coins, spins and payouts. |
| `Ledger.cs` | The casino ledger: per-event log, per-player totals and the house summary. |
| `SpawnPlacement.cs` | Puts a spawned machine on the ground, facing the player. |
| `WinSound.cs` | Win and firework sounds. |

The prefab ID `21251` is fixed. Don't change it, or machines already placed in a world will break.
