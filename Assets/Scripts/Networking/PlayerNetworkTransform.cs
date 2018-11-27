using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class PlayerState
{
    public Vector3 Origin;
};

public class Frame {

    public Frame(float deltaTime) 
    {
        DeltaTime = deltaTime;
        DeltaPosition = Vector3.zero;
    }

    public Vector3 DeltaPosition;
    public float DeltaTime;
}

public class LagRecord 
{
    public List<Frame> FrameHistory;
    public float HistoryDuration;

    public LagRecord()
    {
        FrameHistory = new List<Frame>();
    } 
}

public class ServerStateUpdate : MessageBase
{
    public static readonly short MessageID = 889;
    public uint netId;
    
    public Vector3 Origin;

    public override void Deserialize(NetworkReader reader)
    {
        netId = reader.ReadPackedUInt32();
        Origin = reader.ReadVector3();
    }

    public override void Serialize(NetworkWriter writer)
    {
        writer.WritePackedUInt32(netId);
        writer.Write(Origin);
    }
}

[AddComponentMenu("Network/Player Network Transform")]
public class PlayerNetworkTransform : NetworkBehaviour
{
    public PlayerState LastPredictedState;
    public PlayerState NewState;
    public PlayerState ServerState;

    private Player m_TargetPlayer;
    private PlayerInputSynchronization m_PlayerInput;

    private LagRecord m_LagRecord;

    public Transform TargetTransform;

    public void Start()
    {
        m_LagRecord = new LagRecord();
    }

    public void Awake()
    {
        m_TargetPlayer = GetComponent<Player>();
        m_PlayerInput = GetComponent<PlayerInputSynchronization>();
        //Initialize predicted state
        LastPredictedState = new PlayerState();
        LastPredictedState.Origin = transform.position;
        //Intialize new state
        NewState = new PlayerState();
        NewState.Origin = transform.position;
        //Initialize server state
        ServerState = new PlayerState();
        ServerState.Origin = transform.position;

    }

    void FixedUpdate()
    {
        if(isServer)
        {
            FixedUpdateServer();
        }
        if(isClient)
        {
            FixedUpdateClient();
        }
    }

    private void FixedUpdateClient()
    {
        UserCmd nextCmd = null;

        while(m_PlayerInput.NextUserCommand(out nextCmd))
        {
            //Temporary state for client prediction
            NewState = m_TargetPlayer.ProcessUserCmd(nextCmd, LastPredictedState);

            //Client frame for this duration
            var frame = new Frame(Time.fixedDeltaTime);
            frame.DeltaPosition = NewState.Origin - LastPredictedState.Origin; //Displacement

            m_LagRecord.FrameHistory.Add(frame);
            m_LagRecord.HistoryDuration += Time.fixedDeltaTime; //Duration of frame

            LastPredictedState = NewState;
        }

        float latency = NetworkHandler.Instance.RoundTripTime;
        float lerpFraction = Time.fixedDeltaTime / (latency * (1 + 0.05f));
        //Extrapolate position here
        TargetTransform.position = Vector3.Lerp(
            LastPredictedState.Origin, 
            TargetTransform.position, 
            lerpFraction
        );
    }

    private void FixedUpdateServer()
    {
        PlayerState finalState = ServerState;
        UserCmd nextCmd = null;

        while(m_PlayerInput.NextUserCommand(out nextCmd))
        {
            finalState = m_TargetPlayer.ProcessUserCmd(nextCmd, finalState);
        }

        ServerStateUpdate update = new ServerStateUpdate();
        update.netId = m_TargetPlayer.netId.Value;
        update.Origin = finalState.Origin;

        connectionToClient.SendByChannel(
            ServerStateUpdate.MessageID,
            update,
            Channels.DefaultReliable
        );

    }

    public void OnServerFrame(PlayerState serverUpdate)
    {
        float latency = NetworkHandler.Instance.RoundTripTime;
        float simulationTime = Mathf.Max(0, m_LagRecord.HistoryDuration - latency);
        m_LagRecord.HistoryDuration = m_LagRecord.HistoryDuration - simulationTime;
    
        while(m_LagRecord.FrameHistory.Count > 0 && simulationTime > 0)
        {
            if(simulationTime >= m_LagRecord.FrameHistory[0].DeltaTime)
            {
                simulationTime -= m_LagRecord.FrameHistory[0].DeltaTime;
                m_LagRecord.FrameHistory.RemoveAt(0);
            }
            else
            {
                int x = 0;
                //Move back linear fraction
                var fraction = 1 - simulationTime / m_LagRecord.FrameHistory[0].DeltaTime;
                m_LagRecord.FrameHistory[0].DeltaTime = m_LagRecord.FrameHistory[0].DeltaTime - simulationTime;
                m_LagRecord.FrameHistory[0].DeltaPosition = m_LagRecord.FrameHistory[0].DeltaPosition * fraction;
                break;
            }
        }
        ServerState = serverUpdate;

        //If we're teleporting, go ahead and replay inputs from server position

        LastPredictedState.Origin = serverUpdate.Origin;
        //Add any unaccounted deltas
        foreach(var frame in m_LagRecord.FrameHistory)
        {
            LastPredictedState.Origin += frame.DeltaPosition;
        }

    }
}