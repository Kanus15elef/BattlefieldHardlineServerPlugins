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

        // Timers
        private Timer roundStartDelayTimer;
        private Timer votingPeriodicTimer;
        private Timer phaseTransitionTimer;

        // Player Tracking
        private int currentPlayerCount = 0;
        private int minimumPlayersToVote = 4;
        private bool isWaitingForPlayers = false;

        // Loop & Round Tracker
        private string previousLevelName = string.Empty;
        private int currentMapLoadSequenceCount = 0;

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
        
        // Heist Round Trackers
        private bool isHeistRoundOne = false;

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

        #endregion

        #region Standard PRoCon Plugin API

        public CVotingSystem() { }

        public string GetPluginName() { return "CVotingSystem"; }
        public string GetPluginVersion() { return "1.1.26"; }
        public string GetPluginAuthor() { return "Yonatan (kanus15elef)"; }
        public string GetPluginWebsite() { return "localhost"; }
        public string GetPluginDescription() { return "Advanced Map & Gamemode Voting with Engine Loop Bypass & UI Sync."; }

        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            return new List<CPluginVariable>()
            {
                new CPluginVariable("minimumPlayersToVote", typeof(int), minimumPlayersToVote.ToString()),
                new CPluginVariable("mapVotingStartDelaySeconds", typeof(int), mapVotingStartDelaySeconds.ToString()),
                new CPluginVariable("mapVotingDurationSeconds", typeof(int), mapVotingDurationSeconds.ToString()),
                new CPluginVariable("modeVotingDurationSeconds", typeof(int), modeVotingDurationSeconds.ToString()),
                new CPluginVariable("votingStatusUpdateIntervalSeconds", typeof(int), votingStatusUpdateIntervalSeconds.ToString()),
                new CPluginVariable("delayBetweenMapAndModeVotingSeconds", typeof(int), delayBetweenMapAndModeVotingSeconds.ToString())
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
                switch (strVariable)
                {
                    case "minimumPlayersToVote": minimumPlayersToVote = Math.Max(1, int.Parse(strValue)); break;
                    case "mapVotingStartDelaySeconds": mapVotingStartDelaySeconds = Math.Max(1, int.Parse(strValue)); break;
                    case "mapVotingDurationSeconds": mapVotingDurationSeconds = Math.Max(1, int.Parse(strValue)); break;
                    case "modeVotingDurationSeconds": modeVotingDurationSeconds = Math.Max(1, int.Parse(strValue)); break;
                    case "votingStatusUpdateIntervalSeconds": votingStatusUpdateIntervalSeconds = Math.Max(1, int.Parse(strValue)); break;
                    case "delayBetweenMapAndModeVotingSeconds": delayBetweenMapAndModeVotingSeconds = Math.Max(1, int.Parse(strValue)); break;
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
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bCVotingSystem^n v1.1.26 Enabled!");
            ResetVotingState();
            currentPlayerCount = 0;
            isWaitingForPlayers = false;
            isHeistRoundOne = false;
            previousLevelName = string.Empty;
            currentMapLoadSequenceCount = 0;
            this.ExecuteCommand("procon.protected.send", "admin.listPlayers", "all");
        }

        public void OnPluginDisable()
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bCVotingSystem^n Disabled!");
            StopAllTimers();
        }

        #endregion

        #region Player Tracking & Minimum Players

        public void OnPlayerJoin(string strSoldierName)
        {
            lock (syncLock) 
            { 
                currentPlayerCount++; 
                CheckMinimumPlayers();
            }
        }

        public void OnPlayerLeft(CPlayerInfo playerInfo)
        {
            lock (syncLock) { currentPlayerCount = Math.Max(0, currentPlayerCount - 1); }
        }

        public void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        {
            lock (syncLock) 
            { 
                currentPlayerCount = players.Count; 
                CheckMinimumPlayers();
            }
        }

        private void CheckMinimumPlayers()
        {
            if (isWaitingForPlayers && currentPlayerCount >= minimumPlayersToVote && currentPhase == VotingPhase.Idle)
            {
                isWaitingForPlayers = false;
                this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Player threshold reached. Triggering voting countdown.");
                TriggerRoundStartSequence();
            }
        }

        #endregion

        #region Round & Timer Management

        private bool IsHeist(string gamemode)
        {
            if (string.IsNullOrEmpty(gamemode)) return false;
            return gamemode.Equals("Heist0", StringComparison.OrdinalIgnoreCase) ||
                   gamemode.Equals("Heist", StringComparison.OrdinalIgnoreCase);
        }

        public void OnLevelLoaded(string mapFileName, string Gamemode, int roundsPlayed, int roundsTotal)
        {
            lock (syncLock)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: Map loaded. Mode: {Gamemode}, RoundsPlayed: {roundsPlayed}");

                // 1. Consecutive Load Loop Tracker
                if (string.Equals(previousLevelName, mapFileName, StringComparison.OrdinalIgnoreCase))
                {
                    currentMapLoadSequenceCount++;
                }
                else
                {
                    previousLevelName = mapFileName;
                    currentMapLoadSequenceCount = 1;
                }

                this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: Map load sequence count for {mapFileName}: {currentMapLoadSequenceCount}");

                // If the engine maliciously loops back and reloads the same map a second consecutive time, force the next map!
                if (currentMapLoadSequenceCount >= 2)
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Detected infinite map reload bug loop. Forcing immediate map advancement.");
                    this.ExecuteCommand("procon.protected.send", "mapList.runNextMap");
                    return;
                }

                // Engine-safe Maplist Initialization: Clears the slate ONLY on fresh matches (roundsPlayed == 0)
                try
                {
                    if (roundsPlayed == 0 && currentMapLoadSequenceCount == 1)
                    {
                        string safeMode = string.IsNullOrEmpty(Gamemode) ? "Heist0" : Gamemode;
                        string defaultRounds = IsHeist(safeMode) || safeMode.Equals("SquadHeist0", StringComparison.OrdinalIgnoreCase) ? "2" : "1";
                        
                        this.ExecuteCommand("procon.protected.send", "mapList.clear");
                        this.ExecuteCommand("procon.protected.send", "mapList.add", mapFileName, safeMode, defaultRounds, "0");
                        this.ExecuteCommand("procon.protected.send", "mapList.save");
                        this.ExecuteCommand("procon.protected.send", "mapList.list");
                    }
                }
                catch (Exception ex)
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "^1Error initializing maplist: " + ex.Message);
                }

                // Handle Round-specific behavior
                if (roundsPlayed == 0)
                {
                    isHeistRoundOne = IsHeist(Gamemode);
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Round 1 started. Triggering voting countdown.");
                    TriggerRoundStartSequence();
                }
                else if (roundsPlayed == 1)
                {
                    isHeistRoundOne = false;
                    
                    if (currentPhase == VotingPhase.Idle)
                    {
                        this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Round 2 started and voting hasn't happened yet (fast rush). Triggering voting now.");
                        TriggerRoundStartSequence();
                    }
                    else
                    {
                        this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Round 2 started. Voting phase carries over from Round 1.");
                    }
                }
            }
        }

        public void OnRoundStart()
        {
            lock (syncLock)
            {
                if (currentPhase == VotingPhase.Idle && roundStartDelayTimer == null && !isWaitingForPlayers)
                {
                    TriggerRoundStartSequence();
                }
            }
        }

        private void TriggerRoundStartSequence()
        {
            lock (syncLock)
            {
                if (currentPlayerCount < minimumPlayersToVote)
                {
                    isWaitingForPlayers = true;
                    this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: Only {currentPlayerCount}/{minimumPlayersToVote} players. Voting postponed until threshold met.");
                    return;
                }

                isWaitingForPlayers = false;

                if (currentPhase == VotingPhase.Idle)
                {
                    ResetVotingState();
                }
                else
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Voting process is already running/persisted. Skipping timer reset.");
                    return;
                }

                double delayMs = mapVotingStartDelaySeconds * 1000.0;
                if (delayMs <= 0) delayMs = 1000.0;

                roundStartDelayTimer = new Timer(delayMs);
                roundStartDelayTimer.Elapsed += StartMapVotingPhase;
                roundStartDelayTimer.AutoReset = false;
                roundStartDelayTimer.Start();

                this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: New round sequence triggered. Map voting scheduled in {mapVotingStartDelaySeconds} seconds.");
            }
        }

        public void OnRoundOver(int winningTeamId)
        {
            lock (syncLock)
            {
                if (isHeistRoundOne && currentPhase != VotingPhase.GamemodeVotingEnded)
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "CVotingSystem: Heist Round 1 ended early. Active voting timers/state will persist into Round 2.");
                    isHeistRoundOne = false;
                    return;
                }

                if (currentPhase != VotingPhase.GamemodeVotingEnded && currentPhase != VotingPhase.Idle)
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: Round ended prematurely during phase '{currentPhase}'.");
                }

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
        }

        private void ResetVotingState()
        {
            StopAllTimers();
            currentPhase = VotingPhase.Idle;
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

            foreach (var kvp in mapNominations)
            {
                if (!activeMapPool.Contains(kvp.Value) && activeMapPool.Count < 8)
                {
                    activeMapPool.Add(kvp.Value);
                    candidateMaps.Remove(kvp.Value);
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

            SendGlobalChat("Map voting has finished the next map will be:");
            SendGlobalChat($"{winningMap} [{winningGamemode}] !");

            ApplyNextMapMidRound();
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

            bool isAdmin = strSpeaker.Equals("kanus15elef", StringComparison.OrdinalIgnoreCase);

            lock (syncLock)
            {
                if (cleanMessage.Equals("!votestart", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) { SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); return; }
                    isWaitingForPlayers = false; 
                    if (currentPhase == VotingPhase.Idle) { SendGlobalChat("Admin has forcefully started the voting process!"); StartMapVotingPhase(null, null); }
                    else { SendPlayerChat(strSpeaker, "Voting is already active or finished."); }
                    return;
                }

                if (cleanMessage.Equals("!voterefresh", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) { SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); return; }
                    ResetVotingState();
                    SendGlobalChat("all voting stages has been deleted and clear");
                    return;
                }

                if (cleanMessage.Equals("!voteend", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isAdmin) { SendPlayerChat(strSpeaker, "you don't have permission to execute those commands"); return; }
                    if (currentPhase == VotingPhase.MapVoting)
                    {
                        if (votingPeriodicTimer != null) { votingPeriodicTimer.Stop(); votingPeriodicTimer.Dispose(); votingPeriodicTimer = null; }
                        EndMapVoting();
                    }
                    else if (currentPhase == VotingPhase.GamemodeVoting)
                    {
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
                                SendGlobalChat($"{strSpeaker} has changed its voting from {oldMap} to {chosenMap} !");
                            }
                        }
                        else
                        {
                            playerCurrentMapVote[strSpeaker] = chosenMap;
                            mapVotes[chosenMap]++;
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
                                SendGlobalChat($"{strSpeaker} has changed its voting from the map {winningMap} [{oldMode}] to the map {winningMap} [{chosenMode}] !");
                            }
                        }
                        else
                        {
                            playerCurrentGamemodeVote[strSpeaker] = chosenMode;
                            gamemodeVotes[chosenMode]++;
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
                SendGlobalChat($"{playerName} has changed its nomination from {oldNom} to {matchedMap}");
            }
            else
            {
                if (mapNominations.Count >= 8) { SendPlayerChat(playerName, "All nomination slots have been taken you cant nominate for a map!"); return; }
                mapNominations[playerName] = matchedMap;
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
                SendGlobalChat($"{playerName} has changed its nomination gamemode nomination from {oldNom} to {matchedMode}");
            }
            else
            {
                if (gamemodeNominations.Count >= 8) { SendPlayerChat(playerName, "All nomination slots have been taken you cant nominate for a gamemode!"); return; }
                gamemodeNominations[playerName] = matchedMode;
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
            switch (friendlyName.ToLower())
            {
                case "bank job": return "MP_Bank";
                case "derailed": return "MP_Derailed";
                case "downtown": return "MP_Downtown";
                case "dustbowl": return "MP_Dustbowl";
                case "everglades": return "MP_Everglades";
                case "growhouse": return "MP_Growhouse";
                case "hollywoods heights": return "MP_Hollywood";
                case "night job": return "MP_Bank_Night";
                case "night woods": return "MP_Eastside_Night";
                case "riptide": return "MP_Bayside";
                case "the block": return "MP_Bloodout";
                case "backwoods": return "XP1_Backwoods";
                case "black friday": return "XP2_Mall";
                case "code blue": return "XP2_Precinct";
                case "the beat": return "XP2_TheBeat";
                case "break pointe": return "XP3_Border";
                case "museum": return "XP3_Museum";
                case "precinct 7": return "XP3_Precinct7";
                case "the docks": return "XP3_Docks";
                case "diversion": return "XP4_Diversion";
                case "double cross": return "XP4_DoubleCross";
                case "pacific highway": return "XP4_PacificHwy";
                case "train dodge": return "XP4_TrainDodge";
                case "alcatraz": return "XP4_Alcatraz";
                case "chinatown": return "XP4_Chinatown";
                case "cemetery": return "XP4_Cemetery";
                case "thin ice": return "XP4_ThinIce";
                default: return friendlyName;
            }
        }

        private string GetInternalModeName(string friendlyName)
        {
            switch (friendlyName.ToLower())
            {
                case "blood money": return "BloodMoney0";
                case "rescue": return "Rescue0";
                case "heist": return "Heist0";
                case "crosshair": return "Crosshair0";
                case "squad heist": return "SquadHeist0";
                case "conquest": return "ConquestSmall0";
                case "conquest large": return "ConquestLarge0";
                case "team deathmatch": return "TeamDeathMatch0";
                case "hotwire": return "Hotwire0";
                case "capture the bag": return "CaptureTheBag0";
                case "bounty hunter": return "BountyHunter0";
                default: return friendlyName;
            }
        }

        private void ApplyNextMapMidRound()
        {
            try
            {
                string internalMap = GetInternalMapName(winningMap);
                string internalMode = GetInternalModeName(winningGamemode);
                
                string injectedRounds = IsHeist(internalMode) || internalMode.Equals("SquadHeist0", StringComparison.OrdinalIgnoreCase) ? "2" : "1";

                this.ExecuteCommand("procon.protected.pluginconsole.write", $"CVotingSystem: [Mid-Round] Injecting UI map update: {internalMap} [{internalMode}] at index 1");

                this.ExecuteCommand("procon.protected.send", "mapList.add", internalMap, internalMode, injectedRounds, "1");
                this.ExecuteCommand("procon.protected.send", "mapList.save");
                this.ExecuteCommand("procon.protected.send", "mapList.list");
            }
            catch (Exception ex)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", "^1Error mid-round maplist update: " + ex.Message);
            }
        }

        #endregion
    }
}