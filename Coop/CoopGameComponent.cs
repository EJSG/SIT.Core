﻿using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SIT.Coop.Core.Matchmaker;
using SIT.Coop.Core.Player;
using SIT.Tarkov.Core;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

namespace SIT.Core.Coop
{
    /// <summary>
    /// Coop Game Component is the User 1-2-1 communication to the Server
    /// </summary>
    public class CoopGameComponent : MonoBehaviour
    {
        #region Fields/Properties        
        public WorldInteractiveObject[] ListOfInteractiveObjects { get; set; }
        private Request RequestingObj { get; set; }
        public string ServerId { get; set; } = null;
        public ConcurrentDictionary<string, LocalPlayer> Players { get; private set; } = new();
        public ConcurrentQueue<Dictionary<string, object>> QueuedPackets { get; } = new();

        BepInEx.Logging.ManualLogSource Logger { get; set; }

        public static Vector3? ClientSpawnLocation { get; set; }

        private long ReadFromServerLastActionsLastTime { get; set; } = -1;

        public ConcurrentDictionary<string, ESpawnState> PlayersToSpawn { get; private set; } = new();

        #endregion

        #region Public Voids
        public static CoopGameComponent GetCoopGameComponent()
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return null;

            var coopGC = gameWorld.GetComponent<CoopGameComponent>();
            return coopGC;
        }
        public static string GetServerId()
        {
            var coopGC = GetCoopGameComponent();
            if (coopGC == null)
                return null;

            return coopGC.ServerId;
        }
        #endregion

        #region Unity Component Methods

        void Awake()
        {

            // ----------------------------------------------------
            // Create a BepInEx Logger for CoopGameComponent
            Logger = BepInEx.Logging.Logger.CreateLogSource("CoopGameComponent");

        }

        void Start()
        {
            Logger.LogInfo("CoopGameComponent:Start");

            // ----------------------------------------------------
            // Always clear "Players" when creating a new CoopGameComponent
            Players = new ConcurrentDictionary<string, LocalPlayer>();

            StartCoroutine(ReadFromServerLastActions());
            StartCoroutine(ReadFromServerLastMoves());
            StartCoroutine(ReadFromServerCharacters());

            ListOfInteractiveObjects = FindObjectsOfType<WorldInteractiveObject>();
            PatchConstants.Logger.LogInfo($"Found {ListOfInteractiveObjects.Length} interactive objects");

        }

        #endregion

        private IEnumerator ReadFromServerCharacters()
        {
            var waitEndOfFrame = new WaitForEndOfFrame();

            if (GetServerId() == null)
                yield return waitEndOfFrame;

            var waitSeconds = new WaitForSeconds(10f);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d.Add("serverId", GetServerId());
            d.Add("currentPlayerList", null);
            while (true)
            {
                yield return waitSeconds;

                if (Players == null)
                    continue;

                d["currentPlayerList"] = Players.Keys.ToArray();
                var jsonDataToSend = d.ToJson();

                if (RequestingObj == null)
                    RequestingObj = new Request();

                var actionsToValuesJson = RequestingObj.PostJson("/coop/server/read/players", jsonDataToSend);
                if (actionsToValuesJson == null)
                    continue;

                //Logger.LogDebug(actionsToValuesJson);
                try
                {
                    Dictionary<string, object>[] actionsToValues = JsonConvert.DeserializeObject<Dictionary<string, object>[]>(actionsToValuesJson);
                    if (actionsToValues == null)
                        continue;

                    Logger.LogDebug($"CoopGameComponent.ReadFromServerCharacters:{actionsToValues.Length}");

                    var packets = actionsToValues
                         .Where(x => x != null)
                         .Select(x => x);
                    if (packets == null)
                        continue;

                    foreach (var queuedPacket in packets)
                    {
                        if (queuedPacket != null && queuedPacket.Count > 0)
                        {
                            if (queuedPacket != null)
                            {
                                if (queuedPacket.ContainsKey("m"))
                                {
                                    var method = queuedPacket["m"].ToString();
                                    if (method != "PlayerSpawn")
                                        continue;
                                   
                                    string accountId = queuedPacket["accountId"].ToString();
                                    // TODO: Put this back in after testing in Creation functions
                                    //if (Players == null || Players.ContainsKey(accountId))
                                    //{
                                    //    Logger.LogDebug($"Ignoring call to Spawn player {accountId}. The player already exists in the game.");
                                    //    continue;
                                    //}

                                    Vector3 newPosition = Players.First().Value.Position;
                                    if (queuedPacket.ContainsKey("sPx")
                                        && queuedPacket.ContainsKey("sPy")
                                        && queuedPacket.ContainsKey("sPz"))
                                    {
                                        string npxString = queuedPacket["sPx"].ToString();
                                        newPosition.x = float.Parse(npxString);
                                        string npyString = queuedPacket["sPy"].ToString();
                                        newPosition.y = float.Parse(npyString);
                                        string npzString = queuedPacket["sPz"].ToString();
                                        newPosition.z = float.Parse(npzString) + 0.5f;

                                        //QuickLog("New Position found for Spawning Player");
                                    }
                                    PlayerBotSpawn(queuedPacket, accountId, queuedPacket["profileId"].ToString(), newPosition, false);


                                }
                            }
                        }
                    }
                }
                catch(Exception ex) 
                {

                    Logger.LogError(ex.ToString());
                
                }
                finally
                {

                }

                actionsToValuesJson = null;
                yield return waitEndOfFrame;

            }
        }

