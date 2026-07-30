So those we have 2 plugins here that have been written by me and with the support of ai ( gemini and copilot )  heres an explanations on what each plugins do and how it is working:

CSquadAutoBalancer.cs : this plugin is a balancing plugin for battlefield hardline which containing few cool features to improve player experience on the server either if you are solo or playing with friends to keep the player engage in fair matches while making sure the teams are always balanced to the maximum while keeping every match different from last match so how the plugin work you may ask before that i will explain the features for better understanding the core of the plugin:

1. squad feature: so everyone who plays with friends on a battlefield server whether its hardline/4/3  we hate that every time match is ending or mid match the the players are being separated to each team that can cause a huge frustration and even make the players want to leave to prevent that i created the next commands under the family of commands !squad in general how it is working when a player type the command !squad create "name of the squad" it will create in the auto balancing plugin a squad which will have the next features: (important to say squad limit is 5)

a. All players that are in the squad always after the end of the round will be forced to be on the same in game squad for example if all players are in a squad and are separated on different in game for example 2 on alpha 2 on bravo 1 on charlie  after the auto balancer is triggered all players from that squad will be forced to be on the same squad in this case it will choose alpha

b. When auto balancer is triggered it will always switch (if the auto balancer see a reason) all of the 5 players team to the same squad keeping squad integrity as the top priority 
*important notes
1. if there arent enough players on the server the squad will prioritize both teams to have the same amount of players and when an opportunity present it self a player can type !assist to switch to his current squad

c. useful commands:
!squadcreate "name of the squad" : creating squad with the chosen name of the player
!squadmembers : showing the the players that are in your current squad
!squadinvite "playername" : letting you invite a player of your choice to your squad
!squadaccept : accepting invitation to a squad (invitation is expired after 60 seconds)
!squadreject: rejecting invitation to a squad
!squadkick: a tool to help squad leader kick players from created squad

2. playerscore : i introduced a feature called player score which will combine from a few elements K/D, Kills, Score
and default calculations are the next:
1 Kill = 5 playerscore points
0.1 K/D = 1 playerscore point
100 score = 1 player score

the player score is the most important feature of this plugin that will insure the best possible view from plugin point of view who are the most influential players on the server for the calculations for the team balancing.

So when a round is over what the plugin will do is like shuffling cards and reorganizing it will put random players in 2 different teams ( while keeping the same squad together at all given time) and than it start organizing while try to keep both team score as close as possible the team score is just the combined all player score on that team and the plugin will switch player back and forth to see the best way possible to balance the team while doing the next thing on the order from most important to least:
1. keeping teams player count equal
2. keeping players in the same squad
3. ensuring that after balancing team over all player score for team A and team B are the closest as possible
*Important note : yea i know the plugin can be abused by the 5 best players on the server forcing themselves on the same squad but the plugin will "punish them" by putting all the "bad" players on their team and overall for that special case im not gonna remove the squad feature

the balancing will happen in a blink of an eye so all the switching back an forth will not be even noticed by the players so that is good :) i hope XD



CVotingSystem.cs: This plugin is my view of how voting should work in a battlefield server the plugin have some few features to im gonna list them down:

1. !nom system: !nom command have 2 special cases for:

a. before the voting starts instead of giving just 8 random maps (man i hate train dodge :( please no ) the player has the option to nominate for a map of his choice for an example if the voting hasnt started if i type !nommap the block the game will say that you have nominated for your desired map and it will show on the vote if all 8 nomination slots have been taken no random maps will appear and only nomination map will for voting and than player can vote for a map of their choice while voting is running by typing 1 to 8

b. before gamemode voting will start a player have the option to vote for a game mode of their choice by using the command !nomgamemode "name of the gamemode" if the map that was chosen have that game mode for an example if a player type !nomgamemmode hotwire and the map that was chosen have the option for that gamemode for an example downtown the gamemode will appear on the voting for gamemode but if a map that doesnt have this game mode for example the block gamemode will not appear on the gamemode voting

2. algorithm of maps and game mode for certain amounts of players: im not gonna say what gamemode for what map for what amount of players because the algorithm is to long just to know that its there

3. if there arent enough player on the server for the algorithm to give the option for a certain gamemode for example the game will not run conquest large on certain maps if their arent enough players and the players still want this mode they can nominate for it and it will show on the voting no matter what

so how the voting is working? it has 2 stage map voting and gamemode voting

pre stage 1: before map voting is starting there is a window of 180 seconds (can be changed) to nominate for a maps and game mode once this stage ends no map will be able to nominate for and stage 1 will start

stage 1: map voting 8 maps will appear nominated and randoms ( if all 8 nominations slots has not been taken ) for the players to vote

when stage 1 ends and before stage 2 starts theres a very short window to nominate for a game mode

stage 2: gamemode voting for the winning map the options will be gamemodes that are suited best for the player count on the server and nominated gamemodes 

after stage 2 is ended a map and game mode will picked as a winner and will play on the next round

bonus : a player can type !nextmap and it will say in chat whats the next map in case of the player not knowing whats map was chosen


so thats for the plugins the plugins can still have bugs you can report them to me through github so i will make changes to it and patch them if you support my work and want to donate you can ask in the github and we will find a way ( i have no idea how to do it but yea ) special thanks to the play testers and bug testers :

Kanus15elefV2,
Kanus15elefV3,
Doucef_moy ,
TacobellsJr,
North

** This plugin isnt allowed to be sold in any shape or form  
