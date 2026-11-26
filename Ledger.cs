using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MelonLoader;

namespace LuckySpriggull
{
    /// <summary>
    /// The casino's books, kept by the server in UserData/LuckySpriggull/Ledger:
    ///  - ledger-YYYY-MM.csv: every coin put in, every take-back, every spin (bet, result, payout) and every winnings
    ///    pickup, one row each, as it happens. Opens in Excel or Google Sheets.
    ///  - players.csv: each player's running totals (spins, wins, bet, won, net, return, biggest win...). Rewritten as
    ///    play happens, and read back on start, so totals carry over restarts.
    ///  - machines.csv: each machine's totals: bet, paid out, house profit, its real return against the odds.
    ///  - summary.txt: all machines together, how their real payouts compare with what the odds should pay, and
    ///    whether any difference is ordinary luck or more than luck explains.
    /// Times are the server's local time.
    /// </summary>
    internal sealed class Ledger
    {
        private sealed class PlayerTotals
        {
            public int Id;
            public string Name = "";
            public DateTime FirstSeen, LastSeen;
            public long Spins, Wins, Losses, BonusRows, Jackpots;
            public long TotalBet, TotalWon, BiggestWin;
            public long Inserted, TakenBack, CashedOut;
            public long Net => TotalWon - TotalBet;
        }

        private sealed class MachineTotals
        {
            public uint Id;
            public DateTime FirstSpin, LastSpin;
            public long Spins, Wins, BonusRows, Jackpots, TotalBet, TotalWon;
            public double BetSquares;   // the sum of each bet squared: how much a run of big bets can swing the result
            public long Profit => TotalBet - TotalWon;
        }

        private readonly Dictionary<uint, MachineTotals> machines = new Dictionary<uint, MachineTotals>();
        private double expectedReturn = double.NaN, returnVariance;   // from the payout odds (see SetOdds)
        private const string MachinesHeader = "Machine,FirstSpin,LastSpin,Spins,CoinWins,BonusRows,Jackpots,CoinsBet,CoinsPaidOut,HouseProfit,ActualReturnPercent,ExpectedReturnPercent,Verdict,BetSquares";

        private readonly string dir;
        private readonly Dictionary<int, PlayerTotals> players = new Dictionary<int, PlayerTotals>();
        private readonly MelonLogger.Instance log;
        private bool warned;

        private const string LedgerHeader = "Time,Event,PlayerId,Player,Machine,Coins,Result,Payout,PlayerNet";
        private const string PlayersHeader = "PlayerId,Player,FirstSeen,LastSeen,Spins,Wins,Losses,BonusRows,Jackpots,TotalBet,TotalWon,Net,ReturnPercent,BiggestWin,CoinsInserted,CoinsTakenBack,CoinsCashedOut";

        public Ledger(string folder, MelonLogger.Instance log)
        {
            this.log = log;
            dir = folder;
            Directory.CreateDirectory(dir);
            Load();
        }

        /// <summary>
        /// The odds, as (multiplier, chance) pairs, so real payouts can be compared with them. Bonus rows pay no coins, so
        /// they don't count towards the coin return.
        /// </summary>
        public void SetOdds(IEnumerable<KeyValuePair<int, float>> payouts)
        {
            var list = payouts.ToList();
            double mean = list.Sum(kv => kv.Key * (double)kv.Value);
            double meanSquare = list.Sum(kv => kv.Key * (double)kv.Key * kv.Value);
            expectedReturn = mean;
            returnVariance = Math.Max(0, meanSquare - mean * mean);
        }

        // ---------- events ----------

        public void Inserted(int player, uint machine, int coins)
        {
            PlayerTotals p = Touch(player);
            p.Inserted += coins;
            Row("Insert", p, machine, coins, "", 0);
        }

        public void TakenBack(int player, uint machine, int coins)
        {
            PlayerTotals p = Touch(player);
            p.TakenBack += coins;
            Row("TakeBack", p, machine, coins, "", 0);
        }

        public void CashedOut(int player, uint machine, int coins)
        {
            PlayerTotals p = Touch(player);
            p.CashedOut += coins;
            Row("CashOut", p, machine, coins, "", 0);
            SavePlayers();
        }