        private void PlayerBotSpawn(Dictionary<string, object> parsedDict, string accountId, string profileId, Vector3 newPosition, bool isBot)
        {
            Profile profile = MatchmakerAcceptPatches.Profile.Clone();
            profile.AccountId = accountId;

            if (PlayersToSpawn.ContainsKey(accountId)) // CreatePhysicalOtherPlayerOrBot has been done before?
            {
                var newPlayer = CreatePhysicalOtherPlayerOrBot(profile, newPosition);
                if (newPlayer != null)
                {
                    Logger.LogDebug($"DataReceivedClient_PlayerBotSpawn::Spawned:: {profile.Info.Nickname}");
                    Players.TryAdd(accountId, newPlayer);
                }
            }

            try
            {
                Logger.LogDebug("DataReceivedClient_PlayerBotSpawn:: Adding " + accountId + " to spawner list");
                profile.Id = accountId;
                profile.Info.Nickname = "Nikita " + Players.Count;
                profile.Info.Side = isBot ? EPlayerSide.Savage : EPlayerSide.Usec;
                if (parsedDict.ContainsKey("p.info"))
                {
                    Logger.LogDebug("DataReceivedClient_PlayerBotSpawn:: Converting Profile data");
                    profile.Info = parsedDict["p.info"].ToString().ParseJsonTo<ProfileInfo>(Array.Empty<JsonConverter>());
                    //profile.Info = JsonConvert.DeserializeObject<ProfileInfo>(parsedDict["p.info"].ToString());// PatchConstants.SITParseJson<ProfileInfo>(parsedDict["p.info"].ToString());//.ParseJsonTo<ProfileData>(Array.Empty<JsonConverter>());
                    //profile.Info = JsonConvert.DeserializeObject<ProfileInfo>(parsedDict["p.info"].ToString());// PatchConstants.SITParseJson<ProfileInfo>(parsedDict["p.info"].ToString());//.ParseJsonTo<ProfileData>(Array.Empty<JsonConverter>());
                    Logger.LogDebug("DataReceivedClient_PlayerBotSpawn:: Converted Profile data:: Hello " + profile.Info.Nickname);
                }
                if (parsedDict.ContainsKey("p.cust"))
                {
                    var parsedCust = parsedDict["p.cust"].ToString().ParseJsonTo<Dictionary<EBodyModelPart, string>>(Array.Empty<JsonConverter>());
                    if (parsedCust != null && parsedCust.Any())
                    {
                        PatchConstants.SetFieldOrPropertyFromInstance(
                            profile
                            , "Customization"
                            , Activator.CreateInstance(PatchConstants.TypeDictionary["Profile.Customization"], parsedCust)
                            );
                        Logger.LogDebug("DataReceivedClient_PlayerBotSpawn:: Set Profile Customization for " + profile.Info.Nickname);

                    }
                }
                if (parsedDict.ContainsKey("p.equip"))
                {
                    var pEquip = parsedDict["p.equip"].ToString();
                    //var equipment = parsedDict["p.equip"].ToString().ParseJsonTo<Equipment>(Array.Empty<JsonConverter>());
                    var equipment = parsedDict["p.equip"].ToString().SITParseJson<Equipment>();//.ParseJsonTo<Equipment>(Array.Empty<JsonConverter>());
                    profile.Inventory.Equipment = equipment;
                    Logger.LogDebug("DataReceivedClient_PlayerBotSpawn:: Set Equipment for " + profile.Info.Nickname);

                }
                if (parsedDict.ContainsKey("isHost"))
                {
                }

                var newPlayer = CreatePhysicalOtherPlayerOrBot(profile, newPosition);
                if (newPlayer != null)
                {
                    Logger.LogDebug($"DataReceivedClient_PlayerBotSpawn::Spawned:: {profile.Info.Nickname}");
                    Players.TryAdd(accountId, newPlayer);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"DataReceivedClient_PlayerBotSpawn::ERROR::" + ex.Message);
            }

        }

