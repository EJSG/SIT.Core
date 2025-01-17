﻿#pragma warning disable CS0618 // Type or member is obsolete
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using SIT.Coop.Core.Web;
using SIT.Core.Coop;
using SIT.Core.Coop.Components;
using SIT.Core.Coop.Player.FirearmControllerPatches;
using SIT.Core.Misc;
using SIT.Core.SP.Raid;
using SIT.Tarkov.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using UnityEngine;

namespace SIT.Coop.Core.Player
{
    /// <summary>
    /// Player Replicated Component is the Player/AI direct communication to the Server
    /// </summary>
    internal class PlayerReplicatedComponent : MonoBehaviour, IPlayerPacketHandlerComponent
    {
        internal const int PacketTimeoutInSeconds = 1;
        //internal ConcurrentQueue<Dictionary<string, object>> QueuedPackets { get; } = new();
        internal Dictionary<string, object> LastMovementPacket { get; set; }
        internal EFT.LocalPlayer player { get; set; }
        public bool IsMyPlayer { get { return player != null && player.IsYourPlayer; } }
        public bool IsClientDrone { get; internal set; }

        void Awake()
        {
            //PatchConstants.Logger.LogDebug("PlayerReplicatedComponent:Awake");
        }
        
        void Start()
        {
            //PatchConstants.Logger.LogDebug($"PlayerReplicatedComponent:Start");

            if (player == null)
            {
                player = this.GetComponentInParent<EFT.LocalPlayer>();
                PatchConstants.Logger.LogDebug($"PlayerReplicatedComponent:Start:Set Player to {player}");
            }

            // ---------------------------------------------------------
            // TODO: Add Dogtags to PMC Clients in match
            if(player.ProfileId.StartsWith("pmc"))
            {
                if(UpdateDogtagPatch.GetDogtagItem(player) == null)
                {
                    var dogtagSlot = player.Inventory.Equipment.GetSlot(EFT.InventoryLogic.EquipmentSlot.Dogtag);
                    //var dogtagItemComponent = dogtagSlot.Add(new DogtagComponent(new Item("")));
                }
            }

            GCHelpers.EnableGC();
        }

        public void ProcessPacket(Dictionary<string, object> packet)
        {
            var method = packet["m"].ToString();

            var patch = ModuleReplicationPatch.Patches.FirstOrDefault(x => x.MethodName == method);
            if (patch != null)
            {
                // Early bird stop to processing the same item twice!
                //if (!ModuleReplicationPatch.HasProcessed(patch.GetType(), player, packet))
                    patch.Replicated(player, packet);

                return;
            }

            ProcessPlayerState(packet);

            var packetHandlerComponents = this.GetComponents<IPlayerPacketHandlerComponent>();
            if (packetHandlerComponents != null)
            {
                packetHandlerComponents = packetHandlerComponents.Where(x => x.GetType() != typeof(PlayerReplicatedComponent)).ToArray();
                foreach (var packetHandlerComponent in packetHandlerComponents)
                {
                    packetHandlerComponent.ProcessPacket(packet);
                }
            }
        }

        void ProcessPlayerState(Dictionary<string, object> packet)
        {
            var method = packet["m"].ToString();
            if (method != "PlayerState")
                return;


            if (IsClientDrone)
            {
                // Pose
                float poseLevel = float.Parse(packet["pose"].ToString());
                player.MovementContext.SetPoseLevel(poseLevel, true);
                // Prone
                bool prone = bool.Parse(packet["prn"].ToString());
                if(prone)
                    player.MovementContext.SetProneStateForce();
                // Sprint
                bool sprint = bool.Parse(packet["spr"].ToString());
                if (player.IsSprintEnabled)
                {
                    if (!sprint)
                    {
                        player.MovementContext.EnableSprint(false);
                    }
                }
                else
                {
                    if (sprint)
                    {
                        player.MovementContext.EnableSprint(true);
                    }
                }
                // Speed
                float speed = float.Parse(packet["spd"].ToString());
                player.CurrentState.ChangeSpeed(speed);
                //player.MovementContext.CharacterMovementSpeed = speed;
                // Rotation
                Vector2 packetRotation = new Vector2(
                float.Parse(packet["rX"].ToString())
                , float.Parse(packet["rY"].ToString())
                );
                //player.Rotation = packetRotation;
                ReplicatedRotation = packetRotation;
                // Position
                Vector3 packetPosition = new Vector3(
                    float.Parse(packet["pX"].ToString())
                    , float.Parse(packet["pY"].ToString())
                    , float.Parse(packet["pZ"].ToString())
                    );

                ReplicatedPosition = packetPosition;

                // Move / Direction
                if (packet.ContainsKey("dX"))
                {
                    Vector2 packetDirection = new Vector2(
                    float.Parse(packet["dX"].ToString())
                    , float.Parse(packet["dY"].ToString())
                    );
                    player.CurrentState.Move(packetDirection);
                    player.InputDirection = packetDirection;
                    ReplicatedDirection = packetDirection;
                }

                if (packet.ContainsKey("tilt"))
                {
                    var tilt = float.Parse(packet["tilt"].ToString());
                    player.MovementContext.SetTilt(tilt);
                }

                //if (packet.ContainsKey("tp"))
                //{
                //    //FirearmController_SetTriggerPressed_Patch.ReplicatePressed(player, bool.Parse(packet["tp"].ToString()));
                //}

                //if (packet.ContainsKey("spr"))
                //{
                //    bool sprintEnabledFromPacket = bool.Parse(packet.ContainsKey("spr").ToString());
                //    //if (player.MovementContext.IsSprintEnabled != sprintEnabledFromPacket)
                //    //{
                //        player.MovementContext.EnableSprint(sprintEnabledFromPacket);
                //    //}
                //}


                if (packet.ContainsKey("alive"))
                {
                    bool isCharAlive = bool.Parse(packet.ContainsKey("alive").ToString());
                    if (!isCharAlive)
                    {
                        player.ActiveHealthController.Kill(EFT.EDamageType.Undefined);
                        player.PlayerHealthController.Kill(EFT.EDamageType.Undefined);
                    }
                }

                return;
            }
            
        }