        /// <summary>A finished spin. <paramref name="result"/> is "Loss", e.g. "5x", or e.g. "Bonus: Babu".</summary>
        public void Spin(int player, uint machine, int bet, string result, long payout, bool win, bool bonus, bool jackpot)
        {
            PlayerTotals p = Touch(player);
            p.Spins++;
            p.TotalBet += bet;
            p.TotalWon += payout;
            if (win) p.Wins++;
            else if (bonus) p.BonusRows++;
            else p.Losses++;
            if (jackpot) p.Jackpots++;
            if (payout > p.BiggestWin) p.BiggestWin = payout;

            if (!machines.TryGetValue(machine, out MachineTotals m))
                machines[machine] = m = new MachineTotals { Id = machine, FirstSpin = DateTime.Now };
            m.LastSpin = DateTime.Now;
            m.Spins++;
            m.TotalBet += bet;
            m.TotalWon += payout;
            m.BetSquares += (double)bet * bet;
            if (win) m.Wins++;
            if (bonus) m.BonusRows++;
            if (jackpot) m.Jackpots++;

            Row("Spin", p, machine, bet, result, payout);
            SavePlayers();
        }

        /// <summary>"(this player: 12 spins, 1,200 bet, 900 won, net -300)" for the server log.</summary>
        public string Describe(int player) =>
            players.TryGetValue(player, out PlayerTotals p)
                ? $"({p.Name}: {p.Spins} spins, {p.TotalBet:N0} bet, {p.TotalWon:N0} won, net {p.Net:+#,0;-#,0;0})"
                : "";

        // ---------- files ----------

        private PlayerTotals Touch(int id)
        {
            if (!players.TryGetValue(id, out PlayerTotals p))
                players[id] = p = new PlayerTotals { Id = id, FirstSeen = DateTime.Now };
            p.LastSeen = DateTime.Now;
            string name = NameOf(id);
            if (!string.IsNullOrEmpty(name)) p.Name = name;
            return p;
        }

        /// <summary>The player's name, while they're on the server.</summary>
        private static string NameOf(int id)
        {
            try
            {
                foreach (var ip in global::Player.AllPlayers)
                    if (ip?.UserInfo != null && ip.UserInfo.Identifier == id) return ip.UserInfo.Username;
            }
            catch (Exception) { }
            return null;
        }

        private void Row(string kind, PlayerTotals p, uint machine, long coins, string result, long payout)
        {
            DateTime now = DateTime.Now;
            string file = Path.Combine(dir, $"ledger-{now:yyyy-MM}.csv");
            string line = string.Join(",", new[]
            {
                now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), kind, p.Id.ToString(CultureInfo.InvariantCulture),
                Csv(p.Name), machine.ToString(CultureInfo.InvariantCulture), coins.ToString(CultureInfo.InvariantCulture), Csv(result),
                payout.ToString(CultureInfo.InvariantCulture), p.Net.ToString(CultureInfo.InvariantCulture),
            });
            Write(() =>
            {
                if (!File.Exists(file)) File.WriteAllText(file, LedgerHeader + Environment.NewLine);
                File.AppendAllText(file, line + Environment.NewLine);
            });
        }

        private void SavePlayers()
        {
            var sb = new StringBuilder(PlayersHeader + Environment.NewLine);
            foreach (PlayerTotals p in players.Values.OrderByDescending(x => x.TotalBet))
            {
                string ret = p.TotalBet > 0 ? (100.0 * p.TotalWon / p.TotalBet).ToString("0.0", CultureInfo.InvariantCulture) : "";
                sb.AppendLine(string.Join(",", new[]
                {
                    p.Id.ToString(CultureInfo.InvariantCulture), Csv(p.Name), Time(p.FirstSeen), Time(p.LastSeen),
                    N(p.Spins), N(p.Wins), N(p.Losses), N(p.BonusRows), N(p.Jackpots), N(p.TotalBet), N(p.TotalWon), N(p.Net),
                    ret, N(p.BiggestWin), N(p.Inserted), N(p.TakenBack), N(p.CashedOut),
                }));
            }
            Write(() => File.WriteAllText(Path.Combine(dir, "players.csv"), sb.ToString()));

            var mb = new StringBuilder(MachinesHeader + Environment.NewLine);
            foreach (MachineTotals m in machines.Values.OrderBy(x => x.Id))
                mb.AppendLine(string.Join(",", new[]
                {
                    m.Id.ToString(CultureInfo.InvariantCulture), Time(m.FirstSpin), Time(m.LastSpin), N(m.Spins), N(m.Wins), N(m.BonusRows),
                    N(m.Jackpots), N(m.TotalBet), N(m.TotalWon), N(m.Profit), Percent(m.TotalWon, m.TotalBet), ExpectedPercent(),
                    Csv(Verdict(m.Spins, m.TotalBet, m.TotalWon, m.BetSquares)), m.BetSquares.ToString("R", CultureInfo.InvariantCulture),
                }));
            Write(() => File.WriteAllText(Path.Combine(dir, "machines.csv"), mb.ToString()));
            Write(() => File.WriteAllText(Path.Combine(dir, "summary.txt"), Summary()));
        }

