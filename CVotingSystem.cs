using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using PRoCon.Core;
using PRoCon.Core.Plugin;
using PRoCon.Core.Players;

namespace PRoConEvents
{
    public class CVotingSystem : PRoConPluginAPI, IPRoConPluginInterface
    {
        public enum enumYesNo
        {
            Yes,
            No
        }

        #region Plugin Variables & States

        private enum VotingPhase
        {
            Idle,
            MapVoting,
            MapVotingEnded,
            GamemodeVoting,
            GamemodeVotingEnded
        }

        private VotingPhase currentPhase = VotingPhase.Idle;
        private readonly object syncLock = new object();
        private Random rnd = new Random();

        // Plugin Console Setting
        private enumYesNo proconLiveUpdate = enumYesNo.Yes;

        // Timers
        private Timer roundStartDelayTimer;
        private Timer votingPeriodicTimer;
        private Timer phaseTransitionTimer;

        // Player Tracking
        private int currentPlayerCount = 0;
        private int minimumPlayersToVote = 4;
        private bool isWaitingForPlayers = false;
        private DateTime lastThresholdLogTime = DateTime.MinValue;

        // Loop & Round Tracker
        private string previousLevelName = string.Empty;
        private int currentMapLoadSequenceCount = 0;
        private int currentRoundsPlayed = 0;

        // Voting Data
        private List<string> activeMapPool = new List<string>();
        private List<string> activeGamemodePool = new List<string>();

        private Dictionary<string, string> mapNominations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> gamemodeNominations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, int> mapVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> gamemodeVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> playerCurrentMapVote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> playerCurrentGamemodeVote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string winningMap = string.Empty;
        private string winningGamemode = string.Empty;
        private int mapVotingIntervalCount = 0;
        private int gamemodeVotingIntervalCount = 0;
        
        // Map Queue & Injection Flags
        private bool isNextMapQueued = false;
        
        // Multi-Round Memory Tray & Match Tracking
        private bool isWaitingForRoundTwoInjection = false;
        private string storedRoundTwoMap = string.Empty;
        private string storedRoundTwoMode = string.Empty;
        private bool hasVotedThisMatch = false;

        private string currentActiveMapInternalName = string.Empty;
        private string currentActiveGamemodeInternalName = string.Empty;
        private string currentActiveMapFriendlyName = string.Empty;
        private string currentActiveGamemodeFriendlyName = string.Empty;

        // Master List of Maps
        private readonly List<string> allPossibleMaps = new List<string>
        {
            "Bank Job", "Derailed", "Downtown", "Dustbowl", "Everglades",
            "Growhouse", "Hollywoods Heights", "Night Job", "Night Woods", "Riptide",
            "The Block", "Backwoods", "Black Friday", "Code Blue", "The Beat",
            "Break pointe", "Museum", "Precinct 7", "The Docks", "Diversion",
            "Double Cross", "Pacific Highway", "Train Dodge", "Alcatraz",
            "Chinatown", "Cemetery", "Thin Ice"
        };

        // Master List of Gamemodes
        private readonly List<string> allPossibleGamemodes = new List<string>
        {
            "Blood Money", "Rescue", "Heist", "Crosshair", "Squad Heist",
            "Conquest", "Conquest Large", "Team Deathmatch", "Hotwire",
            "Capture The Bag", "Bounty Hunter"
        };

        // Map-specific Illegal Gamemodes Restrictions
        private readonly Dictionary<string, List<string>> illegalMapModes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "Bank Job", new List<string> { "Hotwire", "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Derailed", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Downtown", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Dustbowl", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Everglades", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Growhouse", new List<string> { "Hotwire", "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Hollywoods Heights", new List<string> { "Hotwire", "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Night Job", new List<string> { "Hotwire", "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Night Woods", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Riptide", new List<string> { "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "The Block", new List<string> { "Hotwire", "Squad Heist", "Bounty Hunter", "Capture The Bag" } },
            { "Backwoods", new List<string> { "Squad Heist", "Capture The Bag" } },
            { "Black Friday", new List<string> { "Hotwire", "Squad Heist", "Capture The Bag" } },
            { "Code Blue", new List<string> { "Hotwire", "Squad Heist", "Capture The Bag" } },
            { "The Beat", new List<string> { "Hotwire", "Squad Heist", "Capture The Bag" } },
            { "Break pointe", new List<string> { "Capture The Bag" } },
            { "Museum", new List<string> { "Hotwire", "Capture The Bag" } },
            { "Precinct 7", new List<string> { "Capture The Bag" } },
            { "The Docks", new List<string> { "Capture The Bag" } },
            { "Diversion", new List<string> { "Hotwire" } },
            { "Alcatraz", new List<string> { "Hotwire" } },
            { "Chinatown", new List<string> { "Hotwire" } }
        };

        // Configurable timing variables
        private int mapVotingStartDelaySeconds = 180;                 
        private int mapVotingDurationSeconds = 180;                   
        private int modeVotingDurationSeconds = 180;                  
        private int votingStatusUpdateIntervalSeconds = 30;           
        private int delayBetweenMapAndModeVotingSeconds = 10;         

        // Admin / Developer settings
        private string adminNamesCsv = string.Empty;
        private HashSet<string> adminSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string developerName = "kanus15elef";

        #endregion

        #region Standard PRoCon Plugin API

        public CVotingSystem() { }

        public string GetPluginName() { return "CVotingSystem"; }
        public string GetPluginVersion() { return "2.0.1"; }
        public string GetPluginAuthor() { return "Yonatan (kanus15elef)"; }
        public string GetPluginWebsite() { return "localhost"; }
        public string GetPluginDescription() { return "Advanced Map & Gamemode Voting with safe MapList injection."; }

        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            return new List<CPluginVariable>()
            {
                new CPluginVariable("proconLiveUpdate", "enum.enumYesNo(Yes|No)", proconLiveUpdate.ToString()),
                new CPluginVariable("minimumPlayersToVote", typeof(int), minimumPlayersToVote.ToString()),
                new CPluginVariable("mapVotingStartDelaySeconds", typeof(int), mapVotingStartDelaySeconds.ToString()),
                new CPluginVariable("mapVotingDurationSeconds", typeof(int), mapVotingDurationSeconds.ToString()),
                new CPluginVariable("modeVotingDurationSeconds", typeof(int), modeVotingDurationSeconds.ToString()),
                new CPluginVariable("votingStatusUpdateIntervalSeconds", typeof(int), votingStatusUpdateIntervalSeconds.ToString()),
                new CPluginVariable("delayBetweenMapAndModeVotingSeconds", typeof(int), delayBetweenMapAndModeVotingSeconds.ToString()),
                new CPluginVariable("adminNames", typeof(string), adminNamesCsv ?? string.Empty),
                new CPluginVariable("developerName", typeof(string), developerName ?? string.Empty)
            };
        }

        public List<CPluginVariable> GetPluginVariables()
        {
            return GetDisplayPluginVariables();
        }

        public void SetPluginVariable(string strVariable, string strValue)
        {
            try
            {
                if (string.IsNullOrEmpty(strVariable)) return;
                strValue = strValue ?? string.Empty;

                switch (strVariable)
                {
                    case "proconLiveUpdate":
                        try { proconLiveUpdate = (enumYesNo)Enum.Parse(typeof(enumYesNo), strValue, true); } catch { }
                        break;
                    case "minimumPlayersToVote":
                        int minP; if (int.TryParse(strValue, out minP)) minimumPlayersToVote = Math.Max(1, minP);
                        break;
                    case "mapVotingStartDelaySeconds":
                        int delay; if (int.TryParse(strValue, out delay)) mapVotingStartDelaySeconds = Math.Max(1, delay);
                        break;
                    case "mapVotingDurationSeconds":
                        int mDur; if (int.TryParse(strValue, out mDur)) mapVotingDurationSeconds = Math.Max(1, mDur);
                        break;
                    case "modeVotingDurationSeconds":
                        int modeDur; if (int.TryParse(strValue, out modeDur)) modeVotingDurationSeconds = Math.Max(1, modeDur);
                        break;
                    case "votingStatusUpdateIntervalSeconds":
                        int vInt; if (int.TryParse(strValue, out vInt)) votingStatusUpdateIntervalSeconds = Math.Max(1, vInt);
                        break;
                    case "delayBetweenMapAndModeVotingSeconds":
                        int transition; if (int.TryParse(strValue, out transition)) delayBetweenMapAndModeVotingSeconds = Math.Max(1, transition);
                        break;
                    case "adminNames":
                        adminNamesCsv = strValue;
                        UpdateAdminSet();
                        break;
                    case "developerName":
                        developerName = strValue;
                        break;
                }
            }
            catch { }
        }

        public void OnPluginLoaded(string strHost, string strPort, string strPassword)
        {
            this.RegisterEvents(this.GetType().Name,
                "OnGlobalChat",
                "OnTeamChat",
                "OnSquadChat",
                "OnRoundStart",
                "OnRoundOver",
                "OnLevelLoaded",
                "OnPlayerJoin",
                "OnPlayerLeft",
                "OnListPlayers"
            );
        }

        public void OnPluginEnable()
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bCVotingSystem^n v2.0.1 Enabled!");
            ResetVotingState(true);
            currentPlayerCount = 0;
            isWaitingForPlayers = false;
            isNextMapQueued = false;
            previousLevelName = string.Empty;
            currentMapLoadSequenceCount = 0;
            UpdateAdminSet();
            this.ExecuteCommand("procon.protected.send", "admin.listPlayers", "all");
        }

