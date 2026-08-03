using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PRoCon.Core;
using PRoCon.Core.Plugin;
using PRoCon.Core.Players;

namespace PRoConEvents
{
    public class CSquadAutoBalancer : PRoConPluginAPI, IPRoConPluginInterface
    {
        // --- MANDATORY PLUGIN INTERFACE INFO ---
        public string GetPluginName() { return "Squad Auto Balancer"; }
        public string GetPluginVersion() { return "2.0.0"; }
        public string GetPluginAuthor() { return "Yonatan (kanus15elef)"; }
        public string GetPluginWebsite() { return "localhost"; }
        public string GetPluginDescription() { return "Optimized match-end balancing with squad preservation, post-process solo swapping, and !stats lookup."; }

        // --- Configurable plugin variables ---
        private double scoreWeight = 0.01;   
        private double kdWeight = 10.0;      
        private double killWeight = 5.0;     
        private int inviteExpirySeconds = 60; 
        private bool preserveSquads = true;  
        private int balanceDelaySeconds = 5; 
        private int scoreRequestTimeoutMs = 2000; 
        
        private int assistEnableDelayMinutes = 5;
        private int nukeTriggerMinutes = 2;
        private int nukeDurationSeconds = 30;

        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            return new List<CPluginVariable>()
            {
                new CPluginVariable("scoreWeight", typeof(double), scoreWeight.ToString()),
                new CPluginVariable("kdWeight", typeof(double), kdWeight.ToString()),
                new CPluginVariable("killWeight", typeof(double), killWeight.ToString()),
                new CPluginVariable("inviteExpirySeconds", typeof(int), inviteExpirySeconds.ToString()),
                new CPluginVariable("preserveSquads", typeof(bool), preserveSquads.ToString()),
                new CPluginVariable("balanceDelaySeconds", typeof(int), balanceDelaySeconds.ToString()),
                new CPluginVariable("scoreRequestTimeoutMs", typeof(int), scoreRequestTimeoutMs.ToString()),
                new CPluginVariable("assistEnableDelayMinutes", typeof(int), assistEnableDelayMinutes.ToString()),
                new CPluginVariable("nukeTriggerMinutes", typeof(int), nukeTriggerMinutes.ToString()),
                new CPluginVariable("nukeDurationSeconds", typeof(int), nukeDurationSeconds.ToString())
            };
        }

        public List<CPluginVariable> GetPluginVariables() { return GetDisplayPluginVariables(); }

        public void SetPluginVariable(string strVariable, string strValue)
        {
            try
            {
                switch (strVariable)
                {
                    case "scoreWeight": scoreWeight = double.Parse(strValue); break;
                    case "kdWeight": kdWeight = double.Parse(strValue); break;
                    case "killWeight": killWeight = double.Parse(strValue); break;
                    case "inviteExpirySeconds": inviteExpirySeconds = int.Parse(strValue); break;
                    case "preserveSquads": preserveSquads = bool.Parse(strValue); break;
                    case "balanceDelaySeconds": balanceDelaySeconds = int.Parse(strValue); break;
                    case "scoreRequestTimeoutMs": scoreRequestTimeoutMs = int.Parse(strValue); break;
                    case "assistEnableDelayMinutes": assistEnableDelayMinutes = int.Parse(strValue); break;
                    case "nukeTriggerMinutes": nukeTriggerMinutes = int.Parse(strValue); break;
                    case "nukeDurationSeconds": nukeDurationSeconds = int.Parse(strValue); break;
                }
            }
            catch { /* ignore invalid values */ }
        }

        // --- Models ---
        public class CustomPlayer
        {
            public string Name { get; set; }
            public int OverallScore { get; set; }
            public int Kills { get; set; }
            public int Deaths { get; set; }
            public int TeamId { get; set; }
            public string CurrentSquadName { get; set; }
            public string UniqueId { get; set; } 
            public int PreviousRoundCalculatedScore { get; set; } 
            
            public bool IsAFK { get { return OverallScore == 0 && Kills == 0; } }
            public double KD { get { return (double)Kills / Math.Max(1, Deaths); } }