        void LateUpdate()
        {
            LateUpdate_ClientDrone();

            if(IsClientDrone)
                return;

            if (player.ActiveHealthController.IsAlive)
            {
                var bodyPartHealth = player.ActiveHealthController.GetBodyPartHealth(EBodyPart.Common);
                if (bodyPartHealth.AtMinimum)
                {
                    var packet = new Dictionary<string, object>();
                    packet.Add("dmt", EDamageType.Undefined.ToString());
                    packet.Add("m", "Kill");
                    ServerCommunication.PostLocalPlayerData(player, packet, true);
                }
            }
        }

        private void LateUpdate_ClientDrone()
        {
            if (!IsClientDrone)
                return;
                // Replicate Position.
                // If a short distance -> Smooth Lerp to the Desired Position
                // If the other side of a wall -> Teleport to the correct side (TODO)
                // If far away -> Teleport
                if (ReplicatedPosition.HasValue)
                {
                    var replicationDistance = Vector3.Distance(ReplicatedPosition.Value, player.Position);
                    var replicatedPositionDirection = ReplicatedPosition.Value - player.Position;
                    if (replicationDistance >= 2)
                    {
                        player.Teleport(ReplicatedPosition.Value, true);
                    }
                    else
                    {
                        player.Position = Vector3.Lerp(player.Position, ReplicatedPosition.Value, Time.deltaTime * 8);
                    }
                }

                // Replicate Rotation.
                // Smooth Lerp to the Desired Rotation
                if (ReplicatedRotation.HasValue)
                {
                    player.Rotation = Vector3.Lerp(player.Rotation, ReplicatedRotation.Value, Time.deltaTime * 8);
                }

                if (ReplicatedDirection.HasValue)
                {
                    player.CurrentState.Move(ReplicatedDirection.Value);
                    player.InputDirection = ReplicatedDirection.Value;
                }
        }

        private Vector2 LastDirection { get; set; } = Vector2.zero;
        private DateTime LastDirectionSent { get; set; } = DateTime.Now;
        private Vector2 LastRotation { get; set; } = Vector2.zero;
        private DateTime LastRotationSent { get; set; } = DateTime.Now;
        private Vector3 LastPosition { get; set; } = Vector3.zero;
        private DateTime LastPositionSent { get; set; } = DateTime.Now;
        public Vector2? ReplicatedDirection { get; internal set; }
        public Vector2? ReplicatedRotation { get; internal set; }
        public bool? ReplicatedRotationClamp { get; internal set; }
        public Vector3? ReplicatedPosition { get; internal set; }
        public DateTime LastPoseSent { get; private set; }
        public float LastPose { get; private set; }
        public DateTime LastSpeedSent { get; private set; }
        public float LastSpeed { get; private set; }
        public DateTime LastPlayerStateSent { get; private set; } = DateTime.Now;
        public bool TriggerPressed { get; internal set; }

        public Dictionary<string, object> PreMadeMoveDataPacket = new()
        {
            { "dX", "0" },
            { "dY", "0" },
            { "rX", "0" },
            { "rY", "0" },
            { "m", "Move" }
        };
        public Dictionary<string, object> PreMadeTiltDataPacket = new()
        {
            { "tilt", "0" },
            { "m", "Tilt" }
        };

        public bool IsAI()
        {
            return player.IsAI && !player.Profile.Id.StartsWith("pmc");
        }

        public bool IsOwnedPlayer()
        {
            return player.Profile.Id.StartsWith("pmc") && !IsClientDrone;
        }
    }
}
