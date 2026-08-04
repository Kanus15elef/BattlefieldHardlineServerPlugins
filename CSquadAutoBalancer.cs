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
        public string GetPluginDescription() { return "Optimized match-end balancing with squad preservation, advanced chat logic, and universal chat command support."; }

        // --- Configurable plugin variables ---
        private double scoreWeight = 0.01;   
        private double kdWeight = 10.0;      
        private double killWeight = 5.0;     
        private int inviteExpirySeconds = 60; 
        private bool preserveSquads = true;  
        private int balanceDelaySeconds = 5; 
        private int scoreRequestTimeoutMs = 2000; 
        private int assistEnableDelayMinutes = 5;

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
                new CPluginVariable("assistEnableDelayMinutes", typeof(int), assistEnableDelayMinutes.ToString())
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
            public string InviterName;
            public DateTime ExpiresUtc;
            public InviteInfo(string squadName, string inviterName, DateTime expiresUtc)
            {
                this.SquadName = squadName;
                this.InviterName = inviterName;
                this.ExpiresUtc = expiresUtc;
            }
        }

        // --- State Collections ---
        private readonly Dictionary<string, CustomSquad> activeSquads = new Dictionary<string, CustomSquad>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CustomPlayer> trackedPlayers = new Dictionary<string, CustomPlayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InviteInfo> pendingInvites = new Dictionary<string, InviteInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> rejectCooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
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

        public CSquadAutoBalancer() { }

        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            this.RegisterEvents(this.GetType().Name,
                "OnGlobalChat", "OnTeamChat", "OnSquadChat", "OnRoundOver", "OnPlayerJoin", "OnPlayerLeft", 
                "OnPlayerKilled", "OnListPlayers", "OnPlayerTeamChange", 
                "OnServerInfo", "OnLevelLoaded");
        }

        public void OnPluginEnable() { SafeExecuteCommand("procon.protected.plugin.console", "Squad Auto Balancer 2.0.2 Enabled!"); }
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
                    ProcessPlayerLeavingSquad(player, true); 
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

        // --- Universal Chat Handlers (All, Team, Squad) ---
        public override void OnGlobalChat(string strSpeaker, string strMessage) { ProcessPlayerCommand(strSpeaker, strMessage); }
        public override void OnTeamChat(string strSpeaker, string strMessage, int iTeamID) { ProcessPlayerCommand(strSpeaker, strMessage); }
        public override void OnSquadChat(string strSpeaker, string strMessage, int iTeamID, int iSquadID) { ProcessPlayerCommand(strSpeaker, strMessage); }

        private void ProcessPlayerCommand(string strSpeaker, string strMessage)
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
            string parameter = args.Length >= 2 ? strMessage.Substring(args[0].Length).Trim() : "";

            bool inSquad = !string.IsNullOrEmpty(speaker.CurrentSquadName) && activeSquads.ContainsKey(speaker.CurrentSquadName);
            bool isLeader = false;
            
            if (inSquad)
            {
                isLeader = activeSquads[speaker.CurrentSquadName].LeaderName == speaker.Name;
            }

            switch (command)
            {
                case "!squad":
                    SendChat(speaker.Name, "Squad commands are: !squadcreate, !squadinvite, !squadleave, !squadaccept, !squadreject, !squadkick, !squadclose, !squadmembers");
                    break;

                case "!squadcreate":
                    if (inSquad)
                    {
                        SendChat(speaker.Name, "Can't create a squad while in a squad ! to create a squad please leave your squad first and try again.");
                    }
                    else if (string.IsNullOrEmpty(parameter))
                    {
                        SendChat(speaker.Name, "Please add a name for your squad !");
                    }
                    else
                    {
                        CreateSquad(speaker, parameter);
                    }
                    break;

                case "!squadaccept":
                case "!squadreject":
                    if (inSquad)
                    {
                        SendChat(speaker.Name, "Can't accept or reject invitations while in a squad, please leave your squad to get invitations to other squads !");
                    }
                    else
                    {
                        if (command == "!squadaccept") AcceptInvite(speaker);
                        else RejectInvite(speaker);
                    }
                    break;

                case "!squadinvite":
                case "!squadleave":
                case "!squadkick":
                case "!squadclose":
                case "!squadmembers":
                    if (!inSquad)
                    {
                        SendChat(speaker.Name, "Can't use these commands while not in a squad !");
                        return;
                    }

                    if (command == "!squadmembers")
                    {
                        ShowSquadMembers(speaker);
                    }
                    else if (command == "!squadleave")
                    {
                        ProcessPlayerLeavingSquad(speaker, false);
                    }
                    else
                    {
                        if (!isLeader)
                        {
                            SendChat(speaker.Name, "Can't use this command you are not the squad leader !");
                            return;
                        }

                        if (command == "!squadinvite")
                        {
                            if (string.IsNullOrEmpty(parameter)) SendChat(speaker.Name, "Please add a player name to invite !");
                            else InvitePlayer(speaker, parameter);
                        }
                        else if (command == "!squadkick")
                        {
                            if (string.IsNullOrEmpty(parameter)) SendChat(speaker.Name, "Please add a name of the player you want to kick from your squad!");
                            else KickPlayer(speaker, parameter);
                        }
                        else if (command == "!squadclose")
                        {
                            CloseSquad(speaker);
                        }
                    }
                    break;

                case "!assist": ExecuteAssistCommand(speaker); break;
                case "!stats":
                case "!playerscore":
                    ShowPlayerScoreByName(speaker, string.IsNullOrEmpty(parameter) ? speaker.Name : parameter);
                    break;
            }
        }

        private CustomPlayer FindPlayer(string searchName, out int matchCount)
        {
            matchCount = 0;
            lock (stateLock)
            {
                CustomPlayer exact;
                if (trackedPlayers.TryGetValue(searchName, out exact)) 
                {
                    matchCount = 1;
                    return exact;
                }
                var matches = trackedPlayers.Where(kvp => kvp.Key.StartsWith(searchName, StringComparison.OrdinalIgnoreCase)).ToList();
                matchCount = matches.Count;
                return matches.Count == 1 ? matches[0].Value : null;
            }
        }

        private void CreateSquad(CustomPlayer creator, string squadName)
        {
            lock (stateLock)
            {
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

            SendChat(creator.Name, "You have created a squad named " + squadName + " !");
            SendChat(creator.Name, "To invite players to your squad please type !squadinvite <name>");
            SendGlobalAnnouncement(creator.Name + " has created a squad named " + squadName + " !");
        }

        private void InvitePlayer(CustomPlayer inviter, string targetNameArg)
        {
            int matches;
            CustomPlayer target = FindPlayer(targetNameArg, out matches);

            if (matches == 0)
            {
                SendChat(inviter.Name, "Player has not found please try again !");
                return;
            }
            if (matches > 1)
            {
                SendChat(inviter.Name, "There are one or more players with this exact combination of letters please write their full name to invite them to your squad !");
                return;
            }

            lock (stateLock)
            {
                CustomSquad squad = activeSquads[inviter.CurrentSquadName];
                
                string rejectKey = inviter.Name + "_" + target.Name;
                if (rejectCooldowns.ContainsKey(rejectKey) && rejectCooldowns[rejectKey] > DateTime.UtcNow)
                {
                    SendChat(inviter.Name, "Can't invite " + target.Name + " to your squad since he rejected invitation please wait 60 seconds !");
                    return;
                }

                pendingInvites[target.Name] = new InviteInfo(squad.Name, inviter.Name, DateTime.UtcNow.AddSeconds(inviteExpirySeconds));
            }

            SendChat(target.Name, "You have been invited to squad " + activeSquads[inviter.CurrentSquadName].Name + " !");
            SendChat(target.Name, "To accept type !squadaccept");
            SendChat(target.Name, "To reject type !squadreject");
            SendChat(target.Name, "Invitation will be expired after 60 seconds.");
        }

        private void AcceptInvite(CustomPlayer player)
        {
            string squadName = null;
            lock (stateLock)
            {
                if (!pendingInvites.ContainsKey(player.Name)) 
                { 
                    SendChat(player.Name, "No invitation request is active for you to join a squad !"); 
                    return; 
                }
                
                InviteInfo invite = pendingInvites[player.Name];
                if (invite.ExpiresUtc < DateTime.UtcNow) 
                { 
                    pendingInvites.Remove(player.Name); 
                    SendChat(player.Name, "No invitation request is active for you to join a squad !"); 
                    return; 
                }
                
                squadName = invite.SquadName;
                pendingInvites.Remove(player.Name);

                if (activeSquads.ContainsKey(squadName))
                {
                    activeSquads[squadName].Members.Add(player);
                    player.CurrentSquadName = squadName;
                }
                else
                {
                    return;
                }
            }

            SendChat(player.Name, "You have joined squad " + squadName + " !");
            
            string membersList = "";
            lock (stateLock) { membersList = string.Join(", ", activeSquads[squadName].Members.Select(m => m.Name)); }
            
            SendChat(player.Name, "Squad " + squadName + " members are: " + membersList);
            
            lock (stateLock)
            {
                SendSquadChat(activeSquads[squadName], player.Name + " has joined your squad !", player.Name);
            }
        }

        private void RejectInvite(CustomPlayer player)
        {
            lock (stateLock)
            {
                if (!pendingInvites.ContainsKey(player.Name)) 
                { 
                    SendChat(player.Name, "No invitation request is active for you to join a squad !"); 
                    return; 
                }
                
                InviteInfo invite = pendingInvites[player.Name];
                if (invite.ExpiresUtc < DateTime.UtcNow) 
                { 
                    pendingInvites.Remove(player.Name); 
                    SendChat(player.Name, "No invitation request is active for you to join a squad !"); 
                    return; 
                }

                string squadName = invite.SquadName;
                string leaderName = invite.InviterName;
                
                pendingInvites.Remove(player.Name);
                
                SendChat(player.Name, "You have rejected an invitation from squad " + squadName + " !");
                SendChat(leaderName, player.Name + " has rejected an invitation for your squad you will be able to invite him again after 60 seconds !");
                
                rejectCooldowns[leaderName + "_" + player.Name] = DateTime.UtcNow.AddSeconds(60);
            }
        }

        private void ProcessPlayerLeavingSquad(CustomPlayer player, bool isDisconnect)
        {
            lock (stateLock)
            {
                if (string.IsNullOrEmpty(player.CurrentSquadName) || !activeSquads.ContainsKey(player.CurrentSquadName)) return;

                CustomSquad squad = activeSquads[player.CurrentSquadName];
                string squadName = squad.Name;
                bool isLeader = squad.LeaderName == player.Name;

                squad.Members.Remove(player);
                player.CurrentSquadName = null;

                if (isLeader)
                {
                    if (squad.Members.Count == 0)
                    {
                        if (!isDisconnect) SendChat(player.Name, "You have left your squad and your squad has been disbanded !");
                        activeSquads.Remove(squadName);
                    }
                    else
                    {
                        CustomPlayer oldest = squad.Members[0]; 
                        squad.LeaderName = oldest.Name;

                        if (!isDisconnect) SendChat(player.Name, "You have left your squad transferring leadership to " + oldest.Name + " !");
                        SendSquadChat(squad, player.Name + " has left the squad transferring squad leader role to " + oldest.Name + " !");
                    }
                }
                else
                {
                    if (!isDisconnect) SendChat(player.Name, "You have left squad " + squadName + " !");
                    SendSquadChat(squad, player.Name + " have left your squad !");
                    
                    if (squad.Members.Count > 0)
                    {
                        string memberList = string.Join(", ", squad.Members.Select(m => m.Name));
                        SendSquadChat(squad, "Squad members are now: " + memberList);
                    }
                    else
                    {
                        activeSquads.Remove(squadName);
                    }
                }
            }
        }

        private void KickPlayer(CustomPlayer leader, string targetNameArg)
        {
            int matches;
            CustomPlayer target = FindPlayer(targetNameArg, out matches);

            if (target == null || matches != 1) return; 

            lock (stateLock)
            {
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                if (target.CurrentSquadName != squad.Name) return;

                squad.Members.Remove(target);
                target.CurrentSquadName = null;

                SendChat(target.Name, "You have been kicked from squad " + squad.Name + "!");
                SendSquadChat(squad, target.Name + " have been kicked from your squad !");
            }
        }

        private void CloseSquad(CustomPlayer leader)
        {
            lock (stateLock)
            {
                CustomSquad squad = activeSquads[leader.CurrentSquadName];
                SendSquadChat(squad, "Squad " + squad.Name + " has been disbanded you are no longer in a squad !");
                
                foreach (var m in squad.Members) m.CurrentSquadName = null;
                activeSquads.Remove(squad.Name);
            }
        }

        private void ShowSquadMembers(CustomPlayer player)
        {
            lock (stateLock)
            {
                CustomSquad squad = activeSquads[player.CurrentSquadName];
                string membersList = string.Join(", ", squad.Members.Select(m => m.Name));
                SendChat(player.Name, "Squad " + squad.Name + " members are: " + membersList);
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

            SendChat(requester.Name, profile.Name + " stats are:");
            SendChat(requester.Name, "K/D: " + profile.KD.ToString("F2"));
            SendChat(requester.Name, "Kills: " + profile.Kills);
            SendChat(requester.Name, "Score: " + profile.OverallScore);
            SendChat(requester.Name, "Overall PlayerScore: " + profile.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight));
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
                int scoreABefore = 0;
                int scoreBBefore = 0;

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

                    scoreABefore = lobbySnapshot.Where(p => p.TeamId == 1).Sum(p => p.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight));
                    scoreBBefore = lobbySnapshot.Where(p => p.TeamId == 2).Sum(p => p.CalculatedPlayerScore(scoreWeight, kdWeight, killWeight));
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

                SendGlobalAnnouncement("Match over balancing teams:");
                SendGlobalAnnouncement("Team A team playerscore: " + scoreABefore + " ---> " + scoreA);
                SendGlobalAnnouncement("Team B team playerscore: " + scoreBBefore + " ---> " + scoreB);

                TryLogConsole("Teams successfully balanced!");
                TryLogConsole("Team A PlayerScore Before: " + scoreABefore + " -> After: " + scoreA + " | Total Players: " + playersA);
                TryLogConsole("Team B PlayerScore Before: " + scoreBBefore + " -> After: " + scoreB + " | Total Players: " + playersB);

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
        
        private void SendSquadChat(CustomSquad squad, string msg, string excludePlayer = null)
        {
            foreach (var member in squad.Members)
            {
                if (excludePlayer != null && member.Name.Equals(excludePlayer, StringComparison.OrdinalIgnoreCase)) continue;
                SendChat(member.Name, msg);
            }
        }

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