        /// <summary>All machines together, against the odds, then each machine.</summary>
        public string Summary()
        {
            long spins = machines.Values.Sum(m => m.Spins), bet = machines.Values.Sum(m => m.TotalBet), won = machines.Values.Sum(m => m.TotalWon);
            long wins = machines.Values.Sum(m => m.Wins), bonus = machines.Values.Sum(m => m.BonusRows), jackpots = machines.Values.Sum(m => m.Jackpots);
            double squares = machines.Values.Sum(m => m.BetSquares);
            double expectedPaid = double.IsNaN(expectedReturn) ? double.NaN : bet * expectedReturn;
            var sb = new StringBuilder();
            sb.AppendLine("The Lucky Spriggull: house summary, all machines (all time)");
            sb.AppendLine($"Updated:              {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Machines:             {machines.Count:N0}    Players: {players.Count:N0}");
            sb.AppendLine($"Spins:                {spins:N0}  ({wins:N0} coin wins, {bonus:N0} bonus rows, {jackpots:N0} jackpots)");
            sb.AppendLine();
            sb.AppendLine("WIN / LOSS (coins)");
            sb.AppendLine($"Coins bet:            {bet:N0}");
            sb.AppendLine($"Coins paid out:       {won:N0}");
            sb.AppendLine($"House win/loss:       {bet - won:+#,0;-#,0;0}  ({(bet - won >= 0 ? "the house is ahead" : "the players are ahead")})");
            sb.AppendLine();
            sb.AppendLine("AGAINST THE ODDS");
            sb.AppendLine($"Should pay back:      {ExpectedPercent()}% of every coin bet (from the Payouts setting)");
            sb.AppendLine($"Actually paid back:   {Percent(won, bet)}%");
            if (!double.IsNaN(expectedPaid))
            {
                sb.AppendLine($"Expected to pay out:  {expectedPaid:N0} coins for what's been bet");
                sb.AppendLine($"Difference:           {won - expectedPaid:+#,0;-#,0;0} coins ({(won >= expectedPaid ? "players got more than the odds give" : "players got less than the odds give")})");
            }
            sb.AppendLine($"Verdict:              {Verdict(spins, bet, won, squares)}");
            sb.AppendLine();
            if (machines.Count > 0)
            {
                sb.AppendLine("EACH MACHINE");
                foreach (MachineTotals m in machines.Values.OrderBy(x => x.Id))
                    sb.AppendLine($"  Machine {m.Id}: {m.Spins:N0} spins, {m.TotalBet:N0} bet, {m.TotalWon:N0} paid, house {m.Profit:+#,0;-#,0;0}, " +
                                  $"return {Percent(m.TotalWon, m.TotalBet)}%. {Verdict(m.Spins, m.TotalBet, m.TotalWon, m.BetSquares)}");
                sb.AppendLine();
            }
            // Only someone actually ahead counts as a winner, and only someone actually behind as a loser.
            PlayerTotals top = players.Values.Where(p => p.Spins > 0 && p.Net > 0).OrderByDescending(p => p.Net).FirstOrDefault();
            PlayerTotals bottom = players.Values.Where(p => p.Spins > 0 && p.Net < 0).OrderBy(p => p.Net).FirstOrDefault();
            sb.AppendLine(top != null ? $"Biggest winner:       {top.Name} ({top.Id}), net {top.Net:+#,0;-#,0;0}" : "Biggest winner:       nobody is ahead yet");
            sb.AppendLine(bottom != null ? $"Biggest loser:        {bottom.Name} ({bottom.Id}), net {bottom.Net:+#,0;-#,0;0}" : "Biggest loser:        nobody is behind yet");
            sb.AppendLine();
            sb.AppendLine("How the verdict works: slot results swing a lot in the short run, especially with big bets. The verdict");
            sb.AppendLine("works out how far the real payout can drift from the odds by plain luck for the bets made so far, and");
            sb.AppendLine("only flags a machine when the difference is bigger than luck would explain about 19 times in 20.");
            return sb.ToString();
        }