        public void OnPluginDisable()
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bCVotingSystem^n Disabled!");
            StopAllTimers();
        }

        private void LogLive(string message)
        {
            if (proconLiveUpdate == enumYesNo.Yes)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", message);
            }
        }

        #endregion

        #region Admin helpers

        private void UpdateAdminSet()
        {
            lock (syncLock)
            {
                adminSet.Clear();
                if (!string.IsNullOrEmpty(adminNamesCsv))
                {
                    var parts = adminNamesCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var n = p.Trim();
                        if (!string.IsNullOrEmpty(n)) adminSet.Add(n);
                    }
                }
            }
        }

        private bool IsAuthorized(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!string.IsNullOrEmpty(developerName) && name.Equals(developerName, StringComparison.OrdinalIgnoreCase)) return true;
            lock (syncLock) { return adminSet.Contains(name); }
        }

        #endregion

        #region Player Tracking & Minimum Players

        public void OnPlayerJoin(string strSoldierName)
        {
            lock (syncLock) 
            { 
                currentPlayerCount++; 
                LogLive($"[CVotingSystem] [Live]: Player Joined ({strSoldierName}). Total Current Players: {currentPlayerCount}");
                CheckMinimumPlayers();
            }
        }

        public void OnPlayerLeft(CPlayerInfo playerInfo)
        {
            lock (syncLock) 
            { 
                currentPlayerCount = Math.Max(0, currentPlayerCount - 1); 
                string playerName = playerInfo != null ? playerInfo.SoldierName : "Unknown";
                LogLive($"[CVotingSystem] [Live]: Player Left ({playerName}). Total Current Players: {currentPlayerCount}");
            }
        }

        public void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        {
            lock (syncLock) 
            { 
                currentPlayerCount = players.Count; 
                LogLive($"[CVotingSystem] [Live]: Player List Updated. Total Current Players: {currentPlayerCount}");
                CheckMinimumPlayers();
            }
        }

        private void CheckMinimumPlayers()
        {
            lock (syncLock)
            {
                bool thresholdMet = currentPlayerCount >= minimumPlayersToVote;

                if (!isWaitingForPlayers && !thresholdMet && currentPhase == VotingPhase.Idle && !hasVotedThisMatch)
                {
                    isWaitingForPlayers = true;
                    lastThresholdLogTime = DateTime.Now;
                    LogLive($"[CVotingSystem] [Live]: Player count ({currentPlayerCount}) is below minimum requirement ({minimumPlayersToVote}). Entering waiting state until players join.");
                }
                else if (isWaitingForPlayers && thresholdMet && currentPhase == VotingPhase.Idle && roundStartDelayTimer == null && !hasVotedThisMatch)
                {
                    isWaitingForPlayers = false;
                    LogLive($"[CVotingSystem] [Live]: Minimum player count requirement reached ({currentPlayerCount}/{minimumPlayersToVote}). Starting voting countdown immediately!");
                    
                    double delayMs = mapVotingStartDelaySeconds * 1000.0;
                    if (delayMs <= 0) delayMs = 1000.0;

                    roundStartDelayTimer = new Timer(delayMs);
                    roundStartDelayTimer.Elapsed += StartMapVotingPhase;
                    roundStartDelayTimer.AutoReset = false;
                    roundStartDelayTimer.Start();
                }

                if (isWaitingForPlayers && currentPhase == VotingPhase.Idle)
                {
                    if ((DateTime.Now - lastThresholdLogTime).TotalSeconds >= 60)
                    {
                        LogLive($"[CVotingSystem] [Live]: Checking Threshold -> Current Players: {currentPlayerCount} | Min Required: {minimumPlayersToVote} | WaitingState: {isWaitingForPlayers} | Phase: {currentPhase} | TimerActive: {(roundStartDelayTimer != null)}");
                        lastThresholdLogTime = DateTime.Now;
                    }
                }
            }
        }

        #endregion

        #region Round & Timer Management

        private bool IsHeistOrSquadHeist(string gamemode)
        {
            if (string.IsNullOrEmpty(gamemode)) return false;
            return gamemode.Equals("Heist0", StringComparison.OrdinalIgnoreCase) ||
                   gamemode.Equals("Heist", StringComparison.OrdinalIgnoreCase) ||
                   gamemode.Equals("SquadHeist0", StringComparison.OrdinalIgnoreCase) ||
                   gamemode.Equals("Squad Heist", StringComparison.OrdinalIgnoreCase);
        }

        public void OnLevelLoaded(string mapFileName, string Gamemode, int roundsPlayed, int roundsTotal)
        {
            lock (syncLock)
            {
                currentActiveMapInternalName = mapFileName;
                currentActiveGamemodeInternalName = Gamemode;
                currentActiveMapFriendlyName = GetFriendlyMapName(mapFileName);
                currentActiveGamemodeFriendlyName = GetFriendlyModeName(Gamemode);
                currentRoundsPlayed = roundsPlayed;

                LogLive($"[CVotingSystem] [Live]: OnLevelLoaded -> Map: {currentActiveMapFriendlyName} ({mapFileName}), Mode: {currentActiveGamemodeFriendlyName} ({Gamemode}), Round: {roundsPlayed + 1} of {roundsTotal}");

                if (string.Equals(previousLevelName, mapFileName, StringComparison.OrdinalIgnoreCase))
                {
                    currentMapLoadSequenceCount++;
                }
                else
                {
                    previousLevelName = mapFileName;
                    currentMapLoadSequenceCount = 1;
                }

                int maxAllowedLoads = IsHeistOrSquadHeist(Gamemode) ? 2 : 1;
                if (currentMapLoadSequenceCount > maxAllowedLoads)
                {
                    LogLive($"[CVotingSystem] [Live]: Detected infinite map reload loop bug (Sequence: {currentMapLoadSequenceCount}). Forcing immediate map advancement.");
                    this.ExecuteCommand("procon.protected.send", "mapList.runNextMap");
                    return;
                }

                bool isRoundTwo = (roundsPlayed >= 1) || (IsHeistOrSquadHeist(Gamemode) && currentMapLoadSequenceCount == 2);

                if (isRoundTwo)
                {
                    LogLive($"[CVotingSystem] [Live]: Round 2/2 detected on {currentActiveMapFriendlyName}.");

                    if (isWaitingForRoundTwoInjection)
                    {
                        LogLive($"[CVotingSystem] [Live]: Injecting stored memory tray map ({storedRoundTwoMap} [{storedRoundTwoMode}]) now!");
                        
                        winningMap = storedRoundTwoMap;
                        winningGamemode = storedRoundTwoMode;
                        ApplyNextMapMidRound();
                        
                        isWaitingForRoundTwoInjection = false;
                        storedRoundTwoMap = string.Empty;
                        storedRoundTwoMode = string.Empty;
                    }

                    if (currentPhase == VotingPhase.Idle && !hasVotedThisMatch)
                    {
                        TriggerRoundStartSequence();
                    }
                }
                else if (currentMapLoadSequenceCount == 1)
                {
                    LogLive("[CVotingSystem] [Live]: New match started (Round 0 / Round 1 of map). Resetting voting states.");

                    if (isNextMapQueued)
                    {
                        try
                        {
                            LogLive("[CVotingSystem] [Live]: Cleaning up queued map entry from index 0.");
                            this.ExecuteCommand("procon.protected.send", "mapList.remove", "0");
                            this.ExecuteCommand("procon.protected.send", "mapList.save");
                            this.ExecuteCommand("procon.protected.send", "mapList.list");
                            isNextMapQueued = false;
                        }
                        catch (Exception ex)
                        {
                            LogLive("^1Error shifting maplist: " + ex.Message);
                        }
                    }

                    ResetVotingState(false);
                    TriggerRoundStartSequence();
                }
            }
        }

        public void OnRoundStart()
        {
            lock (syncLock)
            {
                LogLive($"[CVotingSystem] [Live]: OnRoundStart triggered. Current Phase: {currentPhase}, WaitingForPlayers: {isWaitingForPlayers}");

                if (currentPhase == VotingPhase.Idle && roundStartDelayTimer == null && !isWaitingForPlayers && !hasVotedThisMatch)
                {
                    TriggerRoundStartSequence();
                }
            }
        }

        private void TriggerRoundStartSequence()
        {
            lock (syncLock)
            {
                if (hasVotedThisMatch)
                {
                    LogLive($"[CVotingSystem] [Live]: Voting already completed for this match. Skipping round start trigger.");
                    return;
                }

                LogLive($"[CVotingSystem] [Live]: Evaluating TriggerRoundStartSequence -> PlayerCount: {currentPlayerCount}, MinRequired: {minimumPlayersToVote}");

                if (currentPlayerCount < minimumPlayersToVote)
                {
                    isWaitingForPlayers = true;
                    LogLive($"[CVotingSystem] [Live]: Player count is {currentPlayerCount} (< {minimumPlayersToVote}). Voting countdown postponed, waiting for players to join.");
                    return;
                }

                isWaitingForPlayers = false;

                double delayMs = mapVotingStartDelaySeconds * 1000.0;
                if (delayMs <= 0) delayMs = 1000.0;

                if (roundStartDelayTimer != null)
                {
                    roundStartDelayTimer.Stop();
                    roundStartDelayTimer.Dispose();
                    roundStartDelayTimer = null;
                }

                roundStartDelayTimer = new Timer(delayMs);
                roundStartDelayTimer.Elapsed += StartMapVotingPhase;
                roundStartDelayTimer.AutoReset = false;
                roundStartDelayTimer.Start();

                LogLive($"[CVotingSystem] [Live]: Voting countdown started! Will begin map vote in {mapVotingStartDelaySeconds}s on map {currentActiveMapFriendlyName}.");
            }
        }

        public void OnRoundOver(int winningTeamId)
        {
            lock (syncLock)
            {
                LogLive($"[CVotingSystem] [Live]: OnRoundOver triggered. Winning Team ID: {winningTeamId}");

                if (currentPhase == VotingPhase.GamemodeVotingEnded)
                {
                    StopAllTimers();
                    currentPhase = VotingPhase.Idle;
                }
            }
        }

        private void StopAllTimers()
        {
            if (roundStartDelayTimer != null) { roundStartDelayTimer.Stop(); roundStartDelayTimer.Dispose(); roundStartDelayTimer = null; }
            if (votingPeriodicTimer != null) { votingPeriodicTimer.Stop(); votingPeriodicTimer.Dispose(); votingPeriodicTimer = null; }
            if (phaseTransitionTimer != null) { phaseTransitionTimer.Stop(); phaseTransitionTimer.Dispose(); phaseTransitionTimer = null; }
            LogLive("[CVotingSystem] [Live]: All timers stopped and disposed.");
        }

        private void ResetVotingState(bool clearMemoryTray = false)
        {
            StopAllTimers();
            currentPhase = VotingPhase.Idle;
            hasVotedThisMatch = false; // Master Match Flag reset
            activeMapPool.Clear();
            activeGamemodePool.Clear();
            mapNominations.Clear();
            gamemodeNominations.Clear();
            mapVotes.Clear();
            gamemodeVotes.Clear();
            playerCurrentMapVote.Clear();
            playerCurrentGamemodeVote.Clear();
            winningMap = string.Empty;
            winningGamemode = string.Empty;
            mapVotingIntervalCount = 0;
            gamemodeVotingIntervalCount = 0;
            
            if (clearMemoryTray)
            {
                isWaitingForRoundTwoInjection = false;
                storedRoundTwoMap = string.Empty;
                storedRoundTwoMode = string.Empty;
            }

            LogLive("[CVotingSystem] [Live]: Voting state completely reset to Idle.");
        }

        #endregion

        #region Phase 1: Map Voting Logic

        private void StartMapVotingPhase(object sender, ElapsedEventArgs e)
        {
            lock (syncLock)
            {
                if (roundStartDelayTimer != null)
                {
                    roundStartDelayTimer.Stop();
                    roundStartDelayTimer.Dispose();
                    roundStartDelayTimer = null;
                }

                currentPhase = VotingPhase.MapVoting;
                BuildMapPool();
                mapVotingIntervalCount = 0;

                LogLive("[CVotingSystem] [Live]: Map voting phase has officially started.");
                foreach (var map in activeMapPool)
                {
                    string selectionType = mapNominations.ContainsValue(map) ? "nominated" : "random";
                    LogLive($"[CVotingSystem] [Live] -> Pool Map: {map} ({selectionType})");
                }

                SendMultiLineGlobalChat(BuildMapVotingMessage("MAP VOTING STARTS NOW:"));

                int intervalMs = votingStatusUpdateIntervalSeconds * 1000;
                if (intervalMs <= 0) intervalMs = 1000;

                votingPeriodicTimer = new Timer(intervalMs);
                votingPeriodicTimer.Elapsed += MapVotingPeriodicUpdate;
                votingPeriodicTimer.AutoReset = true;
                votingPeriodicTimer.Start();
            }
        }

        private void MapVotingPeriodicUpdate(object sender, ElapsedEventArgs e)
        {
            lock (syncLock)
            {
                mapVotingIntervalCount++;
                int mapVotingIntervals = Math.Max(1, (int)Math.Ceiling((double)mapVotingDurationSeconds / votingStatusUpdateIntervalSeconds));
                LogLive($"[CVotingSystem] [Live]: Map voting periodic update {mapVotingIntervalCount}/{mapVotingIntervals}");

                if (mapVotingIntervalCount < mapVotingIntervals)
                {
                    SendMultiLineGlobalChat(BuildMapVotingMessage("MAP VOTING STARTED:"));
                }
                else
                {
                    if (votingPeriodicTimer != null)
                    {
                        votingPeriodicTimer.Stop();
                        votingPeriodicTimer.Dispose();
                        votingPeriodicTimer = null;
                    }
                    EndMapVoting();
                }
            }
        }

        private void BuildMapPool()
        {
            activeMapPool.Clear();
            List<string> candidateMaps = new List<string>(allPossibleMaps);

            if (!string.IsNullOrEmpty(currentActiveMapFriendlyName))
            {
                candidateMaps.RemoveAll(m => m.Equals(currentActiveMapFriendlyName, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var kvp in mapNominations)
            {
                if (!activeMapPool.Contains(kvp.Value) && activeMapPool.Count < 8)
                {
                    activeMapPool.Add(kvp.Value);
                    candidateMaps.RemoveAll(m => m.Equals(kvp.Value, StringComparison.OrdinalIgnoreCase));
                }
            }

            while (activeMapPool.Count < 8 && candidateMaps.Count > 0)
            {
                int index = rnd.Next(candidateMaps.Count);
                string randomMap = candidateMaps[index];
                if (!activeMapPool.Contains(randomMap))
                {
                    activeMapPool.Add(randomMap);
                }
                candidateMaps.RemoveAt(index);
            }

            mapVotes.Clear();
            foreach (var map in activeMapPool)
            {
                mapVotes[map] = 0;
            }
        }

        private List<string> BuildMapVotingMessage(string headerTitle)
        {
            List<string> lines = new List<string> { headerTitle };
            for (int i = 0; i < activeMapPool.Count; i += 2)
            {
                string map1 = activeMapPool[i];
                int votes1 = mapVotes.ContainsKey(map1) ? mapVotes[map1] : 0;

                if (i + 1 < activeMapPool.Count)
                {
                    string map2 = activeMapPool[i + 1];
                    int votes2 = mapVotes.ContainsKey(map2) ? mapVotes[map2] : 0;
                    lines.Add($"{i + 1} {map1} [{votes1}] / {i + 2} {map2} [{votes2}]");
                }
                else
                {
                    lines.Add($"{i + 1} {map1} [{votes1}]");
                }
            }
            return lines;
        }

        private void EndMapVoting()
        {
            currentPhase = VotingPhase.MapVotingEnded;

            int maxVotes = -1;
            List<string> tiedMaps = new List<string>();

            foreach (var map in activeMapPool)
            {
                int v = mapVotes.ContainsKey(map) ? mapVotes[map] : 0;
                if (v > maxVotes)
                {
                    maxVotes = v;
                    tiedMaps.Clear();
                    tiedMaps.Add(map);
                }
                else if (v == maxVotes)
                {
                    tiedMaps.Add(map);
                }
            }

            winningMap = tiedMaps.Count > 0 ? tiedMaps[rnd.Next(tiedMaps.Count)] : (activeMapPool.Count > 0 ? activeMapPool[0] : "Bank Job");
            bool isTieBreaker = tiedMaps.Count > 1;

            if (isTieBreaker)
            {
                LogLive($"[CVotingSystem] [Live]: Map vote ended. Winner: {winningMap} with {maxVotes} votes (Tie breaker resolved).");
            }
            else
            {
                LogLive($"[CVotingSystem] [Live]: Map vote ended. Winner: {winningMap} with {maxVotes} votes.");
            }

            SendGlobalChat($"{winningMap} has won the voting starting gamemode voting now !");

            double transitionMs = delayBetweenMapAndModeVotingSeconds * 1000.0;
            if (transitionMs <= 0) transitionMs = 1000.0;

            phaseTransitionTimer = new Timer(transitionMs);
            phaseTransitionTimer.Elapsed += StartGamemodeVotingPhase;
            phaseTransitionTimer.AutoReset = false;
            phaseTransitionTimer.Start();
        }

        #endregion

        #region Phase 2: Gamemode Voting Logic

        private void StartGamemodeVotingPhase(object sender, ElapsedEventArgs e)
        {
            lock (syncLock)
            {
                if (phaseTransitionTimer != null)
                {
                    phaseTransitionTimer.Stop();
                    phaseTransitionTimer.Dispose();
                    phaseTransitionTimer = null;
                }

                currentPhase = VotingPhase.GamemodeVoting;
                BuildGamemodePool(winningMap);
                gamemodeVotingIntervalCount = 0;

                LogLive($"[CVotingSystem] [Live]: Gamemode voting phase started for winning map: {winningMap}");
                foreach (var mode in activeGamemodePool)
                {
                    string selectionType = gamemodeNominations.ContainsValue(mode) ? "nominated" : "random";
                    LogLive($"[CVotingSystem] [Live] -> Pool Mode: {mode} ({selectionType})");
                }

                SendMultiLineGlobalChat(BuildGamemodeVotingMessage($"GAMEMODE VOTING FOR MAP {winningMap} HAS STARTED"));

                int intervalMs = votingStatusUpdateIntervalSeconds * 1000;
                if (intervalMs <= 0) intervalMs = 1000;

                votingPeriodicTimer = new Timer(intervalMs);
                votingPeriodicTimer.Elapsed += GamemodeVotingPeriodicUpdate;
                votingPeriodicTimer.AutoReset = true;
                votingPeriodicTimer.Start();
            }
        }

        private void GamemodeVotingPeriodicUpdate(object sender, ElapsedEventArgs e)
        {
            lock (syncLock)
            {
                gamemodeVotingIntervalCount++;
                int modeVotingIntervals = Math.Max(1, (int)Math.Ceiling((double)modeVotingDurationSeconds / votingStatusUpdateIntervalSeconds));
                LogLive($"[CVotingSystem] [Live]: Gamemode voting periodic update {gamemodeVotingIntervalCount}/{modeVotingIntervals}");

                if (gamemodeVotingIntervalCount < modeVotingIntervals)
                {
                    SendMultiLineGlobalChat(BuildGamemodeVotingMessage($"GAMEMODE VOTING FOR MAP {winningMap} STARTED:"));
                }
                else
                {
                    if (votingPeriodicTimer != null)
                    {
                        votingPeriodicTimer.Stop();
                        votingPeriodicTimer.Dispose();
                        votingPeriodicTimer = null;
                    }
                    EndGamemodeVoting();
                }
            }
        }

        private bool IsGamemodeLegalForMap(string mapName, string modeName)
        {
            if (string.IsNullOrEmpty(mapName) || string.IsNullOrEmpty(modeName)) return false;

            if (illegalMapModes.ContainsKey(mapName))
            {
                foreach (var illegalMode in illegalMapModes[mapName])
                {
                    if (illegalMode.Equals(modeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return false; 
                    }
                }
            }

            return allPossibleGamemodes.Exists(x => x.Equals(modeName, StringComparison.OrdinalIgnoreCase));
        }

        private void BuildGamemodePool(string mapName)
        {
            activeGamemodePool.Clear();
            List<string> availableModes = GetAvailableModes(mapName, currentPlayerCount);

            foreach (var kvp in gamemodeNominations)
            {
                string nominatedMode = kvp.Value;
                
                if (!activeGamemodePool.Contains(nominatedMode) && activeGamemodePool.Count < 8 && IsGamemodeLegalForMap(mapName, nominatedMode))
                {
                    activeGamemodePool.Add(nominatedMode);
                    availableModes.RemoveAll(x => x.Equals(nominatedMode, StringComparison.OrdinalIgnoreCase));
                }
            }

            while (activeGamemodePool.Count < 8 && availableModes.Count > 0)
            {
                int index = rnd.Next(availableModes.Count);
                string mode = availableModes[index];
                if (!activeGamemodePool.Contains(mode))
                {
                    activeGamemodePool.Add(mode);
                }
                availableModes.RemoveAt(index);
            }

            gamemodeVotes.Clear();
            foreach (var mode in activeGamemodePool)
            {
                gamemodeVotes[mode] = 0;
            }
        }

        private List<string> BuildGamemodeVotingMessage(string headerTitle)
        {
            List<string> lines = new List<string> { headerTitle };
            for (int i = 0; i < activeGamemodePool.Count; i += 2)
            {
                string mode1 = activeGamemodePool[i];
                int votes1 = gamemodeVotes.ContainsKey(mode1) ? gamemodeVotes[mode1] : 0;

                if (i + 1 < activeGamemodePool.Count)
                {
                    string mode2 = activeGamemodePool[i + 1];
                    int votes2 = gamemodeVotes.ContainsKey(mode2) ? gamemodeVotes[mode2] : 0;
                    lines.Add($"{i + 1} {mode1} [{votes1}] / {i + 2} {mode2} [{votes2}]");
                }
                else
                {
                    lines.Add($"{i + 1} {mode1} [{votes1}]");
                }
            }
            return lines;
        }

        private void EndGamemodeVoting()
        {
            currentPhase = VotingPhase.GamemodeVotingEnded;
            hasVotedThisMatch = true; // Lock out further votes for this match

            int maxVotes = -1;
            List<string> tiedModes = new List<string>();

            foreach (var mode in activeGamemodePool)
            {
                int v = gamemodeVotes.ContainsKey(mode) ? gamemodeVotes[mode] : 0;
                if (v > maxVotes)
                {
                    maxVotes = v;
                    tiedModes.Clear();
                    tiedModes.Add(mode);
                }
                else if (v == maxVotes)
                {
                    tiedModes.Add(mode);
                }
            }

            winningGamemode = tiedModes.Count > 0 ? tiedModes[rnd.Next(tiedModes.Count)] : (activeGamemodePool.Count > 0 ? activeGamemodePool[0] : "Blood Money");

            int mapVotesCount = mapVotes.ContainsKey(winningMap) ? mapVotes[winningMap] : 0;
            int modeVotesCount = gamemodeVotes.ContainsKey(winningGamemode) ? gamemodeVotes[winningGamemode] : 0;

            LogLive($"[CVotingSystem] [Live]: Gamemode vote ended. Winner: {winningMap} [{winningGamemode}] with Map Votes: {mapVotesCount}, Mode Votes: {modeVotesCount}");
            
            SendGlobalChat("Map voting has finished the next map will be:");
            SendGlobalChat($"{winningMap} [{winningGamemode}] !");

            TryExecuteOrQueueInjection();
        }

        #endregion

        #region Injection Timing Handlers

        private void TryExecuteOrQueueInjection()
        {
            bool currentIsMultiRound = IsHeistOrSquadHeist(currentActiveGamemodeInternalName);

            if (currentIsMultiRound)
            {
                if (currentRoundsPlayed == 0)
                {
                    storedRoundTwoMap = winningMap;
                    storedRoundTwoMode = winningGamemode;
                    isWaitingForRoundTwoInjection = true;

                    LogLive($"[CVotingSystem] [Live]: Currently on Round 1/2 of {currentActiveGamemodeFriendlyName}. Storing winner ({winningMap} [{winningGamemode}]) in memory tray.");
                    LogLive("[CVotingSystem] [Live]: Waiting until Round 2/2 starts to inject.");
                }
                else
                {
                    LogLive($"[CVotingSystem] [Live]: Currently on Round 2/2 of {currentActiveGamemodeFriendlyName}. Injecting map instantly.");
                    ApplyNextMapMidRound();
                }
            }
            else
            {
                LogLive($"[CVotingSystem] [Live]: Single-round mode detected ({currentActiveGamemodeFriendlyName}). Injecting map instantly.");
                ApplyNextMapMidRound();
            }
        }

        #endregion

        #region Chat Commands & Inputs

        public void OnGlobalChat(string strSpeaker, string strMessage) { HandleChatInput(strSpeaker, strMessage); }
        public void OnTeamChat(string strSpeaker, string strMessage, int teamId) { HandleChatInput(strSpeaker, strMessage); }
        public void OnSquadChat(string strSpeaker, string strMessage, int teamId, int squadId) { HandleChatInput(strSpeaker, strMessage); }

        private void HandleChatInput(string strSpeaker, string strMessage)
        {
            string cleanMessage = strMessage.Trim();
            if (strSpeaker == "Server") return;

            bool isAdmin = IsAuthorized(strSpeaker);

            lock (syncLock)
            {
                if (cleanMessage.Equals("!votestart", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) { SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); return; }
                    isWaitingForPlayers = false; 
                    if (currentPhase == VotingPhase.Idle) 
                    { 
                        hasVotedThisMatch = false; // Allow admin to force a new vote even if one completed
                        LogLive($"[CVotingSystem] [Live]: Admin ({strSpeaker}) forcefully started voting via !votestart.");
                        SendGlobalChat("Admin has forcefully started the voting process!"); 
                        StartMapVotingPhase(null, null); 
                    }
                    else { SendPlayerChat(strSpeaker, "Voting is already active or finished."); }
                    return;
                }

                if (cleanMessage.Equals("!voterefresh", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) 
                    { 
                        SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); 
                        return; 
                    }

                    try
                    {
                        this.ExecuteCommand("procon.protected.send", "mapList.remove", "1");
                        this.ExecuteCommand("procon.protected.send", "mapList.list");
                    }
                    catch { }

                    ResetVotingState(true);
                    LogLive($"[CVotingSystem] [Live]: Admin ({strSpeaker}) executed !voterefresh. Reset state and cleared queued rotation.");
                    SendGlobalChat("all voting stages has been deleted and clear");
                    return;
                }

                if (cleanMessage.Equals("!voteend", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) { SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); return; }
                    if (currentPhase == VotingPhase.MapVoting)
                    {
                        LogLive($"[CVotingSystem] [Live]: Admin ({strSpeaker}) forcefully ended map voting.");
                        if (votingPeriodicTimer != null) { votingPeriodicTimer.Stop(); votingPeriodicTimer.Dispose(); votingPeriodicTimer = null; }
                        EndMapVoting();
                    }
                    else if (currentPhase == VotingPhase.GamemodeVoting)
                    {
                        LogLive($"[CVotingSystem] [Live]: Admin ({strSpeaker}) forcefully ended gamemode voting.");
                        if (votingPeriodicTimer != null) { votingPeriodicTimer.Stop(); votingPeriodicTimer.Dispose(); votingPeriodicTimer = null; }
                        EndGamemodeVoting();
                    }
                    else { SendPlayerChat(strSpeaker, "There is no active voting phase to end."); }
                    return;
                }

                if (cleanMessage.Equals("!nextmap", StringComparison.OrdinalIgnoreCase) || cleanMessage.Equals("!next map", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentPhase == VotingPhase.GamemodeVotingEnded)
                    {
                        SendGlobalChat("The next map will be:");
                        SendGlobalChat($"{winningMap} [{winningGamemode}] !");
                    }
                    else { SendGlobalChat("Voting hasn't started or finished yet"); }
                    return;
                }

                if (cleanMessage.StartsWith("!nommap ", StringComparison.OrdinalIgnoreCase))
                {
                    HandleMapNomination(strSpeaker, cleanMessage.Substring(8).Trim());
                    return;
                }

                if (cleanMessage.StartsWith("!nomgamemode ", StringComparison.OrdinalIgnoreCase))
                {
                    HandleGamemodeNomination(strSpeaker, cleanMessage.Substring(13).Trim());
                    return;
                }
                if (cleanMessage.StartsWith("!nommode ", StringComparison.OrdinalIgnoreCase))
                {
                    HandleGamemodeNomination(strSpeaker, cleanMessage.Substring(9).Trim());
                    return;
                }

                if (currentPhase == VotingPhase.MapVoting && int.TryParse(cleanMessage, out int mapChoice))
                {
                    if (mapChoice >= 1 && mapChoice <= activeMapPool.Count)
                    {
                        string chosenMap = activeMapPool[mapChoice - 1];
                        if (playerCurrentMapVote.ContainsKey(strSpeaker))
                        {
                            string oldMap = playerCurrentMapVote[strSpeaker];
                            if (oldMap != chosenMap)
                            {
                                mapVotes[oldMap] = Math.Max(0, mapVotes[oldMap] - 1);
                                mapVotes[chosenMap]++;
                                playerCurrentMapVote[strSpeaker] = chosenMap;
                                LogLive($"[CVotingSystem] [Live]: Player {strSpeaker} changed map vote from {oldMap} to {chosenMap}");
                                SendGlobalChat($"{strSpeaker} has changed its voting from {oldMap} to {chosenMap} !");
                            }
                        }
                        else
                        {
                            playerCurrentMapVote[strSpeaker] = chosenMap;
                            mapVotes[chosenMap]++;
                            LogLive($"[CVotingSystem] [Live]: Player {strSpeaker} voted for map {chosenMap}");
                            SendGlobalChat($"{strSpeaker} has voted for {chosenMap} !");
                        }
                    }
                    return;
                }

                if (currentPhase == VotingPhase.GamemodeVoting && int.TryParse(cleanMessage, out int modeChoice))
                {
                    if (modeChoice >= 1 && modeChoice <= activeGamemodePool.Count)
                    {
                        string chosenMode = activeGamemodePool[modeChoice - 1];
                        if (playerCurrentGamemodeVote.ContainsKey(strSpeaker))
                        {
                            string oldMode = playerCurrentGamemodeVote[strSpeaker];
                            if (oldMode != chosenMode)
                            {
                                gamemodeVotes[oldMode] = Math.Max(0, gamemodeVotes[oldMode] - 1);
                                gamemodeVotes[chosenMode]++;
                                playerCurrentGamemodeVote[strSpeaker] = chosenMode;
                                LogLive($"[CVotingSystem] [Live]: Player {strSpeaker} changed mode vote from {oldMode} to {chosenMode}");
                                SendGlobalChat($"{strSpeaker} has changed its voting from the map {winningMap} [{oldMode}] to the map {winningMap} [{chosenMode}] !");
                            }
                        }
                        else
                        {
                            playerCurrentGamemodeVote[strSpeaker] = chosenMode;
                            gamemodeVotes[chosenMode]++;
                            LogLive($"[CVotingSystem] [Live]: Player {strSpeaker} voted for mode {chosenMode}");
                            SendGlobalChat($"{strSpeaker} has voted for the map {winningMap} [{chosenMode}] !");
                        }
                    }
                    return;
                }
            }
        }

        private string ResolveMapName(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            input = input.Trim();
            string exact = allPossibleMaps.Find(m => m.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<string> prefixMatches = allPossibleMaps.FindAll(m => m.StartsWith(input, StringComparison.OrdinalIgnoreCase));
            if (prefixMatches.Count == 1) return prefixMatches[0];
            List<string> containsMatches = allPossibleMaps.FindAll(m => m.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0);
            if (containsMatches.Count == 1) return containsMatches[0];
            return null;
        }

        private string ResolveGamemodeName(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            input = input.Trim();
            string exact = allPossibleGamemodes.Find(g => g.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<string> prefixMatches = allPossibleGamemodes.FindAll(g => g.StartsWith(input, StringComparison.OrdinalIgnoreCase));
            if (prefixMatches.Count == 1) return prefixMatches[0];
            List<string> containsMatches = allPossibleGamemodes.FindAll(g => g.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0);
            if (containsMatches.Count == 1) return containsMatches[0];
            return null;
        }

        private void HandleMapNomination(string playerName, string mapName)
        {
            string matchedMap = ResolveMapName(mapName);
            if (matchedMap == null) { SendPlayerChat(playerName, "Map hasn't been found try again"); return; }
            if (currentPhase != VotingPhase.Idle) { SendPlayerChat(playerName, "Cannot nominate maps while voting is active or finished!"); return; }

            if (mapNominations.ContainsKey(playerName))
            {
                string oldNom = mapNominations[playerName];
                mapNominations[playerName] = matchedMap;
                LogLive($"[CVotingSystem] [Live]: Player {playerName} changed map nomination from {oldNom} to {matchedMap}");
                SendGlobalChat($"{playerName} has changed its nomination from {oldNom} to {matchedMap}");
            }
            else
            {
                if (mapNominations.Count >= 8) { SendPlayerChat(playerName, "All nomination slots have been taken you cant nominate for a map!"); return; }
                mapNominations[playerName] = matchedMap;
                LogLive($"[CVotingSystem] [Live]: Player {playerName} nominated map {matchedMap}");
                SendGlobalChat($"{playerName} has nominated for map {matchedMap} !");
            }
        }

        private void HandleGamemodeNomination(string playerName, string modeName)
        {
            string matchedMode = ResolveGamemodeName(modeName);
            if (matchedMode == null) { SendPlayerChat(playerName, "Game mode hasn't been found try again"); return; }
            if (currentPhase == VotingPhase.GamemodeVoting || currentPhase == VotingPhase.GamemodeVotingEnded) { SendPlayerChat(playerName, "Cant nominate a gamemode while gamemode voting has started !"); return; }

            if (gamemodeNominations.ContainsKey(playerName))
            {
                string oldNom = gamemodeNominations[playerName];
                gamemodeNominations[playerName] = matchedMode;
                LogLive($"[CVotingSystem] [Live]: Player {playerName} changed gamemode nomination from {oldNom} to {matchedMode}");
                SendGlobalChat($"{playerName} has changed its nomination gamemode nomination from {oldNom} to {matchedMode}");
            }
            else
            {
                if (gamemodeNominations.Count >= 8) { SendPlayerChat(playerName, "All gamemode nomination slots have been taken you cant nominate for a gamemode!"); return; }
                gamemodeNominations[playerName] = matchedMode;
                LogLive($"[CVotingSystem] [Live]: Player {playerName} nominated gamemode {matchedMode}");
                SendGlobalChat($"{playerName} has nominated for gamemode {matchedMode} !");
            }
        }

        #endregion

        #region Mode Filtering Database & Chat Output Helpers

        private List<string> GetAvailableModes(string mapName, int playerCount)
        {
            List<string> modes = new List<string>();
            string m = mapName.ToLower();

            if (playerCount < 10)
            {
                if (m == "bank job" || m == "hollywoods heights" || m == "night job" || m == "the block" || m == "growhouse") modes.AddRange(new[] { "Blood Money", "Rescue", "Heist", "Crosshair" });
                else if (m == "derailed" || m == "downtown" || m == "dustbowl" || m == "everglades" || m == "night woods" || m == "riptide" || m == "backwoods" || m == "black friday" || m == "code blue" || m == "the beat") modes.AddRange(new[] { "Crosshair", "Rescue" });
                else if (m == "break pointe" || m == "museum" || m == "precinct 7" || m == "the docks" || m == "alcatraz" || m == "chinatown" || m == "cemetery" || m == "thin ice") modes.AddRange(new[] { "Crosshair", "Rescue", "Squad Heist" });
                else if (m == "diversion") modes.AddRange(new[] { "Blood Money", "Rescue", "Squad Heist", "Crosshair" });
                else if (m == "double cross" || m == "pacific highway" || m == "train dodge") modes.AddRange(new[] { "Rescue", "Squad Heist", "Crosshair" });
                else modes.AddRange(new[] { "Crosshair", "Rescue" });
            }
            else if (playerCount >= 10 && playerCount < 20)
            {
                if (m == "bank job") modes.AddRange(new[] { "Blood Money", "Rescue", "Heist", "Crosshair" });
                else if (m == "derailed" || m == "downtown" || m == "dustbowl" || m == "everglades" || m == "night woods" || m == "black friday" || m == "code blue" || m == "the beat") modes.AddRange(new[] { "Crosshair", "Rescue", "Blood Money" });
                else if (m == "hollywoods heights" || m == "night job" || m == "the block" || m == "growhouse") modes.AddRange(new[] { "Blood Money", "Crosshair", "Rescue", "Heist", "Conquest" });
                else if (m == "riptide" || m == "backwoods") modes.AddRange(new[] { "Crosshair", "Rescue" });
                else if (m == "break pointe" || m == "the docks" || m == "double cross" || m == "pacific highway" || m == "train dodge" || m == "cemetery" || m == "thin ice") modes.AddRange(new[] { "Crosshair", "Rescue", "Squad Heist" });
                else if (m == "museum" || m == "precinct 7" || m == "alcatraz" || m == "chinatown") modes.AddRange(new[] { "Crosshair", "Rescue", "Squad Heist", "Blood Money" });
                else if (m == "diversion") modes.AddRange(new[] { "Blood Money", "Rescue", "Squad Heist", "Crosshair", "Conquest" });
                else modes.AddRange(new[] { "Crosshair", "Rescue", "Blood Money" });
            }
            else if (playerCount >= 20 && playerCount < 30)
            {
                if (m == "bank job") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Conquest Large", "Team Deathmatch" });
                else if (m == "derailed") modes.AddRange(new[] { "Blood Money", "Conquest", "Hotwire", "Heist", "Team Deathmatch" });
                else if (m == "downtown") modes.AddRange(new[] { "Blood Money", "Hotwire", "Heist" });
                else if (m == "dustbowl") modes.AddRange(new[] { "Blood Money", "Hotwire", "Heist", "Team Deathmatch" });
                else if (m == "everglades") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Hotwire" });
                else if (m == "hollywoods heights" || m == "night job") modes.AddRange(new[] { "Blood Money", "Crosshair", "Rescue", "Heist", "Conquest" });
                else if (m == "night woods") modes.AddRange(new[] { "Crosshair", "Rescue", "Blood Money" });
                else if (m == "riptide") modes.AddRange(new[] { "Heist", "Blood Money", "Team Deathmatch" });
                else if (m == "the block") modes.AddRange(new[] { "Blood Money", "Heist", "Crosshair", "Rescue", "Conquest" });
                else if (m == "backwoods") modes.AddRange(new[] { "Crosshair", "Rescue" });
                else if (m == "black friday" || m == "code blue" || m == "the beat") modes.AddRange(new[] { "Crosshair", "Rescue", "Blood Money" });
                else if (m == "break pointe" || m == "museum" || m == "precinct 7" || m == "the docks") modes.AddRange(new[] { "Crosshair", "Rescue", "Squad Heist" });
                else if (m == "diversion") modes.AddRange(new[] { "Blood Money", "Rescue", "Squad Heist", "Crosshair", "Conquest", "Capture The Bag" });
                else if (m == "double cross") modes.AddRange(new[] { "Rescue", "Squad Heist", "Crosshair", "Capture The Bag", "Team Deathmatch", "Hotwire" });
                else if (m == "pacific highway") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Capture The Bag", "Team Deathmatch", "Hotwire" });
                else if (m == "train dodge") modes.AddRange(new[] { "Blood Money", "Hotwire", "Conquest", "Heist", "Capture The Bag" });
                else if (m == "alcatraz") modes.AddRange(new[] { "Blood Money", "Heist", "Capture The Bag", "Conquest", "Team Deathmatch" });
                else if (m == "chinatown") modes.AddRange(new[] { "Blood Money", "Conquest", "Capture The Bag", "Heist", "Team Deathmatch" });
                else if (m == "cemetery" || m == "thin ice") modes.AddRange(new[] { "Blood Money", "Heist", "Capture The Bag", "Conquest", "Team Deathmatch", "Hotwire" });
                else modes.AddRange(new[] { "Crosshair", "Rescue" });
            }
            else if (playerCount >= 30 && playerCount < 40)
            {
                if (m == "bank job") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Conquest Large", "Team Deathmatch" });
                else if (m == "derailed") modes.AddRange(new[] { "Blood Money", "Conquest", "Hotwire", "Heist", "Team Deathmatch" });
                else if (m == "downtown") modes.AddRange(new[] { "Blood Money", "Hotwire", "Heist", "Team Deathmatch", "Conquest" });
                else if (m == "dustbowl") modes.AddRange(new[] { "Blood Money", "Hotwire", "Heist", "Team Deathmatch", "Conquest" });
                else if (m == "everglades") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Hotwire" });
                else if (m == "hollywoods heights") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Team Deathmatch", "Conquest Large" });
                else if (m == "night job") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Conquest Large", "Team Deathmatch" });
                else if (m == "night woods") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Hotwire" });
                else if (m == "riptide") modes.AddRange(new[] { "Heist", "Blood Money", "Team Deathmatch", "Hotwire" });
                else if (m == "the block") modes.AddRange(new[] { "Blood Money", "Conquest", "Conquest Large", "Team Deathmatch" });
                else if (m == "backwoods") modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Hotwire" });
                else if (m == "black friday" || m == "code blue" || m == "the beat") modes.AddRange(new[] { "Blood Money", "Conquest", "Heist", "Conquest Large" });
                else if (m == "break pointe" || m == "museum") modes.AddRange(new[] { "Blood Money", "Hotwire", "Conquest", "Heist", "Team Deathmatch" });
                else if (m == "precinct 7") modes.AddRange(new[] { "Blood Money", "Conquest", "Team Deathmatch", "Hotwire" });
                else if (m == "the docks") modes.AddRange(new[] { "Blood Money", "Heist", "Hotwire", "Conquest", "Team Deathmatch" });
                else if (m == "diversion") modes.AddRange(new[] { "Blood Money", "Conquest", "Heist", "Capture The Bag", "Team Deathmatch", "Conquest Large" });
                else if (m == "double cross" || m == "pacific highway" || m == "train dodge") modes.AddRange(new[] { "Blood Money", "Conquest", "Heist", "Capture The Bag", "Team Deathmatch", "Hotwire" });
                else if (m == "alcatraz") modes.AddRange(new[] { "Blood Money", "Heist", "Capture The Bag", "Conquest", "Team Deathmatch", "Conquest Large" });
                else if (m == "cemetery" || m == "thin ice") modes.AddRange(new[] { "Blood Money", "Heist", "Capture The Bag", "Conquest", "Team Deathmatch", "Hotwire" });
                else if (m == "chinatown") modes.AddRange(new[] { "Blood Money", "Conquest", "Capture The Bag", "Heist", "Team Deathmatch", "Conquest Large" });
                else modes.AddRange(new[] { "Crosshair", "Rescue" });
            }
            else
            {
                modes.AddRange(new[] { "Blood Money", "Heist", "Conquest", "Conquest Large", "Team Deathmatch", "Hotwire", "Capture The Bag", "Bounty Hunter" });
            }

            if (illegalMapModes.ContainsKey(mapName))
            {
                foreach (var illegal in illegalMapModes[mapName])
                {
                    modes.RemoveAll(x => x.Equals(illegal, StringComparison.OrdinalIgnoreCase));
                }
            }

            List<string> unique = new List<string>();
            foreach (var mode in modes)
            {
                if (!unique.Contains(mode)) unique.Add(mode);
            }
            return unique;
        }

        private void SendPlayerChat(string playerName, string message)
        {
            try { this.ExecuteCommand("procon.protected.send", "admin.say", message, "player", playerName); } catch { }
        }

        private void SendGlobalChat(string message)
        {
            try { this.ExecuteCommand("procon.protected.send", "admin.say", message, "all"); } catch { }
        }

        private void SendMultiLineGlobalChat(List<string> lines)
        {
            foreach (var line in lines) SendGlobalChat(line);
        }

        #endregion

        #region Helpers: Apply Next Map/Mode

        private string GetInternalMapName(string friendlyName)
        {
            if (string.IsNullOrEmpty(friendlyName)) return friendlyName;
            switch (friendlyName.ToLower())
            {
                case "downtown": return "MP_Downtown";
                case "the block": return "MP_Bloodout";
                case "derailed": return "MP_Eastside";
                case "dustbowl": return "MP_Desert05";
                case "bank job": return "MP_Bank";
                case "growhouse": return "MP_Growhouse";
                case "riptide": return "MP_Offshore";
                case "hollywoods heights":
                case "hollywood heights": return "MP_Hills";
                case "everglades": return "MP_Glades";
                case "black friday": return "XP1_Mallcops";
                case "code blue": return "XP1_Nights";
                case "the beat": return "XP1_Projects";
                case "backwoods": return "XP1_Sawmill";
                case "the docks": return "xp2_cargoship";
                case "break pointe":
                case "break point": return "xp2_coastal";
                case "museum": return "xp2_museum02";
                case "precinct 7": return "xp2_precinct7";
                case "night job": return "xp25_bank";
                case "night woods": return "xp25_sawmill";
                case "double cross": return "xp3_border";
                case "diversion": return "xp3_cistern02";
                case "pacific highway": return "xp3_highway";
                case "train dodge": return "xp3_traindodge";
                case "alcatraz": return "xp4_alcatraz";
                case "cemetery": return "xp4_cemetery";
                case "chinatown": return "xp4_chinatown";
                case "thin ice": return "xp4_snowcrash";
                default: return friendlyName;
            }
        }

        private string GetFriendlyMapName(string internalName)
        {
            if (string.IsNullOrEmpty(internalName)) return internalName;
            switch (internalName.ToLower())
            {
                case "mp_downtown": return "Downtown";
                case "mp_bloodout": return "The Block";
                case "mp_eastside": return "Derailed";
                case "mp_desert05": return "Dustbowl";
                case "mp_bank": return "Bank Job";
                case "mp_growhouse": return "Growhouse";
                case "mp_offshore": return "Riptide";
                case "mp_hills": return "Hollywoods Heights";
                case "mp_glades": return "Everglades";
                case "xp1_mallcops": return "Black Friday";
                case "xp1_nights": return "Code Blue";
                case "xp1_projects": return "The Beat";
                case "xp1_sawmill": return "Backwoods";
                case "xp2_cargoship": return "The Docks";
                case "xp2_coastal": return "Break pointe";
                case "xp2_museum02": return "Museum";
                case "xp2_precinct7": return "Precinct 7";
                case "xp25_bank": return "Night Job";
                case "xp25_sawmill": return "Night Woods";
                case "xp3_border": return "Double Cross";
                case "xp3_cistern02": return "Diversion";
                case "xp3_highway": return "Pacific Highway";
                case "xp3_traindodge": return "Train Dodge";
                case "xp4_alcatraz": return "Alcatraz";
                case "xp4_cemetery": return "Cemetery";
                case "xp4_chinatown": return "Chinatown";
                case "xp4_snowcrash": return "Thin Ice";
                default: return internalName;
            }
        }

        private string GetInternalModeName(string friendlyName)
        {
            switch (friendlyName.ToLower())
            {
                case "blood money": return "BloodMoney0";
                case "rescue": return "Hostage0";
                case "heist": return "Heist0";
                case "crosshair": return "Hit0";
                case "squad heist": return "SquadHeist0";
                case "conquest": return "TurfWarSmall0";
                case "conquest large": return "TurfWarLarge0";
                case "team deathmatch": return "TeamDeathMatch0";
                case "hotwire": return "Hotwire0";
                case "capture the bag": return "CaptureTheFlag0";
                case "bounty hunter": return "CashGrab0";
                default: return friendlyName;
            }
        }

        private string GetFriendlyModeName(string internalName)
        {
            switch (internalName.ToLower())
            {
                case "bloodmoney0":
                case "blood money": return "Blood Money";
                case "hostage0":
                case "rescue": return "Rescue";
                case "heist0":
                case "heist": return "Heist";
                case "hit0":
                case "crosshair": return "Crosshair";
                case "squadheist0":
                case "squad heist": return "Squad Heist";
                case "turfwarsmall0":
                case "conquest": return "Conquest";
                case "turfwarlarge0":
                case "conquest large": return "Conquest Large";
                case "teamdeathmatch0":
                case "team deathmatch": return "Team Deathmatch";
                case "hotwire0":
                case "hotwire": return "Hotwire";
                case "capturetheflag0":
                case "capture the bag": return "Capture The Bag";
                case "cashgrab0":
                case "bounty hunter": return "Bounty Hunter";
                default: return internalName;
            }
        }

        private void ApplyNextMapMidRound()
        {
            try
            {
                string internalMap = GetInternalMapName(winningMap);
                string internalMode = GetInternalModeName(winningGamemode);
                string injectedRounds = IsHeistOrSquadHeist(internalMode) ? "2" : "1";

                LogLive($"[CVotingSystem] [Live]: [Injection] Applying next map: {internalMap} [{internalMode}] with {injectedRounds} rounds");

                this.ExecuteCommand("procon.protected.send", "mapList.remove", "1");
                this.ExecuteCommand("procon.protected.send", "mapList.add", internalMap, internalMode, injectedRounds);
                this.ExecuteCommand("procon.protected.send", "mapList.setNextMapIndex", "1");
                
                this.ExecuteCommand("procon.protected.send", "mapList.save");
                this.ExecuteCommand("procon.protected.send", "mapList.list");

                isNextMapQueued = true;
            }
            catch (Exception ex)
            {
                LogLive("^1Error maplist injection update: " + ex.Message);
            }
        }

        #endregion
    }
}