            public int CalculatedPlayerScore(double scoreWeight, double kdWeight, double killWeight)
            {
                int scorePoints = (int)Math.Round(OverallScore * scoreWeight);
                int kdPoints = (int)Math.Floor(KD * kdWeight);
                int killPoints = (int)Math.Round(Kills * killWeight);
                return scorePoints + kdPoints + killPoints + PreviousRoundCalculatedScore;
            }

            public CustomPlayer ShallowClone()
            {
                return new CustomPlayer
                {
                    Name = this.Name,
                    OverallScore = this.OverallScore,
                    Kills = this.Kills,
                    Deaths = this.Deaths,
                    TeamId = this.TeamId,
                    CurrentSquadName = this.CurrentSquadName,
                    UniqueId = this.UniqueId,
                    PreviousRoundCalculatedScore = this.PreviousRoundCalculatedScore
                };
            }
        }

        public class CustomSquad
        {
            public string Name { get; set; }
            public string LeaderName { get; set; }
            public List<CustomPlayer> Members { get; set; }

            public CustomSquad() { Members = new List<CustomPlayer>(); }

            public int TotalScore(double scoreWeight, double kdWeight, double killWeight)
            {
                int total = 0;
                for (int i = 0; i < Members.Count; i++) total += Members[i].CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                return total;
            }
        }

        public class BalanceGroup
        {
            public List<CustomPlayer> Players { get; set; }
            public BalanceGroup() { Players = new List<CustomPlayer>(); }
            public int TotalScore(double scoreWeight, double kdWeight, double killWeight)
            {
                int total = 0;
                for (int i = 0; i < Players.Count; i++) total += Players[i].CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                return total;
            }
            public int Size { get { return Players.Count; } }
        }

        private class InviteInfo
        {
            public string SquadName;
            public DateTime ExpiresUtc;
            public InviteInfo(string squadName, DateTime expiresUtc)
            {
                this.SquadName = squadName;
                this.ExpiresUtc = expiresUtc;
            }
        }

        // --- State Collections ---
        private readonly Dictionary<string, CustomSquad> activeSquads = new Dictionary<string, CustomSquad>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CustomPlayer> trackedPlayers = new Dictionary<string, CustomPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InviteInfo> pendingInvites = new Dictionary<string, InviteInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CustomPlayer> matchProfiles = new Dictionary<string, CustomPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ManualResetEventSlim> pendingScoreRequests = new Dictionary<string, ManualResetEventSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> authorizedTeams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly object stateLock = new object();
        private readonly Random randomGen = new Random();

        private bool isMatchRunning = true; 
        private string currentGameMode = "";
        private int heistRoundCount = 0;
        private Dictionary<string, int> previousRoundScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        private List<CustomPlayer> pendingMapChangeLobbySnapshot = null;
        private List<CustomSquad> pendingMapChangeSquadsSnapshot = null;

        private DateTime matchStartTime = DateTime.UtcNow;
        private bool teamANuked = false;
        private bool teamBNuked = false;
        private DateTime teamANukeEndTime;
        private DateTime teamBNukeEndTime;

