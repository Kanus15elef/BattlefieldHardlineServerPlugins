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
        public string GetPluginVersion() { return "1.5.1"; }
        public string GetPluginAuthor() { return "Yonatan (kanus15elef)"; }
        public string GetPluginWebsite() { return "localhost"; }
        public string GetPluginDescription() { return "Optimized balancing with persistent Heist round-tracking, exact equal player counts, strict squad preservation, and automatic in-game squad assignments."; }

        // --- Configurable plugin variables (exposed to server admins) ---
        private double scoreWeight = 0.01;   
        private double kdWeight = 10.0;      
        private double killWeight = 5.0;     
        private int inviteExpirySeconds = 60; 
        private bool preserveSquads = true;  
        private int balanceDelaySeconds = 5; 
        private int scoreRequestTimeoutMs = 2000; 

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
                new CPluginVariable("scoreRequestTimeoutMs", typeof(int), scoreRequestTimeoutMs.ToString())
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

            public double KD
            {
                get { return (double)Kills / Math.Max(1, Deaths); }
            }

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

            public CustomSquad()
            {
                Members = new List<CustomPlayer>();
            }

            public int TotalScore(double scoreWeight, double kdWeight, double killWeight)
            {
                int total = 0;
                for (int i = 0; i < Members.Count; i++)
                {
                    total += Members[i].CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                }
                return total;
            }
        }

        public class BalanceGroup
        {
            public List<CustomPlayer> Players { get; set; }

            public BalanceGroup()
            {
                Players = new List<CustomPlayer>();
            }

            public int TotalScore(double scoreWeight, double kdWeight, double killWeight)
            {
                int total = 0;
                for (int i = 0; i < Players.Count; i++)
                {
                    total += Players[i].CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                }
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

        // --- Thread-safe collections and locks ---
        private readonly Dictionary<string, CustomSquad> activeSquads = new Dictionary<string, CustomSquad>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CustomPlayer> trackedPlayers = new Dictionary<string, CustomPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InviteInfo> pendingInvites = new Dictionary<string, InviteInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly object stateLock = new object();
        private readonly Random randomGen = new Random();

        private readonly Dictionary<string, CustomPlayer> matchProfiles = new Dictionary<string, CustomPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ManualResetEventSlim> pendingScoreRequests = new Dictionary<string, ManualResetEventSlim>(StringComparer.OrdinalIgnoreCase);

        // Persistent Heist State Trackers (Fixes round-reset loop bug)
        private bool isMatchRunning = true; 
        private string currentGameMode = "";
        private string currentMapName = "";
        private int persistentHeistRound = 0;
        private Dictionary<string, int> previousRoundScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public CSquadAutoBalancer()
        {
        }

        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            this.RegisterEvents(this.GetType().Name,
                "OnGlobalChat",
                "OnRoundOver",
                "OnPlayerJoin",
                "OnPlayerLeft",
                "OnPlayerKilled",
                "OnListPlayers",
                "OnPlayerTeamChange",
                "OnServerInfo",
                "OnLevelLoaded");
        }

        public void OnPluginEnable()
        {
            SafeExecuteCommand("procon.protected.plugin.console", "Squad Auto Enabled!");
        }

        public void OnPluginDisable() { }

        public override void OnServerInfo(CServerInfo serverInfo)
        {
            if (serverInfo != null && !string.IsNullOrEmpty(serverInfo.GameMode))
            {
                lock (stateLock) { this.currentGameMode = serverInfo.GameMode; }
            }
        }

        // Persistent Round Tracker inside OnLevelLoaded
        public override void OnLevelLoaded(string mapFileName, string GameMode, int roundsPlayed, int roundsTotal)
        {
            lock (stateLock)
            {
                this.currentGameMode = GameMode;

                if (string.Equals(this.currentMapName, mapFileName, StringComparison.OrdinalIgnoreCase))
                {
                    // Same map, round transitioned (e.g., Heist Round 2)
                    this.persistentHeistRound++;
                }
                else
                {
                    // Completely new map: Reset round counter and scores cache
                    this.currentMapName = mapFileName;
                    this.persistentHeistRound = 0;
                    this.previousRoundScores.Clear();
                }
            }
        }

        public override void OnPlayerJoin(string strSoldierName)
        {
            lock (stateLock)
            {
                if (!trackedPlayers.ContainsKey(strSoldierName))
                {
                    trackedPlayers[strSoldierName] = new CustomPlayer { Name = strSoldierName, OverallScore = 0, Kills = 0, Deaths = 0, TeamId = 0 };
                }

                if (!matchProfiles.ContainsKey(strSoldierName))
                {
                    matchProfiles[strSoldierName] = new CustomPlayer { Name = strSoldierName, OverallScore = 0, Kills = 0, Deaths = 0, TeamId = 0 };
                }
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
                        if (activeSquads[player.CurrentSquadName].Members.Count == 0)
                        {
                            activeSquads.Remove(player.CurrentSquadName);
                        }
                    }
                    trackedPlayers.Remove(strSoldierName);
                }

                if (matchProfiles.ContainsKey(strSoldierName))
                {
                    matchProfiles.Remove(strSoldierName);
                }
            }
        }

        public override void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        {
            lock (stateLock)
            {
                foreach (CPlayerInfo p in players)
                {
                    if (!matchProfiles.ContainsKey(p.SoldierName))
                    {
                        matchProfiles[p.SoldierName] = new CustomPlayer { Name = p.SoldierName };
                    }
                    matchProfiles[p.SoldierName].OverallScore = p.Score;
                    matchProfiles[p.SoldierName].Kills = p.Kills;
                    matchProfiles[p.SoldierName].Deaths = p.Deaths;
                    matchProfiles[p.SoldierName].TeamId = p.TeamID;

                    if (!trackedPlayers.ContainsKey(p.SoldierName))
                    {
                        trackedPlayers[p.SoldierName] = new CustomPlayer { Name = p.SoldierName };
                    }
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
                if (!string.IsNullOrEmpty(killerName))
                {
                    if (!trackedPlayers.ContainsKey(killerName)) trackedPlayers[killerName] = new CustomPlayer { Name = killerName };
                    if (!matchProfiles.ContainsKey(killerName)) matchProfiles[killerName] = new CustomPlayer { Name = killerName };
                    
                    trackedPlayers[killerName].Kills++;
                    matchProfiles[killerName].Kills++;
                }
                if (!string.IsNullOrEmpty(victimName))
                {
                    if (!trackedPlayers.ContainsKey(victimName)) trackedPlayers[victimName] = new CustomPlayer { Name = victimName };
                    if (!matchProfiles.ContainsKey(victimName)) matchProfiles[victimName] = new CustomPlayer { Name = victimName };
                    
                    trackedPlayers[victimName].Deaths++;
                    matchProfiles[victimName].Deaths++;
                }
            }
        }

        public override void OnPlayerTeamChange(string soldierName, int teamId, int squadId)
        {
            lock (stateLock)
            {
                if (trackedPlayers.ContainsKey(soldierName)) trackedPlayers[soldierName].TeamId = teamId;
                if (matchProfiles.ContainsKey(soldierName)) matchProfiles[soldierName].TeamId = teamId;
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
                case "!squadcreate":
                    if (args.Length < 2) return;
                    string squadName = strMessage.Substring(args[0].Length).Trim();
                    CreateSquad(speaker, squadName);
                    break;
                case "!squadinvite":
                    if (args.Length < 2) return;
                    CustomPlayer target = FindPlayer(args[1]);
                    if (target != null) InvitePlayer(speaker, target);
                    else SendChat(speaker.Name, "Player not found or ambiguous.");
                    break;
                case "!squadaccept":
                    AcceptInvite(speaker);
                    break;
                case "!squadreject":
                    RejectInvite(speaker);
                    break;
                case "!squadleave":
                    LeaveSquad(speaker);
                    break;
                case "!squadkick":
                    if (args.Length < 2) return;
                    CustomPlayer kickTarget = FindPlayer(args[1]);
                    if (kickTarget != null) KickPlayer(speaker, kickTarget);
                    break;
                case "!squadclose":
                    CloseSquad(speaker);
                    break;
                case "!squadmembers":
                    ShowSquadMembers(speaker);
                    break;
                case "!assist":
                    ExecuteAssistCommand(speaker);
                    break;
                case "!playerscore":
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

                List<CustomPlayer> matches = new List<CustomPlayer>();
                foreach (KeyValuePair<string, CustomPlayer> kvp in trackedPlayers)
                {
                    if (kvp.Key.StartsWith(searchName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(kvp.Value);
                    }
                }

                if (matches.Count == 1) return matches[0];
                return null;
            }
        }

        private void CreateSquad(CustomPlayer creator, string squadName)
        {
            lock (stateLock)
            {
                if (!string.IsNullOrEmpty(creator.CurrentSquadName))
                {
                    SendChat(creator.Name, "You are already in a squad. Type !squadleave to leave your squad.");
                    return;
                }
                if (activeSquads.ContainsKey(squadName))
                {
                    SendChat(creator.Name, squadName + " already taken, try another name.");
                    return;
                }

                CustomSquad newSquad = new CustomSquad { Name = squadName, LeaderName = creator.Name };
                newSquad.Members.Add(creator);
                activeSquads[squadName] = newSquad;
                creator.CurrentSquadName = squadName;
            }

            SendChat(creator.Name, "You have created a squad named " + squadName + ". Invite with !squadinvite <name>.");
            SendGlobalAnnouncement("Squad '" + squadName + "' has been created by " + creator.Name + "!");
        }

        private void InvitePlayer(CustomPlayer inviter, CustomPlayer target)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(inviter.CurrentSquadName) || !activeSquads.ContainsKey(inviter.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[inviter.CurrentSquadName];
                if (squad.LeaderName != inviter.Name) return;
                if (squad.Members.Count >= 5) { SendChat(inviter.Name, "Squad is full."); return; }

                pendingInvites[target.Name] = new InviteInfo(squad.Name, DateTime.UtcNow.AddSeconds(inviteExpirySeconds));
            }

            SendChat(inviter.Name, "You have invited " + target.Name + " to your squad.");
            SendChat(target.Name, "You have been invited to " + inviter.CurrentSquadName + ". Type !squadaccept to join or !squadreject to reject. Invite expires in " + inviteExpirySeconds + "s.");
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
                    SendChat(player.Name, "You have joined the squad " + squadName + ". Type !squadleave to leave.");
                }
                else
                {
                    SendChat(player.Name, "Squad no longer exists.");
                }
            }
        }

        private void RejectInvite(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (!pendingInvites.ContainsKey(player.Name)) { SendChat(player.Name, "No pending invite."); return; }
                string squadName = pendingInvites[player.Name].SquadName;
                pendingInvites.Remove(player.Name);
                SendChat(player.Name, "You have rejected joining the squad " + squadName);
            }
        }

        private void LeaveSquad(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(player.CurrentSquadName) || !activeSquads.ContainsKey(player.CurrentSquadName)) { SendChat(player.Name, "You are not in a squad."); return; }

                string squadName = player.CurrentSquadName;
                CustomSquad squad = activeSquads[squadName];
                squad.Members.Remove(player);
                player.CurrentSquadName = null;
                SendChat(player.Name, "You have left the squad " + squadName);

                if (squad.Members.Count == 0) activeSquads.Remove(squadName);
                else if (squad.LeaderName == player.Name) squad.LeaderName = squad.Members[0].Name;
            }
        }

        private void KickPlayer(CustomPlayer leader, CustomPlayer target)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(leader.CurrentSquadName) || !activeSquads.ContainsKey(leader.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                if (squad.LeaderName != leader.Name || target.CurrentSquadName != squad.Name) return;

                squad.Members.Remove(target);
                target.CurrentSquadName = null;
                SendChat(leader.Name, target.Name + " has been kicked from " + squad.Name);
            }
        }

        private void CloseSquad(CustomPlayer leader)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(leader.CurrentSquadName) || !activeSquads.ContainsKey(leader.CurrentSquadName)) return;
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                if (squad.LeaderName != leader.Name) return;

                string squadName = squad.Name;
                for (int i = 0; i < squad.Members.Count; i++)
                {
                    squad.Members[i].CurrentSquadName = null;
                    SendChat(squad.Members[i].Name, "Squad " + squadName + " has been closed");
                }
                activeSquads.Remove(squadName);
            }
        }

        private void ShowSquadMembers(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(player.CurrentSquadName) || !activeSquads.ContainsKey(player.CurrentSquadName))
                {
                    SendChat(player.Name, "You are not currently in a squad.");
                    return;
                }

                CustomSquad squad = activeSquads[player.CurrentSquadName];
                string memberList = "";
                for (int i = 0; i < squad.Members.Count; i++)
                {
                    memberList += squad.Members[i].Name;
                    if (i < squad.Members.Count - 1) memberList += ", ";
                }
                SendChat(player.Name, "Squad " + squad.Name + " members: " + memberList);
            }
        }

        private void ExecuteAssistCommand(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (!isMatchRunning)
                {
                    SendChat(player.Name, "The !assist command is disabled during the end-of-round balancing phase.");
                    return;
                }
            }

            if (player.TeamId != 1 && player.TeamId != 2) return;

            int team1Count = 0, team2Count = 0;
            int team1Score = 0, team2Score = 0;

            lock (stateLock)
            {
                foreach (KeyValuePair<string, CustomPlayer> kvp in trackedPlayers)
                {
                    if (kvp.Value.TeamId == 1)
                    {
                        team1Count++;
                        team1Score += kvp.Value.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                    }
                    else if (kvp.Value.TeamId == 2)
                    {
                        team2Count++;
                        team2Score += kvp.Value.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                    }
                }
            }

            int teamACount = (player.TeamId == 1) ? team1Count : team2Count;
            int teamBCount = (player.TeamId == 1) ? team2Count : team1Count;
            
            int teamAScore = (player.TeamId == 1) ? team1Score : team2Score;
            int teamBScore = (player.TeamId == 1) ? team2Score : team1Score;

            bool element1 = teamACount <= teamBCount; 
            bool element2 = teamBScore > teamAScore; 

            if (element1 || element2)
            {
                SendGlobalAnnouncement("Can't assist " + player.Name + " to the other team it will cause unbalanced teams");
            }
            else
            {
                int newTeamId = (player.TeamId == 1) ? 2 : 1;
                SafeExecuteCommand("procon.protected.send", "admin.movePlayer", player.Name, newTeamId.ToString(), "0", "true");
                SendGlobalAnnouncement("Assisting " + player.Name + " to the other team. Thank you for assisting!");
                
                lock (stateLock) { player.TeamId = newTeamId; }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(1500); 
                    List<CustomPlayer> newTeamPlayers = new List<CustomPlayer>();
                    string currentMode;
                    lock (stateLock)
                    {
                        currentMode = this.currentGameMode;
                        foreach (var kvp in trackedPlayers)
                        {
                            if (kvp.Value.TeamId == newTeamId) newTeamPlayers.Add(kvp.Value);
                        }
                    }
                    AssignInGameSquads(newTeamPlayers, newTeamId, currentMode);
                });
            }
        }

        // --- Core Balancing Hook with Persistent Heist Tracking ---
        public override void OnRoundOver(int winningTeamId)
        {
            lock (stateLock) { isMatchRunning = false; }

            bool isHeist = !string.IsNullOrEmpty(currentGameMode) && 
                           currentGameMode.IndexOf("heist", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           currentGameMode.IndexOf("squad", StringComparison.OrdinalIgnoreCase) < 0;

            if (isHeist)
            {
                int activeRound = 0;
                lock (stateLock) { activeRound = persistentHeistRound; }

                // If it's Round 1, save scores and skip balance
                if (activeRound == 0)
                {
                    SendGlobalAnnouncement("Heist Round 1 complete. Scores saved. Teams will balance after Round 2.");
                    
                    List<CustomPlayer> lobbySnapshot;
                    lock (stateLock) { lobbySnapshot = trackedPlayers.Values.Select(p => p.ShallowClone()).ToList(); }
                    
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try 
                        {
                            Thread.Sleep(Math.Max(0, balanceDelaySeconds) * 1000);
                            RequestAllPlayerScores(lobbySnapshot);
                            
                            lock (stateLock) 
                            {
                                foreach (var p in lobbySnapshot) 
                                {
                                    if (matchProfiles.ContainsKey(p.Name)) 
                                    {
                                        int calc = matchProfiles[p.Name].CalculatedPlayerScore(scoreWeight, kdWeight, killWeight);
                                        previousRoundScores[p.Name] = calc;
                                    }
                                }
                                isMatchRunning = true; 
                                matchProfiles.Clear();
                            }
                        }
                        catch (Exception ex) { TryLogConsole("Round 1 save error: " + ex.Message); }
                    });
                    
                    return; 
                }
                else
                {
                    SendGlobalAnnouncement("Heist Match over! Combining scores and balancing teams for the next map...");
                }
            }
            else 
            {
                SendGlobalAnnouncement("Match over! Shuffling and balancing teams in " + balanceDelaySeconds + " seconds...");
            }

            List<CustomPlayer> finalLobbySnapshot;
            List<CustomSquad> finalSquadsSnapshot;
            lock (stateLock)
            {
                finalLobbySnapshot = trackedPlayers.Values.Select(p => p.ShallowClone()).ToList();
                finalSquadsSnapshot = new List<CustomSquad>();
                foreach (KeyValuePair<string, CustomSquad> kvp in activeSquads)
                {
                    CustomSquad copy = new CustomSquad();
                    copy.Name = kvp.Value.Name;
                    copy.LeaderName = kvp.Value.LeaderName;
                    copy.Members = kvp.Value.Members.Select(m => m.ShallowClone()).ToList();
                    finalSquadsSnapshot.Add(copy);
                }
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Thread.Sleep(Math.Max(0, balanceDelaySeconds) * 1000);
                    RequestAllPlayerScores(finalLobbySnapshot);
                    RunBalancer(finalLobbySnapshot, finalSquadsSnapshot);

                    lock (stateLock)
                    {
                        matchProfiles.Clear();
                    }
                }
                catch (Exception ex)
                {
                    TryLogConsole("Balancer thread error: " + ex.Message);
                }
            });
        }

        private void RequestAllPlayerScores(List<CustomPlayer> lobbySnapshot)
        {
            if (lobbySnapshot == null || lobbySnapshot.Count == 0) return;

            lock (pendingScoreRequests)
            {
                pendingScoreRequests.Clear();
                foreach (var p in lobbySnapshot)
                {
                    if (!pendingScoreRequests.ContainsKey(p.Name))
                    {
                        pendingScoreRequests[p.Name] = new ManualResetEventSlim(false);
                    }
                }
            }

            foreach (var p in lobbySnapshot)
            {
                SafeExecuteCommand("procon.protected.send", "server.getPlayerStats", p.Name);
            }

            int timeout = Math.Max(0, scoreRequestTimeoutMs);
            DateTime waitStart = DateTime.UtcNow;
            foreach (var kvp in pendingScoreRequests.ToList())
            {
                var ev = kvp.Value;
                int elapsed = (int)(DateTime.UtcNow - waitStart).TotalMilliseconds;
                int remaining = Math.Max(0, timeout - elapsed);
                if (remaining <= 0) break;
                try { ev.Wait(remaining); } catch { }
            }

            lock (pendingScoreRequests)
            {
                foreach (var ev in pendingScoreRequests.Values) { try { ev.Dispose(); } catch { } }
                pendingScoreRequests.Clear();
            }
        }

        private void RunBalancer(List<CustomPlayer> lobbySnapshot, List<CustomSquad> squadsSnapshot)
        {
            try
            {
                string currentMode = "";
                lock (stateLock)
                {
                    currentMode = this.currentGameMode;
                    
                    for (int i = 0; i < lobbySnapshot.Count; i++)
                    {
                        var snap = lobbySnapshot[i];
                        if (matchProfiles.ContainsKey(snap.Name))
                        {
                            var live = matchProfiles[snap.Name];
                            snap.OverallScore = live.OverallScore;
                            snap.Kills = live.Kills;
                            snap.Deaths = live.Deaths;
                        }
                        if (previousRoundScores.ContainsKey(snap.Name))
                        {
                            snap.PreviousRoundCalculatedScore = previousRoundScores[snap.Name];
                        }
                    }

                    foreach (var s in squadsSnapshot)
                    {
                        for (int m = 0; m < s.Members.Count; m++)
                        {
                            var mem = s.Members[m];
                            if (matchProfiles.ContainsKey(mem.Name))
                            {
                                var live = matchProfiles[mem.Name];
                                mem.OverallScore = live.OverallScore;
                                mem.Kills = live.Kills;
                                mem.Deaths = live.Deaths;
                            }
                            if (previousRoundScores.ContainsKey(mem.Name))
                            {
                                mem.PreviousRoundCalculatedScore = previousRoundScores[mem.Name];
                            }
                        }
                    }
                }

                if (lobbySnapshot == null || lobbySnapshot.Count == 0) return;

                int hardLimit = (int)Math.Floor(lobbySnapshot.Count / 2.0);

                List<BalanceGroup> groups = new List<BalanceGroup>();
                HashSet<string> groupedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (preserveSquads)
                {
                    for (int sIndex = 0; sIndex < squadsSnapshot.Count; sIndex++)
                    {
                        CustomSquad s = squadsSnapshot[sIndex];
                        if (s.Members != null && s.Members.Count > 0)
                        {
                            BalanceGroup bg = new BalanceGroup();
                            for (int mIndex = 0; mIndex < s.Members.Count; mIndex++)
                            {
                                bg.Players.Add(s.Members[mIndex]);
                                groupedNames.Add(s.Members[mIndex].Name);
                            }
                            groups.Add(bg);
                        }
                    }
                }

                for (int pIndex = 0; pIndex < lobbySnapshot.Count; pIndex++)
                {
                    CustomPlayer p = lobbySnapshot[pIndex];
                    if (!groupedNames.Contains(p.Name))
                    {
                        BalanceGroup bg = new BalanceGroup();
                        bg.Players.Add(p);
                        groups.Add(bg);
                    }
                }

                groups = groups.OrderByDescending(g => g.Size).ThenBy(g => randomGen.Next()).ToList();

                List<BalanceGroup> teamAGroups = new List<BalanceGroup>();
                List<BalanceGroup> teamBGroups = new List<BalanceGroup>();
                int playersA = 0, playersB = 0;
                int scoreA = 0, scoreB = 0;

                for (int gIndex = 0; gIndex < groups.Count; gIndex++)
                {
                    BalanceGroup g = groups[gIndex];
                    int gSize = g.Size;
                    int gScore = g.TotalScore(scoreWeight, kdWeight, killWeight);

                    bool canA = (playersA + gSize <= hardLimit);
                    bool canB = (playersB + gSize <= hardLimit);

                    if (canA && canB)
                    {
                        if (playersA < playersB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                        else if (playersB < playersA) { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                        else
                        {
                            if (scoreA <= scoreB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                            else { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                        }
                    }
                    else if (canA)
                    {
                        teamAGroups.Add(g); playersA += gSize; scoreA += gScore;
                    }
                    else if (canB)
                    {
                        teamBGroups.Add(g); playersB += gSize; scoreB += gScore;
                    }
                    else
                    {
                        if (playersA <= playersB) { teamAGroups.Add(g); playersA += gSize; scoreA += gScore; }
                        else { teamBGroups.Add(g); playersB += gSize; scoreB += gScore; }
                    }
                }

                bool improved = true;
                int iterations = 0;
                while (improved && iterations < 100)
                {
                    improved = false;
                    iterations++;
                    int currentDiff = Math.Abs(scoreA - scoreB);

                    for (int i = 0; i < teamAGroups.Count; i++)
                    {
                        for (int j = 0; j < teamBGroups.Count; j++)
                        {
                            BalanceGroup a = teamAGroups[i];
                            BalanceGroup b = teamBGroups[j];

                            if (a.Size != b.Size) continue;

                            int newScoreA = scoreA - a.TotalScore(scoreWeight, kdWeight, killWeight) + b.TotalScore(scoreWeight, kdWeight, killWeight);
                            int newScoreB = scoreB - b.TotalScore(scoreWeight, kdWeight, killWeight) + a.TotalScore(scoreWeight, kdWeight, killWeight);
                            int newDiff = Math.Abs(newScoreA - newScoreB);

                            if (newDiff < currentDiff)
                            {
                                teamAGroups[i] = b;
                                teamBGroups[j] = a;
                                scoreA = newScoreA;
                                scoreB = newScoreB;
                                improved = true;
                                goto NextSwapPass;
                            }
                        }
                    }
                NextSwapPass:
                    continue;
                }

                List<CustomPlayer> finalTeamA = new List<CustomPlayer>();
                for (int i = 0; i < teamAGroups.Count; i++)
                {
                    finalTeamA.AddRange(teamAGroups[i].Players);
                    for (int j = 0; j < teamAGroups[i].Players.Count; j++)
                    {
                        lock (stateLock) { if (trackedPlayers.ContainsKey(teamAGroups[i].Players[j].Name)) trackedPlayers[teamAGroups[i].Players[j].Name].TeamId = 1; }
                    }
                }

                List<CustomPlayer> finalTeamB = new List<CustomPlayer>();
                for (int i = 0; i < teamBGroups.Count; i++)
                {
                    finalTeamB.AddRange(teamBGroups[i].Players);
                    for (int j = 0; j < teamBGroups[i].Players.Count; j++)
                    {
                        lock (stateLock) { if (trackedPlayers.ContainsKey(teamBGroups[i].Players[j].Name)) trackedPlayers[teamBGroups[i].Players[j].Name].TeamId = 2; }
                    }
                }

                AssignInGameSquads(finalTeamA, 1, currentMode);
                AssignInGameSquads(finalTeamB, 2, currentMode);

                SendGlobalAnnouncement("Teams balanced! Final Player Counts - Team A: " + playersA + " | Team B: " + playersB + " | Scores - Team A: " + scoreA + " | Team B: " + scoreB);

                lock (stateLock)
                {
                    foreach (KeyValuePair<string, CustomPlayer> kvp in trackedPlayers)
                    {
                        kvp.Value.OverallScore = 0;
                        kvp.Value.Kills = 0;
                        kvp.Value.Deaths = 0;
                    }
                    
                    previousRoundScores.Clear(); 
                    isMatchRunning = true; 
                }
            }
            catch (Exception ex)
            {
                TryLogConsole("RunBalancer error: " + ex.Message);
            }
        }

        private void AssignInGameSquads(List<CustomPlayer> teamPlayers, int teamId, string mode)
        {
            if (string.IsNullOrEmpty(mode)) mode = "";
            string m = mode.ToLower();
            
            bool is5v5 = m.Contains("rescue") || m.Contains("crosshair") || m.Contains("squadheist");

            if (is5v5)
            {
                foreach(var p in teamPlayers) 
                {
                    SafeExecuteCommand("procon.protected.send", "admin.movePlayer", p.Name, teamId.ToString(), "1", "true");
                }
                return;
            }

            var squads = new Dictionary<string, List<CustomPlayer>>(StringComparer.OrdinalIgnoreCase);
            var solos = new List<CustomPlayer>();

            foreach(var p in teamPlayers) 
            {
                if (!string.IsNullOrEmpty(p.CurrentSquadName)) 
                {
                    if (!squads.ContainsKey(p.CurrentSquadName)) squads[p.CurrentSquadName] = new List<CustomPlayer>();
                    squads[p.CurrentSquadName].Add(p);
                } 
                else 
                {
                    solos.Add(p);
                }
            }

            int currentSquadId = 1;

            foreach(var kvp in squads) 
            {
                var squadMembers = kvp.Value;
                int currentSquadCount = 0;

                foreach(var p in squadMembers) 
                {
                    if (currentSquadCount >= 5) 
                    {
                        currentSquadId++;
                        currentSquadCount = 0;
                    }
                    SafeExecuteCommand("procon.protected.send", "admin.movePlayer", p.Name, teamId.ToString(), currentSquadId.ToString(), "true");
                    currentSquadCount++;
                }
                
                while (currentSquadCount < 5 && solos.Count > 0) 
                {
                    var soloPlayer = solos[0];
                    solos.RemoveAt(0);
                    SafeExecuteCommand("procon.protected.send", "admin.movePlayer", soloPlayer.Name, teamId.ToString(), currentSquadId.ToString(), "true");
                    currentSquadCount++;
                }
                
                currentSquadId++;
            }

            int soloCount = 0;
            foreach(var p in solos) 
            {
                if (soloCount >= 5) 
                {
                    currentSquadId++;
                    soloCount = 0;
                }
                SafeExecuteCommand("procon.protected.send", "admin.movePlayer", p.Name, teamId.ToString(), currentSquadId.ToString(), "true");
                soloCount++;
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

        private void SendChat(string playerName, string msg)
        {
            SafeExecuteCommand("procon.protected.send", "admin.say", msg, "player", playerName);
        }

        private void SendGlobalAnnouncement(string msg)
        {
            SafeExecuteCommand("procon.protected.send", "admin.say", msg, "all");
        }

        private void SafeExecuteCommand(string service)
        {
            try { this.ExecuteCommand(service); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void SafeExecuteCommand(string service, string a1)
        {
            try { this.ExecuteCommand(service, a1); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void SafeExecuteCommand(string service, string a1, string a2)
        {
            try { this.ExecuteCommand(service, a1, a2); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void SafeExecuteCommand(string service, string a1, string a2, string a3)
        {
            try { this.ExecuteCommand(service, a1, a2, a3); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void SafeExecuteCommand(string service, string a1, string a2, string a3, string a4)
        {
            try { this.ExecuteCommand(service, a1, a2, a3, a4); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void SafeExecuteCommand(string service, string a1, string a2, string a3, string a4, string a5)
        {
            try { this.ExecuteCommand(service, a1, a2, a3, a4, a5); }
            catch (Exception ex) { TryLogConsole("SquadAutoBalancer Error: " + ex.Message); }
        }

        private void TryLogConsole(string message)
        {
            try { this.ExecuteCommand("procon.protected.plugin.console", message); } catch { }
        }
    }
}