        private LocalPlayer CreatePhysicalOtherPlayerOrBot(Profile profile, Vector3 position)
        {
            try
            {
                EFT.Player.EUpdateMode armsUpdateMode = EFT.Player.EUpdateMode.Auto;
                EFT.Player.EUpdateMode bodyUpdateMode = EFT.Player.EUpdateMode.Auto;
                
                if (Players == null)
                {
                    Logger.LogError("Players is NULL!");
                    return null;
                }

                int playerId = Players.Count + 1;
                if (profile == null)
                {
                    Logger.LogError("CreatePhysicalOtherPlayerOrBot Profile is NULL!");
                    return null;
                }

                PlayersToSpawn.TryAdd(profile.AccountId, ESpawnState.None);
                if (PlayersToSpawn[profile.AccountId] == ESpawnState.None)
                {
                    Logger.LogDebug($"CreatePhysicalOtherPlayerOrBot::{profile.Info.Nickname}::Loading...");
                    PlayersToSpawn[profile.AccountId] = ESpawnState.Loading;
                    IEnumerable<ResourceKey> allPrefabPaths = profile.GetAllPrefabPaths();
                    if (allPrefabPaths.Count() == 0)
                    {
                        Logger.LogError($"CreatePhysicalOtherPlayerOrBot::{profile.Info.Nickname}::PrefabPaths are empty!");
                        PlayersToSpawn[profile.AccountId] = ESpawnState.Error;
                        return null;
                    }

                    Singleton<PoolManager>.Instance.LoadBundlesAndCreatePools(PoolManager.PoolsCategory.Raid, PoolManager.AssemblyType.Local, allPrefabPaths.ToArray(), JobPriority.General)
                        .ContinueWith(delegate
                        {
                            PlayersToSpawn[profile.AccountId] = ESpawnState.Spawning;
                            Logger.LogDebug($"CreatePhysicalOtherPlayerOrBot::{profile.Info.Nickname}::Load Complete.");
                        });

                    return null;
                }

                // Its loading on the previous pass, ignore this one until its finished
                if (PlayersToSpawn[profile.AccountId] == ESpawnState.Loading)
                {
                    Logger.LogDebug($"CreatePhysicalOtherPlayerOrBot::{profile.Info.Nickname}::Is still Loading");

                    return null;
                }

                // It has already spawned, we should never reach this point if Players check is working in previous step
                if (PlayersToSpawn[profile.AccountId] == ESpawnState.Spawned)
                {
                    Logger.LogDebug($"CreatePhysicalOtherPlayerOrBot::{profile.Info.Nickname}::Is already spawned");
                    return null;
                }

                Logger.LogDebug("CreatePhysicalOtherPlayerOrBot: Attempting to Create Player " + profile.Info.Nickname);
          
                LocalPlayer localPlayer = LocalPlayer.Create(playerId
                    , position
                    , Quaternion.identity
                    ,
                    "Player",
                    ""
                    , EPointOfView.ThirdPerson
                    , profile
                    , aiControl: false
                    , EUpdateQueue.Update
                    , armsUpdateMode
                    , EFT.Player.EUpdateMode.Auto
                    , GClass577.Config.CharacterController.ClientPlayerMode
                    , () => Singleton<OriginalSettings>.Instance.Control.Settings.MouseSensitivity
                    , () => Singleton<OriginalSettings>.Instance.Control.Settings.MouseAimingSensitivity
                    , new GClass1698()
                    , new GClass1390()
                    , null
                    , isYourPlayer: false).Result;

                PlayersToSpawn[profile.AccountId] = ESpawnState.Spawned;

                Logger.LogDebug("CreatePhysicalOtherPlayerOrBot: Created Player " + profile.Info.Nickname);

                //LocalPlayer localPlayer = LocalPlayer.Create(
                //        playerId
                //        , position + new Vector3(0, 1, 1)
                //        , Quaternion.identity
                //        , "Player"
                //        , ""
                //        , EPointOfView.ThirdPerson
                //        , profile
                //        , false
                //        , EUpdateQueue.Update
                //        , armsUpdateMode
                //        , bodyUpdateMode
                //        , PatchConstants.CharacterControllerSettings.ClientPlayerMode
                //        , () => 1f
                //        , () => 1f
                //        , (IStatisticsManager)Activator.CreateInstance(PatchConstants.TypeDictionary["StatisticsSession"])
                //        , default(GInterface82)
                //        , null
                //        , false
                //    ).Result;
                //localPlayer.Transform.position = position;
                return localPlayer;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.ToString());
            }