        public CSquadAutoBalancer() { }

        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            this.RegisterEvents(this.GetType().Name,
                "OnGlobalChat", "OnRoundOver", "OnPlayerJoin", "OnPlayerLeft", 
                "OnPlayerKilled", "OnListPlayers", "OnPlayerTeamChange", 
                "OnServerInfo", "OnLevelLoaded", "OnPlayerSpawned");
        }

        public void OnPluginEnable() { SafeExecuteCommand("procon.protected.plugin.console", "Squad Auto Balancer 2.1.0 Enabled!"); }
        public void OnPluginDisable() { }

        public override void OnServerInfo(CServerInfo serverInfo)
        {
            if (serverInfo != null && !string.IsNullOrEmpty(serverInfo.GameMode))
            {
                lock (stateLock) { this.currentGameMode = serverInfo.GameMode; }
            }
        }

        public override void OnLevelLoaded(string mapFileName, string GameMode, int roundsPlayed, int roundsTotal)
        {
            lock (stateLock)
            {
                this.currentGameMode = GameMode;
                this.heistRoundCount = roundsPlayed; 
                this.previousRoundScores.Clear();
                this.authorizedTeams.Clear(); 
                
                this.matchStartTime = DateTime.UtcNow;
                this.teamANuked = false;
                this.teamBNuked = false;
            }

            if (pendingMapChangeLobbySnapshot != null && pendingMapChangeSquadsSnapshot != null)
            {
                List<CustomPlayer> lobby = pendingMapChangeLobbySnapshot;
                List<CustomSquad> squads = pendingMapChangeSquadsSnapshot;
                
                pendingMapChangeLobbySnapshot = null;
                pendingMapChangeSquadsSnapshot = null;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Thread.Sleep(2000); 
                        RunBalancer(lobby, squads);
                        lock (stateLock) { matchProfiles.Clear(); }
                    }
                    catch (Exception ex) { TryLogConsole("OnLevelLoaded Balancer error: " + ex.Message); }
                });
            }
        }

        public override void OnPlayerJoin(string strSoldierName)
        {
            lock (stateLock)
            {
                if (!trackedPlayers.ContainsKey(strSoldierName)) trackedPlayers[strSoldierName] = new CustomPlayer { Name = strSoldierName };
                if (!matchProfiles.ContainsKey(strSoldierName)) matchProfiles[strSoldierName] = new CustomPlayer { Name = strSoldierName };
            }
        }

        public override void OnPlayerLeft(CPlayerInfo playerInfo)
        {
            string strSoldierName = playerInfo.SoldierName;
            lock (stateLock)
            {
                if (trackedPlayers.ContainsKey(strSoldierName))
                {
                    CustomPlayer player = trackedPlayers[strSoldierName];
                    if (!string.IsNullOrEmpty(player.CurrentSquadName) && activeSquads.ContainsKey(player.CurrentSquadName))
                    {
                        activeSquads[player.CurrentSquadName].Members.Remove(player);
                        if (activeSquads[player.CurrentSquadName].Members.Count == 0) activeSquads.Remove(player.CurrentSquadName);
                    }
                    trackedPlayers.Remove(strSoldierName);
                }
                matchProfiles.Remove(strSoldierName);
                authorizedTeams.Remove(strSoldierName);
            }
        }

        public override void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        {
            lock (stateLock)
            {
                foreach (CPlayerInfo p in players)
                {
                    if (!matchProfiles.ContainsKey(p.SoldierName)) matchProfiles[p.SoldierName] = new CustomPlayer { Name = p.SoldierName };
                    matchProfiles[p.SoldierName].OverallScore = p.Score;
                    matchProfiles[p.SoldierName].Kills = p.Kills;
                    matchProfiles[p.SoldierName].Deaths = p.Deaths;
                    matchProfiles[p.SoldierName].TeamId = p.TeamID;

                    if (!trackedPlayers.ContainsKey(p.SoldierName)) trackedPlayers[p.SoldierName] = new CustomPlayer { Name = p.SoldierName };
                    trackedPlayers[p.SoldierName].OverallScore = p.Score;
                    trackedPlayers[p.SoldierName].Kills = p.Kills;
                    trackedPlayers[p.SoldierName].Deaths = p.Deaths;
                    trackedPlayers[p.SoldierName].TeamId = p.TeamID;
                }
            }
        }

        public void OnPlayerKilled(string killerName, string victimName, string weapon, bool isHeadshot) { UpdateKillDeath(killerName, victimName); }
        public void OnPlayerKilled(string killerName, string victimName, string weapon) { UpdateKillDeath(killerName, victimName); }
        public void OnPlayerKilled(string killerName, string victimName, int weaponId) { UpdateKillDeath(killerName, victimName); }
        public void OnPlayerKilled(string killerName, string victimName) { UpdateKillDeath(killerName, victimName); }

        private void UpdateKillDeath(string killerName, string victimName)
        {
            lock (stateLock)
            {
                if (!string.IsNullOrEmpty(killerName) && trackedPlayers.ContainsKey(killerName)) trackedPlayers[killerName].Kills++;
                if (!string.IsNullOrEmpty(victimName) && trackedPlayers.ContainsKey(victimName)) trackedPlayers[victimName].Deaths++;
            }
        }
        
        public void OnPlayerSpawned(string soldierName)
        {
            lock (stateLock)
            {
                if (!trackedPlayers.ContainsKey(soldierName)) return;
                int team = trackedPlayers[soldierName].TeamId;
                
                if (team == 1 && teamANuked && DateTime.UtcNow < teamANukeEndTime)
                {
                    SafeExecuteCommand("procon.protected.send", "admin.killPlayer", soldierName);
                    SendChat(soldierName, "Your team is currently NUKED for spawn trapping.");
                }
                else if (team == 2 && teamBNuked && DateTime.UtcNow < teamBNukeEndTime)
                {
                    SafeExecuteCommand("procon.protected.send", "admin.killPlayer", soldierName);
                    SendChat(soldierName, "Your team is currently NUKED for spawn trapping.");
                }
            }
        }

        public override void OnPlayerTeamChange(string soldierName, int teamId, int squadId)
        {
            lock (stateLock)
            {
                if (trackedPlayers.ContainsKey(soldierName)) trackedPlayers[soldierName].TeamId = teamId;
                if (matchProfiles.ContainsKey(soldierName)) matchProfiles[soldierName].TeamId = teamId;

                if (authorizedTeams.ContainsKey(soldierName))
                {
                    int expectedTeam = authorizedTeams[soldierName];
                    if (expectedTeam != teamId && expectedTeam != 0)
                    {
                        SafeExecuteCommand("procon.protected.send", "admin.movePlayer", soldierName, expectedTeam.ToString(), "0", "true");
                        SendChat(soldierName, "Manual team switching via menu is disabled. Use !assist.");
                        trackedPlayers[soldierName].TeamId = expectedTeam; 
                        return;
                    }
                }
                else
                {
                    authorizedTeams[soldierName] = teamId;
                }
            }
        }

        public override void OnGlobalChat(string strSpeaker, string strMessage)
        {
            if (string.IsNullOrEmpty(strMessage) || !strMessage.StartsWith("!")) return;

            lock (stateLock)
            {
                if (!trackedPlayers.ContainsKey(strSpeaker)) trackedPlayers[strSpeaker] = new CustomPlayer { Name = strSpeaker };
                if (!matchProfiles.ContainsKey(strSpeaker)) matchProfiles[strSpeaker] = new CustomPlayer { Name = strSpeaker };
            }

            CustomPlayer speaker;
            lock (stateLock) { speaker = trackedPlayers[strSpeaker]; }

            string[] args = strMessage.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) return;

            string command = args[0].ToLower();

            switch (command)
            {
                case "!squadcreate": if (args.Length >= 2) CreateSquad(speaker, strMessage.Substring(args[0].Length).Trim()); break;
                case "!squadinvite": if (args.Length >= 2) InvitePlayer(speaker, FindPlayer(args[1])); break;
                case "!squadaccept": AcceptInvite(speaker); break;
                case "!squadreject": RejectInvite(speaker); break;
                case "!squadleave": LeaveSquad(speaker); break;
                case "!squadkick": if (args.Length >= 2) KickPlayer(speaker, FindPlayer(args[1])); break;
                case "!squadclose": CloseSquad(speaker); break;
                case "!squadmembers": ShowSquadMembers(speaker); break;
                case "!assist": ExecuteAssistCommand(speaker); break;
                case "!stats":
                    if (args.Length >= 2)
                    {
                        string targetName = strMessage.Substring(args[0].Length).Trim();
                        ShowPlayerScoreByName(speaker, targetName);
                    }
                    else
                    {
                        ShowPlayerScoreByName(speaker, speaker.Name);
                    }
                    break;
            }
        }

        private CustomPlayer FindPlayer(string searchName)
        {
            lock (stateLock)
            {
                CustomPlayer exact;
                if (trackedPlayers.TryGetValue(searchName, out exact)) return exact;
                var matches = trackedPlayers.Where(kvp => kvp.Key.StartsWith(searchName, StringComparison.OrdinalIgnoreCase)).ToList();
                return matches.Count == 1 ? matches[0].Value : null;
            }
        }

        private void CreateSquad(CustomPlayer creator, string squadName)
        {
            lock (stateLock)
            {
                if (!string.IsNullOrEmpty(creator.CurrentSquadName))
                {
                    SendChat(creator.Name, "You are already in a squad. Type !squadleave first.");
                    return;
                }
                if (activeSquads.ContainsKey(squadName))
                {
                    SendChat(creator.Name, squadName + " already taken.");
                    return;
                }

                CustomSquad newSquad = new CustomSquad { Name = squadName, LeaderName = creator.Name };
                newSquad.Members.Add(creator);
                activeSquads[squadName] = newSquad;
                creator.CurrentSquadName = squadName;
            }

            SendChat(creator.Name, "Squad " + squadName + " created.");
            SendGlobalAnnouncement("Squad '" + squadName + "' created by " + creator.Name + "!");
        }

        private void InvitePlayer(CustomPlayer inviter, CustomPlayer target)
        {
            if (target == null) return;
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(inviter.CurrentSquadName) || !activeSquads.ContainsKey(inviter.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[inviter.CurrentSquadName];
                if (squad.LeaderName != inviter.Name || squad.Members.Count >= 5) return;

                pendingInvites[target.Name] = new InviteInfo(squad.Name, DateTime.UtcNow.AddSeconds(inviteExpirySeconds));
            }
            SendChat(inviter.Name, "Invite sent to " + target.Name);
            SendChat(target.Name, "Invited to " + inviter.CurrentSquadName + ". Type !squadaccept or !squadreject.");
        }

        private void AcceptInvite(CustomPlayer player)
        {
            string squadName = null;
            lock (stateLock)
            {
                if (!pendingInvites.ContainsKey(player.Name)) { SendChat(player.Name, "No pending invite."); return; }
                InviteInfo invite = pendingInvites[player.Name];
                if (invite.ExpiresUtc < DateTime.UtcNow) { pendingInvites.Remove(player.Name); SendChat(player.Name, "Invite expired."); return; }
                squadName = invite.SquadName;
                pendingInvites.Remove(player.Name);

                if (!string.IsNullOrEmpty(player.CurrentSquadName)) LeaveSquad(player);

                if (activeSquads.ContainsKey(squadName))
                {
                    activeSquads[squadName].Members.Add(player);
                    player.CurrentSquadName = squadName;
                    SendChat(player.Name, "Joined squad " + squadName);
                }
            }
        }

        private void RejectInvite(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (!pendingInvites.ContainsKey(player.Name)) return;
                pendingInvites.Remove(player.Name);
                SendChat(player.Name, "Invite rejected.");
            }
        }

        private void LeaveSquad(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(player.CurrentSquadName) || !activeSquads.ContainsKey(player.CurrentSquadName)) return;
                string squadName = player.CurrentSquadName;
                CustomSquad squad = activeSquads[squadName];
                squad.Members.Remove(player);
                player.CurrentSquadName = null;

                if (squad.Members.Count == 0) activeSquads.Remove(squadName);
                else if (squad.LeaderName == player.Name) squad.LeaderName = squad.Members[0].Name;
            }
        }

        private void KickPlayer(CustomPlayer leader, CustomPlayer target)
        {
            if (target == null) return;
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(leader.CurrentSquadName) || !activeSquads.ContainsKey(leader.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                if (squad.LeaderName != leader.Name || target.CurrentSquadName != squad.Name) return;

                squad.Members.Remove(target);
                target.CurrentSquadName = null;
            }
        }

        private void CloseSquad(CustomPlayer leader)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(leader.CurrentSquadName) || !activeSquads.ContainsKey(leader.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                if (squad.LeaderName != leader.Name) return;

                foreach (var m in squad.Members) m.CurrentSquadName = null;
                activeSquads.Remove(squad.Name);
            }
        }

        private void ShowSquadMembers(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(player.CurrentSquadName) || !activeSquads.ContainsKey(player.CurrentSquadName))
                {
                    SendChat(player.Name, "You are not in a squad.");
                    return;
                }
                SendChat(player.Name, "Squad Members: " + string.Join(", ", activeSquads[player.CurrentSquadName].Members.Select(m => m.Name)));
            }
        }

        private void ShowPlayerScoreByName(CustomPlayer requester, string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || requester == null) return;

            CustomPlayer profile = null;
            lock (stateLock)
            {
                if (!matchProfiles.TryGetValue(targetName, out profile))
                {
                    profile = matchProfiles.Values.FirstOrDefault(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase));
                    if (profile == null)
                    {
                        if (!trackedPlayers.TryGetValue(targetName, out profile))
                        {
                            profile = trackedPlayers.Values.FirstOrDefault(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
            }

            if (profile == null)
            {
                SendChat(requester.Name, "Player '" + targetName + "' not found or no stats available.");
                return;
            }

            string header = profile.Name + " stats are:";
            string kdLine = "K/D: " + profile.KD.ToString("F2");
            string killsLine = "Kills: " + profile.Kills;
            string scoreLine = "Score: " + profile.OverallScore;
            string overallLine = "Overall PlayerScore: " + profile.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);

            SendChat(requester.Name, header);
            SendChat(requester.Name, kdLine);
            SendChat(requester.Name, killsLine);
            SendChat(requester.Name, scoreLine);
            SendChat(requester.Name, overallLine);
        }

        private void ExecuteAssistCommand(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (!isMatchRunning || (DateTime.UtcNow - matchStartTime).TotalMinutes < assistEnableDelayMinutes)
                {
                    SendChat(player.Name, "Can't assist you to other team it will cause unbalanced teams !");
                    return; 
                }
            }

            if (player.TeamId != 1 && player.TeamId != 2) return;

            lock (stateLock)
            {
                int newTeamId = (player.TeamId == 1) ? 2 : 1;
                authorizedTeams[player.Name] = newTeamId; 
                SafeExecuteCommand("procon.protected.send", "admin.movePlayer", player.Name, newTeamId.ToString(), "0", "true");
                SendChat(player.Name, "Assisting you to other team thank you for assisting !");
                player.TeamId = newTeamId;
            }
        }

        public override void OnRoundOver(int winningTeamId)
        {
            lock (stateLock) { isMatchRunning = false; }

            bool isTwoRoundMode = !string.IsNullOrEmpty(currentGameMode) && 
                                  currentGameMode.IndexOf("heist", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isTwoRoundMode)
            {
                lock (stateLock) { heistRoundCount++; }
                if (heistRoundCount == 1)
                {
                    // REMOVED message here to keep chat clean between Heist rounds
                    isMatchRunning = true;
                    return; 
                }
                else { lock (stateLock) { heistRoundCount = 0; } }
            }

            lock (stateLock)
            {
                pendingMapChangeLobbySnapshot = trackedPlayers.Values.Select(p => p.ShallowClone()).ToList();
                pendingMapChangeSquadsSnapshot = new List<CustomSquad>();
                foreach (var kvp in activeSquads)
                {
                    CustomSquad copy = new CustomSquad { Name = kvp.Value.Name, LeaderName = kvp.Value.LeaderName };
                    copy.Members = kvp.Value.Members.Select(m => m.ShallowClone()).ToList();
                    pendingMapChangeSquadsSnapshot.Add(copy);
                }
            }
            
            ThreadPool.QueueUserWorkItem(_ => RequestAllPlayerScores(pendingMapChangeLobbySnapshot));
        }

        private void RequestAllPlayerScores(List<CustomPlayer> lobbySnapshot)
        {
            if (lobbySnapshot == null || lobbySnapshot.Count == 0) return;
            lock (pendingScoreRequests)
            {
                pendingScoreRequests.Clear();
                foreach (var p in lobbySnapshot) pendingScoreRequests[p.Name] = new ManualResetEventSlim(false);
            }
            foreach (var p in lobbySnapshot) SafeExecuteCommand("procon.protected.send", "server.getPlayerStats", p.Name);
        }

        private void RunBalancer(List<CustomPlayer> lobbySnapshot, List<CustomSquad> squadsSnapshot)
        {
            try
            {
                string currentMode = "";
                lock (stateLock)
                {
                    currentMode = this.currentGameMode;
                    foreach (var snap in lobbySnapshot)
                    {
                        if (matchProfiles.ContainsKey(snap.Name))
                        {
                            var live = matchProfiles[snap.Name];
                            snap.OverallScore = live.OverallScore;
                            snap.Kills = live.Kills;
                            snap.Deaths = live.Deaths;
                        }
                        if (previousRoundScores.ContainsKey(snap.Name)) snap.PreviousRoundCalculatedScore = previousRoundScores[snap.Name];
                    }
                }

                if (lobbySnapshot == null || lobbySnapshot.Count == 0) return;

                HashSet<string> groupedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<BalanceGroup> groups = new List<BalanceGroup>();

                if (preserveSquads)
                {
                    foreach (var s in squadsSnapshot)
                    {
                        BalanceGroup bg = new BalanceGroup();
                        foreach (var m in s.Members)
                        {
                            var playerMatch = lobbySnapshot.FirstOrDefault(lp => lp.Name == m.Name);
                            if (playerMatch != null)
                            {
                                bg.Players.Add(playerMatch);
                                groupedNames.Add(playerMatch.Name);
                            }
                        }
                        if (bg.Size > 0) groups.Add(bg);
                    }
                }

                foreach (var p in lobbySnapshot)
                {
                    if (!groupedNames.Contains(p.Name))
                    {
                        BalanceGroup bg = new BalanceGroup();
                        bg.Players.Add(p);
                        groups.Add(bg);
                    }
                }

                groups = groups.OrderByDescending(g => g.Size).ThenBy(g => randomGen.Next()).ToList();

                int totalPlayers = lobbySnapshot.Count;
                int maxTeamSize = (int)Math.Ceiling(totalPlayers / 2.0);

                int playersA = 0, playersB = 0;
                int scoreA = 0, scoreB = 0;
                List<BalanceGroup> teamAGroups = new List<BalanceGroup>();
                List<BalanceGroup> teamBGroups = new List<BalanceGroup>();

                // Phase 1: Initial Greedy Draft Allocation
                foreach (var g in groups)
                {
                    int gSize = g.Size;
                    int gScore = g.TotalScore(scoreWeight, kdWeight, killWeight);

                    bool canFitA = (playersA + gSize <= maxTeamSize);
                    bool canFitB = (playersB + gSize <= maxTeamSize);

                    if (canFitA && canFitB)
                    {
                        if (playersA < playersB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                        else if (playersB < playersA) { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                        else
                        {
                            if (scoreA <= scoreB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                            else { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                        }
                    }
                    else if (canFitA) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                    else if (canFitB) { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                    else
                    {
                        if (playersA <= playersB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                        else { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                    }
                }

                // Phase 2: Post-Processing Optimization (Solo Player Swapping)
                bool improved = true;
                while (improved)
                {
                    improved = false;
                    int currentDiff = Math.Abs(scoreA - scoreB);
                    BalanceGroup bestGA = null;
                    BalanceGroup bestGB = null;
                    int bestNewDiff = currentDiff;

                    var soloA = teamAGroups.Where(g => g.Size == 1).ToList();
                    var soloB = teamBGroups.Where(g => g.Size == 1).ToList();

                    foreach (var gA in soloA)
                    {
                        foreach (var gB in soloB)
                        {
                            int scoreGA = gA.TotalScore(scoreWeight, kdWeight, killWeight);
                            int scoreGB = gB.TotalScore(scoreWeight, kdWeight, killWeight);

                            int newScoreA = scoreA - scoreGA + scoreGB;
                            int newScoreB = scoreB - scoreGB + scoreGA;
                            int newDiff = Math.Abs(newScoreA - newScoreB);

                            if (newDiff < bestNewDiff)
                            {
                                bestNewDiff = newDiff;
                                bestGA = gA;
                                bestGB = gB;
                            }
                        }
                    }

                    if (bestGA != null && bestGB != null && bestNewDiff < currentDiff)
                    {
                        int sA = bestGA.TotalScore(scoreWeight, kdWeight, killWeight);
                        int sB = bestGB.TotalScore(scoreWeight, kdWeight, killWeight);

                        teamAGroups.Remove(bestGA);
                        teamBGroups.Add(bestGA);

                        teamBGroups.Remove(bestGB);
                        teamAGroups.Add(bestGB);

                        scoreA = scoreA - sA + sB;
                        scoreB = scoreB - sB + sA;
                        improved = true;
                    }
                }

                List<CustomPlayer> finalTeamA = new List<CustomPlayer>();
                List<CustomPlayer> finalTeamB = new List<CustomPlayer>();

                foreach (var g in teamAGroups) finalTeamA.AddRange(g.Players);
                foreach (var g in teamBGroups) finalTeamB.AddRange(g.Players);

                lock (stateLock)
                {
                    authorizedTeams.Clear();
                    foreach (var p in finalTeamA) authorizedTeams[p.Name] = 1;
                    foreach (var p in finalTeamB) authorizedTeams[p.Name] = 2;
                }

                AssignInGameSquads(finalTeamA, 1, currentMode);
                AssignInGameSquads(finalTeamB, 2, currentMode);

                // --- NEW CHAT ANNOUNCEMENTS ---
                SendGlobalAnnouncement("Match over balancing teams:");
                SendGlobalAnnouncement("Team A team playerscore: " + scoreA);
                SendGlobalAnnouncement("Team B team playerscore: " + scoreB);

                // --- NEW CONSOLE LOGGING ---
                TryLogConsole("Teams successfully balanced!");
                TryLogConsole("Team A PlayerScore: " + scoreA + " | Total Players: " + playersA);
                TryLogConsole("Team B PlayerScore: " + scoreB + " | Total Players: " + playersB);

                lock (stateLock) { isMatchRunning = true; }
            }
            catch (Exception ex) { TryLogConsole("RunBalancer error: " + ex.Message); }
        }

        private void AssignInGameSquads(List<CustomPlayer> teamPlayers, int teamId, string mode)
        {
            if (string.IsNullOrEmpty(mode)) mode = "";
            bool is5v5 = mode.ToLower().Contains("rescue") || mode.ToLower().Contains("crosshair");

            int squadId = 1;
            int countInSquad = 0;

            foreach (var p in teamPlayers)
            {
                if (is5v5)
                {
                    SafeExecuteCommand("procon.protected.send", "admin.movePlayer", p.Name, teamId.ToString(), "1", "true");
                }
                else
                {
                    if (countInSquad >= 5)
                    {
                        squadId++;
                        countInSquad = 0;
                    }
                    SafeExecuteCommand("procon.protected.send", "admin.movePlayer", p.Name, teamId.ToString(), squadId.ToString(), "true");
                    countInSquad++;
                }
            }
        }

        private void SendChat(string playerName, string msg) { SafeExecuteCommand("procon.protected.send", "admin.say", msg, "player", playerName); }
        private void SendGlobalAnnouncement(string msg) { SafeExecuteCommand("procon.protected.send", "admin.say", msg, "all"); }

        // --- Execute Command Overloads ---
        private void SafeExecuteCommand(string service) 
        { 
            try { this.ExecuteCommand(service); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1) 
        { 
            try { this.ExecuteCommand(service, a1); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1, string a2) 
        { 
            try { this.ExecuteCommand(service, a1, a2); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1, string a2, string a3) 
        { 
            try { this.ExecuteCommand(service, a1, a2, a3); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1, string a2, string a3, string a4) 
        { 
            try { this.ExecuteCommand(service, a1, a2, a3, a4); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1, string a2, string a3, string a4, string a5) 
        { 
            try { this.ExecuteCommand(service, a1, a2, a3, a4, a5); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        private void SafeExecuteCommand(string service, string a1, string a2, string a3, string a4, string a5, string a6) 
        { 
            try { this.ExecuteCommand(service, a1, a2, a3, a4, a5, a6); } catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); } 
        }
        
        private void TryLogConsole(string message) { try { this.ExecuteCommand("procon.protected.plugin.console", message); } catch { } }
    }
}