        /// <summary>
        /// Whether a real return is ordinary luck or more than luck explains. The payout of a bet of b coins varies by
        /// b x (the spread of the multipliers); over many bets those add up, so the expected wobble of the return is
        /// sqrt(variance x sum of b squared) / total bet. More than 2 of those away from the odds is flagged.
        /// </summary>
        private string Verdict(long spins, long bet, long won, double betSquares)
        {
            if (double.IsNaN(expectedReturn) || bet <= 0) return "No spins yet.";
            if (spins < 30) return $"Too few spins to judge yet ({spins}; check again after 30 or more).";
            double wobble = Math.Sqrt(returnVariance * betSquares) / bet;
            if (wobble <= 0) return "Within the odds.";
            double actual = (double)won / bet;
            double z = (actual - expectedReturn) / wobble;
            string range = $"normal luck range {Math.Max(0, expectedReturn - 2 * wobble) * 100:0.#}% to {(expectedReturn + 2 * wobble) * 100:0.#}%";
            if (z > 2) return $"PAYING TOO MUCH: more than luck explains ({range}). Check the Payouts setting.";
            if (z < -2) return $"PAYING TOO LITTLE: less than luck explains ({range}). Check the Payouts setting.";
            return $"Normal: within ordinary luck ({range}).";
        }

        private string ExpectedPercent() => double.IsNaN(expectedReturn) ? "-" : (expectedReturn * 100).ToString("0.0", CultureInfo.InvariantCulture);

        private static string Percent(long won, long bet) => bet > 0 ? (100.0 * won / bet).ToString("0.0", CultureInfo.InvariantCulture) : "-";

        private void Load()
        {
            string file = Path.Combine(dir, "players.csv");
            if (!File.Exists(file)) { LoadMachines(); return; }
            try
            {
                foreach (string line in File.ReadAllLines(file).Skip(1))
                {
                    List<string> f = Split(line);
                    if (f.Count < 17 || !int.TryParse(f[0], out int id)) continue;
                    var p = new PlayerTotals
                    {
                        Id = id, Name = f[1], FirstSeen = ParseTime(f[2]), LastSeen = ParseTime(f[3]),
                        Spins = L(f[4]), Wins = L(f[5]), Losses = L(f[6]), BonusRows = L(f[7]), Jackpots = L(f[8]),
                        TotalBet = L(f[9]), TotalWon = L(f[10]), BiggestWin = L(f[13]),
                        Inserted = L(f[14]), TakenBack = L(f[15]), CashedOut = L(f[16]),
                    };
                    players[id] = p;
                }
                log.Msg($"Ledger: loaded totals for {players.Count} player(s) from {file}.");
                LoadMachines();
            }
            catch (Exception e) { log.Warning("Ledger: couldn't read players.csv (" + e.Message + "); starting fresh totals."); }
        }

        private void LoadMachines()
        {
            string file = Path.Combine(dir, "machines.csv");
            if (!File.Exists(file)) return;
            try
            {
                foreach (string line in File.ReadAllLines(file).Skip(1))
                {
                    List<string> f = Split(line);
                    if (f.Count < 14 || !uint.TryParse(f[0], out uint id)) continue;
                    machines[id] = new MachineTotals
                    {
                        Id = id, FirstSpin = ParseTime(f[1]), LastSpin = ParseTime(f[2]), Spins = L(f[3]), Wins = L(f[4]), BonusRows = L(f[5]),
                        Jackpots = L(f[6]), TotalBet = L(f[7]), TotalWon = L(f[8]),
                        BetSquares = double.TryParse(f[13], NumberStyles.Float, CultureInfo.InvariantCulture, out double sq) ? sq : 0,
                    };
                }
            }
            catch (Exception e) { log.Warning("Ledger: couldn't read machines.csv (" + e.Message + "); starting fresh machine totals."); }
        }

        private void Write(Action write)
        {
            try { write(); }
            catch (IOException e)
            {
                // Usually the file is open in Excel. Keep playing; the next write tries again.
                if (!warned) { warned = true; log.Warning("Ledger: couldn't write a ledger file (" + e.Message + "). Is it open in another program?"); }
            }
        }

        // ---------- small helpers ----------

        private static string Csv(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private static List<string> Split(string line)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { parts.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            parts.Add(cur.ToString());
            return parts;
        }

        private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
        private static long L(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        private static DateTime ParseTime(string s) =>
            DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t) ? t : DateTime.Now;
    }
}
