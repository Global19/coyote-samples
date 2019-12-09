﻿// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Coyote;
using Microsoft.Coyote.Actors;

namespace Coyote.Examples.FailureDetector
{
    /// <summary>
    /// Implementation of a failure detector Coyote machine.
    /// </summary>
    internal class FailureDetector : StateMachine
    {
        internal class Config : Event
        {
            public HashSet<ActorId> Nodes;

            public Config(HashSet<ActorId> nodes)
            {
                this.Nodes = nodes;
            }
        }

        internal class NodeFailed : Event
        {
            public ActorId Node;

            public NodeFailed(ActorId node)
            {
                this.Node = node;
            }
        }

        private class TimerCancelled : Event { }

        private class RoundDone : Event { }

        private class Unit : Event { }

        /// <summary>
        /// Nodes to be monitored.
        /// </summary>
        private HashSet<ActorId> Nodes;

        /// <summary>
        /// Set of registered clients.
        /// </summary>
        private HashSet<ActorId> Clients;

        /// <summary>
        /// Number of made 'Ping' attempts.
        /// </summary>
        private int Attempts;

        /// <summary>
        /// Set of alive nodes.
        /// </summary>
        private HashSet<ActorId> Alive;

        /// <summary>
        /// Collected responses in one round.
        /// </summary>
        private HashSet<ActorId> Responses;

        /// <summary>
        /// Reference to the timer machine.
        /// </summary>
        private ActorId Timer;

        [Start]
        [OnEntry(nameof(InitOnEntry))]
        [OnEventDoAction(typeof(Driver.RegisterClient), nameof(RegisterClientAction))]
        [OnEventDoAction(typeof(Driver.UnregisterClient), nameof(UnregisterClientAction))]
        [OnEventPushState(typeof(Unit), typeof(SendPing))]
        private class Init : State { }

        private Transition InitOnEntry(Event e)
        {
            var nodes = (e as Config).Nodes;

            this.Nodes = new HashSet<ActorId>(nodes);
            this.Clients = new HashSet<ActorId>();
            this.Alive = new HashSet<ActorId>();
            this.Responses = new HashSet<ActorId>();

            // Initializes the alive set to contain all available nodes.
            foreach (var node in this.Nodes)
            {
                this.Alive.Add(node);
            }

            // Initializes the timer.
            this.Timer = this.CreateActor(typeof(Timer), new Timer.Config(this.Id));

            // Transitions to the 'SendPing' state after everything has initialized.
            return this.RaiseEvent(new Unit());
        }

        private void RegisterClientAction(Event e)
        {
            var client = (e as Driver.RegisterClient).Client;
            this.Clients.Add(client);
        }

        private void UnregisterClientAction(Event e)
        {
            var client = (e as Driver.UnregisterClient).Client;
            if (this.Clients.Contains(client))
            {
                this.Clients.Remove(client);
            }
        }

        [OnEntry(nameof(SendPingOnEntry))]
        [OnEventGotoState(typeof(RoundDone), typeof(Reset))]
        [OnEventPushState(typeof(TimerCancelled), typeof(WaitForCancelResponse))]
        [OnEventDoAction(typeof(Node.Pong), nameof(PongAction))]
        [OnEventDoAction(typeof(Timer.TimeoutEvent), nameof(TimeoutAction))]
        private class SendPing : State { }

        private void SendPingOnEntry()
        {
            foreach (var node in this.Nodes)
            {
                // Sends a 'Ping' event to any machine that has not responded.
                if (this.Alive.Contains(node) && !this.Responses.Contains(node))
                {
                    this.Monitor<Safety>(new Safety.Ping(node));
                    this.SendEvent(node, new Node.Ping(this.Id));
                }
            }

            // Starts the timer with a given timeout value. Note that in this sample,
            // the timeout value is not actually used, because the timer is abstracted
            // away using Coyote to enable systematic testing (i.e. timeouts are triggered
            // nondeterministically). In production, this model timer machine will be
            // replaced by a real timer.
            this.SendEvent(this.Timer, new Timer.StartTimerEvent(100));
        }

        /// <summary>
        /// This action is triggered whenever a node replies with a 'Pong' event.
        /// </summary>
        private Transition PongAction(Event e)
        {
            var node = (e as Node.Pong).Node;
            if (this.Alive.Contains(node))
            {
                this.Responses.Add(node);

                // Checks if the status of alive nodes has changed.
                if (this.Responses.Count == this.Alive.Count)
                {
                    this.SendEvent(this.Timer, new Timer.CancelTimerEvent());
                    return this.RaiseEvent(new TimerCancelled());
                }
            }

            return default;
        }

        private Transition TimeoutAction()
        {
            // One attempt is done for this round.
            this.Attempts++;

            // Each round has a maximum number of 2 attempts.
            if (this.Responses.Count < this.Alive.Count && this.Attempts < 2)
            {
                // Retry by looping back to same state.
                return this.GotoState<SendPing>();
            }

            foreach (var node in this.Nodes)
            {
                if (this.Alive.Contains(node) && !this.Responses.Contains(node))
                {
                    this.Alive.Remove(node);

                    // Send failure notification to any clients.
                    foreach (var client in this.Clients)
                    {
                        this.SendEvent(client, new NodeFailed(node));
                    }
                }
            }

            return this.RaiseEvent(new RoundDone());
        }

        [OnEventDoAction(typeof(Timer.CancelSuccess), nameof(CancelSuccessAction))]
        [OnEventDoAction(typeof(Timer.CancelFailure), nameof(CancelFailure))]
        [DeferEvents(typeof(Timer.TimeoutEvent), typeof(Node.Pong))]
        private class WaitForCancelResponse : State { }

        private Transition CancelSuccessAction()
        {
            return this.RaiseEvent(new RoundDone());
        }

        private Transition CancelFailure()
        {
            return this.PopState();
        }

        [OnEntry(nameof(ResetOnEntry))]
        [OnEventGotoState(typeof(Timer.TimeoutEvent), typeof(SendPing))]
        [IgnoreEvents(typeof(Node.Pong))]
        private class Reset : State { }

        /// <summary>
        /// Prepares the failure detector for the next round.
        /// </summary>
        private void ResetOnEntry()
        {
            this.Attempts = 0;
            this.Responses.Clear();

            // Starts the timer with a given timeout value (see details above).
            this.SendEvent(this.Timer, new Timer.StartTimerEvent(1000));
        }
    }
}