            return null;
        }

        private TMP_FPSCounter fpsCounter { get; set; }

        /// <summary>
        /// Gets the Last Actions Dictionary from the Server. This should not be used for things like Moves. Just other stuff.
        /// </summary>
        /// <returns></returns>
        private IEnumerator ReadFromServerLastActions()
        {
            Stopwatch swDebugPerformance = new Stopwatch();
            Stopwatch swRequests = new Stopwatch();

            var waitEndOfFrame = new WaitForEndOfFrame();

            if (GetServerId() == null)
                yield return waitEndOfFrame;

            var waitSeconds = new WaitForSeconds(0.5f);

            var jsonDataServerId = new Dictionary<string, object>
            {
                { "serverId", GetServerId() },
                { "t", ReadFromServerLastActionsLastTime }
            };
            while (true)
            {
                yield return waitSeconds;

                jsonDataServerId["t"] = ReadFromServerLastActionsLastTime;
                //if (fpsCounter == null)
                //{
                //    fpsCounter = GameObject.FindObjectOfType<TMP_FPSCounter>();
                //    if (fpsCounter != null)
                //    {
                //        //GameObject.DontDestroyOnLoad(fpsCounter);
                //        PatchConstants.Logger.LogInfo("CoopGameComponent:Found FPS Counter");
                //    }
                //}
                swDebugPerformance.Reset();
                swDebugPerformance.Start();
                if (Players == null)
                {
                    PatchConstants.Logger.LogInfo("CoopGameComponent:No Players Found! Nothing to process!");
                    yield return waitSeconds;
                    continue;
                }

                if (RequestingObj == null)
                    RequestingObj = new Request();

                try
                {
                    //Task.Run(async() =>
                    {
                        swRequests.Reset();
                        swRequests.Start();
                        var actionsToValuesJson = RequestingObj.PostJsonAsync("/coop/server/read/lastActions", jsonDataServerId.ToJson()).Result;
                        ReadFromServerLastActionsParseData(actionsToValuesJson);
                        actionsToValuesJson = null;
                    }
                    //);
                }
                finally
                {

                }

                swRequests.Stop();
                //Logger.LogInfo($"CoopGameComponent.ReadFromServerLastActions took {swRequests.ElapsedMilliseconds}ms");
                if (fpsCounter != null)
                {

                }

            }
        }

        public void ReadFromServerLastActionsParseData(string actionsToValuesJson)
        {
            if (actionsToValuesJson == null)
            {
                PatchConstants.Logger.LogInfo("CoopGameComponent:No Data Returned from Last Actions!");
                return;
            }

            //Logger.LogInfo($"CoopGameComponent:ReadFromServerLastActions:{actionsToValuesJson}");
            Dictionary<string, JObject> actionsToValues = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(actionsToValuesJson);
            if (actionsToValues == null)
            {
                return;

            }

            var packets = actionsToValues.Values
                 .Where(x => x != null)
                 .Where(x => x.Count > 0)
                 .Select(x => x.ToObject<Dictionary<string, object>>());
            //.Where(x => x.ContainsKey("m") && x["m"].ToString() != "Move")
            //.Where(x => x.ContainsKey("accountId"));
            if (packets == null)
            {
                //PatchConstants.Logger.LogInfo("CoopGameComponent:No Data Returned from Last Actions!");
                return;

            }

            if (!packets.Any())
            {
                //PatchConstants.Logger.LogInfo("CoopGameComponent:No Data Returned from Last Actions!");
                return;

            }

            //// go through all items apart from "Move"
            //foreach (var packet in packets.Where(x => x.ContainsKey("m") && x["m"].ToString() != "Move"))
            foreach (var packet in packets.Where(x => x.ContainsKey("m")))
            {
                if (packet != null && packet.Count > 0)
                {
                    var accountId = packet["accountId"].ToString();
                    //if (dictionary.ContainsKey("accountId"))
                    {
                        if (!Players.ContainsKey(accountId))
                        {
                            PatchConstants.Logger.LogInfo($"CoopGameComponent:Players does not contain {accountId}. Searching. This is SLOW. FIXME! Don't do this!");
                            foreach (var p in FindObjectsOfType<LocalPlayer>())
                            {
                                if (!Players.ContainsKey(p.Profile.AccountId))
                                {
                                    Players.TryAdd(p.Profile.AccountId, p);
                                    var nPRC = p.GetOrAddComponent<PlayerReplicatedComponent>();
                                    nPRC.player = p;
                                }
                            }
                            continue;
                        }

                        if (!Players[packet["accountId"].ToString()].TryGetComponent<PlayerReplicatedComponent>(out var prc))
                        {
                            PatchConstants.Logger.LogInfo($"CoopGameComponent:{accountId} does not have a PlayerReplicatedComponent");
                            continue;
                        }

                        if (prc == null)
                            continue;

                        //if (prc.QueuedPackets == null)
                        //    continue;

                        //prc.QueuedPackets.Enqueue(dictionary);

                        prc.HandlePacket(packet);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the Last Moves Dictionary from the Server. This should be the last move action each account id (character) made
        /// </summary>
        /// <returns></returns>
        private IEnumerator ReadFromServerLastMoves()
        {
            var waitEndOfFrame = new WaitForEndOfFrame();
            var waitSeconds = new WaitForSeconds(1f);

            if (GetServerId() == null)
                yield return waitSeconds;

            var jsonDataServerId = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                { "serverId", GetServerId() }
            });
            while (true)
            {
                yield return waitSeconds;

                if (Players == null)
                    continue;

                if (RequestingObj == null)
                    RequestingObj = new Request();

                try
                {
                    var actionsToValuesJson = RequestingObj.PostJsonAsync("/coop/server/read/lastMoves", jsonDataServerId).Result;
                    ReadFromServerLastMoves_ParseData(actionsToValuesJson);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex.ToString());
                }
                //yield return waitEndOfFrame;

            }
        }

        public void ReadFromServerLastMoves_ParseData(string actionsToValuesJson)
        {
            if (actionsToValuesJson == null)
                return;

            try
            {
                //Logger.LogInfo($"CoopGameComponent:ReadFromServerLastMoves:{actionsToValuesJson}");
                Dictionary<string, JObject> actionsToValues = JsonConvert.DeserializeObject<Dictionary<string, JObject>>(actionsToValuesJson);
                if (actionsToValues == null)
                    return;

                var packets = actionsToValues.Values
                    .Where(x => x != null)
                    .Where(x => x.Count > 0)
                    .Select(x => x.ToObject<Dictionary<string, object>>());
                if (packets == null)
                    return;

                foreach (var packet in packets)
                {
                    if (packet != null && packet.Count > 0)
                    {
                        if (!packet.ContainsKey("accountId"))
                            continue;

                        if (Players == null)
                            continue;

                        if (!Players.ContainsKey(packet["accountId"].ToString()))
                            continue;

                        if (!Players[packet["accountId"].ToString()].TryGetComponent<PlayerReplicatedComponent>(out var prc))
                            continue;

                        if (prc == null)
                            continue;

                        PlayerOnMovePatch.MoveReplicated(prc.player, packet);
                    }
                }
            }
            finally
            {

            }
        }

        //private void ServerCommunication_OnDataReceived(byte[] buffer)
        //{
        //    if (buffer.Length == 0)
        //        return;

        //    try
        //    {
        //        //string @string = streamReader.ReadToEnd();
        //        string @string = Encoding.UTF8.GetString(buffer);

        //        if (@string.Length == 4)
        //        {
        //            return;
        //        }
        //        else
        //        {
        //            //Task.Run(() =>
        //            {
        //                if (@string.Length == 0)
        //                    return;

        //                //Logger.LogInfo($"CoopGameComponent:OnDataReceived:{buffer.Length}");
        //                //Logger.LogInfo($"CoopGameComponent:OnDataReceived:{@string}");

        //                if (@string[0] == '{' && @string[@string.Length - 1] == '}')
        //                {
        //                    //var dictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(@string)
        //                    var dictionary = JsonConvert.DeserializeObject<Dictionary<string, object>>(@string);

        //                    if (dictionary != null && dictionary.Count > 0)
        //                    {
        //                        if (dictionary.ContainsKey("SERVER"))
        //                        {
        //                            //Logger.LogInfo($"LocalGameStartingPatch:OnDataReceived:SERVER:{buffer.Length}");
        //                            QueuedPackets.Enqueue(dictionary);
        //                        }
        //                        else if (dictionary.ContainsKey("m"))
        //                        {
        //                            if (dictionary["m"].ToString() == "HostDied")
        //                            {
        //                                Logger.LogInfo("Host Died");
        //                                //if (MatchmakerAcceptPatches.IsClient)
        //                                //    LocalGameEndingPatch.EndSession(LocalGamePatches.LocalGameInstance, LocalGamePatches.MyPlayerProfile.Id, ExitStatus.Survived, "", 0);
        //                            }

        //                            if (dictionary.ContainsKey("accountId"))
        //                            {
        //                                //Logger.LogInfo(dictionary["accountId"]);

        //                                if (Players.ContainsKey(dictionary["accountId"].ToString()))
        //                                {
        //                                    var prc = Players[dictionary["accountId"].ToString()].GetComponent<PlayerReplicatedComponent>();
        //                                    if (prc == null)
        //                                        return;




        //                                    //prc.QueuedPackets.Enqueue(dictionary);
        //                                }
        //                            }
        //                        }
        //                        else
        //                        {
        //                            //Logger.LogInfo($"ServerCommunication_OnDataReceived:Unhandled:{@string}");
        //                        }
        //                    }
        //                }
        //                else if (@string[0] == '[' && @string[@string.Length - 1] == ']')
        //                {
        //                    foreach (var item in JsonConvert.DeserializeObject<object[]>(@string).Select(x => JsonConvert.SerializeObject(x)))
        //                        ServerCommunication_OnDataReceived(Encoding.UTF8.GetBytes(item));
        //                }
        //                else
        //                {
        //                    Logger.LogInfo($"ServerCommunication_OnDataReceived:Unhandled:{@string}");
        //                }
        //            }
        //            //);
        //        }
        //    }
        //    catch (Exception)
        //    {
        //        return;
        //    }
        //}

    }

    public enum ESpawnState
    {
        None = 0,
        Loading = 1,
        Spawning = 2,
        Spawned = 3,
        Error = 99,
    }
}
