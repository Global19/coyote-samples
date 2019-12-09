﻿// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Coyote;
using Microsoft.Coyote.Actors;
using Microsoft.Coyote.Specifications;

namespace Coyote.Examples.ChainReplication
{
    internal class InvariantMonitor : Monitor
    {
        internal class Config : Event
        {
            public List<ActorId> Servers;

            public Config(List<ActorId> servers)
                : base()
            {
                this.Servers = servers;
            }
        }

        internal class UpdateServers : Event
        {
            public List<ActorId> Servers;

            public UpdateServers(List<ActorId> servers)
                : base()
            {
                this.Servers = servers;
            }
        }

        internal class HistoryUpdate : Event
        {
            public ActorId Server;
            public List<int> History;

            public HistoryUpdate(ActorId server, List<int> history)
                : base()
            {
                this.Server = server;
                this.History = history;
            }
        }

        internal class SentUpdate : Event
        {
            public ActorId Server;
            public List<SentLog> SentHistory;

            public SentUpdate(ActorId server, List<SentLog> sentHistory)
                : base()
            {
                this.Server = server;
                this.SentHistory = sentHistory;
            }
        }

        private class Local : Event { }

        private List<ActorId> Servers;

        private Dictionary<ActorId, List<int>> History;
        private Dictionary<ActorId, List<int>> SentHistory;
        private List<int> TempSeq;

        private ActorId Next;
        private ActorId Prev;

        [Start]
        [OnEventGotoState(typeof(Local), typeof(WaitForUpdateMessage))]
        [OnEventDoAction(typeof(Config), nameof(Configure))]
        private class Init : State { }

        private Transition Configure(Event e)
        {
            this.Servers = (e as Config).Servers;
            this.History = new Dictionary<ActorId, List<int>>();
            this.SentHistory = new Dictionary<ActorId, List<int>>();
            this.TempSeq = new List<int>();
            return this.RaiseEvent(new Local());
        }

        [OnEventDoAction(typeof(HistoryUpdate), nameof(CheckUpdatePropagationInvariant))]
        [OnEventDoAction(typeof(SentUpdate), nameof(CheckInprocessRequestsInvariant))]
        [OnEventDoAction(typeof(UpdateServers), nameof(ProcessUpdateServers))]
        private class WaitForUpdateMessage : State { }

        private void CheckUpdatePropagationInvariant(Event e)
        {
            var server = (e as HistoryUpdate).Server;
            var history = (e as HistoryUpdate).History;

            this.IsSorted(history);

            if (this.History.ContainsKey(server))
            {
                this.History[server] = history;
            }
            else
            {
                this.History.Add(server, history);
            }

            // HIST(i+1) <= HIST(i)
            this.GetNext(server);
            if (this.Next != null && this.History.ContainsKey(this.Next))
            {
                this.CheckLessOrEqualThan(this.History[this.Next], this.History[server]);
            }

            // HIST(i) <= HIST(i-1)
            this.GetPrev(server);
            if (this.Prev != null && this.History.ContainsKey(this.Prev))
            {
                this.CheckLessOrEqualThan(this.History[server], this.History[this.Prev]);
            }
        }

        private void CheckInprocessRequestsInvariant(Event e)
        {
            this.ClearTempSeq();

            var server = (e as SentUpdate).Server;
            var sentHistory = (e as SentUpdate).SentHistory;

            this.ExtractSeqId(sentHistory);

            if (this.SentHistory.ContainsKey(server))
            {
                this.SentHistory[server] = this.TempSeq;
            }
            else
            {
                this.SentHistory.Add(server, this.TempSeq);
            }

            this.ClearTempSeq();

            // HIST(i) == HIST(i+1) + SENT(i)
            this.GetNext(server);
            if (this.Next != null && this.History.ContainsKey(this.Next))
            {
                this.MergeSeq(this.History[this.Next], this.SentHistory[server]);
                this.CheckEqual(this.History[server], this.TempSeq);
            }

            this.ClearTempSeq();

            // HIST(i-1) == HIST(i) + SENT(i-1)
            this.GetPrev(server);
            if (this.Prev != null && this.History.ContainsKey(this.Prev))
            {
                this.MergeSeq(this.History[server], this.SentHistory[this.Prev]);
                this.CheckEqual(this.History[this.Prev], this.TempSeq);
            }

            this.ClearTempSeq();
        }

        private void GetNext(ActorId curr)
        {
            this.Next = null;

            for (int i = 1; i < this.Servers.Count; i++)
            {
                if (this.Servers[i - 1].Equals(curr))
                {
                    this.Next = this.Servers[i];
                }
            }
        }

        private void GetPrev(ActorId curr)
        {
            this.Prev = null;

            for (int i = 1; i < this.Servers.Count; i++)
            {
                if (this.Servers[i].Equals(curr))
                {
                    this.Prev = this.Servers[i - 1];
                }
            }
        }

        private void ExtractSeqId(List<SentLog> seq)
        {
            this.ClearTempSeq();

            for (int i = seq.Count - 1; i >= 0; i--)
            {
                if (this.TempSeq.Count > 0)
                {
                    this.TempSeq.Insert(0, seq[i].NextSeqId);
                }
                else
                {
                    this.TempSeq.Add(seq[i].NextSeqId);
                }
            }

            this.IsSorted(this.TempSeq);
        }

        private void MergeSeq(List<int> seq1, List<int> seq2)
        {
            this.ClearTempSeq();
            this.IsSorted(seq1);

            if (seq1.Count == 0)
            {
                this.TempSeq = seq2;
            }
            else if (seq2.Count == 0)
            {
                this.TempSeq = seq1;
            }
            else
            {
                for (int i = 0; i < seq1.Count; i++)
                {
                    if (seq1[i] < seq2[0])
                    {
                        this.TempSeq.Add(seq1[i]);
                    }
                }

                for (int i = 0; i < seq2.Count; i++)
                {
                    this.TempSeq.Add(seq2[i]);
                }
            }

            this.IsSorted(this.TempSeq);
        }

        private void IsSorted(List<int> seq)
        {
            for (int i = 0; i < seq.Count - 1; i++)
            {
                this.Assert(seq[i] < seq[i + 1], "Sequence is not sorted.");
            }
        }

        private void CheckLessOrEqualThan(List<int> seq1, List<int> seq2)
        {
            this.IsSorted(seq1);
            this.IsSorted(seq2);

            for (int i = 0; i < seq1.Count; i++)
            {
                if ((i == seq1.Count) || (i == seq2.Count))
                {
                    break;
                }

                this.Assert(seq1[i] <= seq2[i], "{0} not less or equal than {1}.", seq1[i], seq2[i]);
            }
        }

        private void CheckEqual(List<int> seq1, List<int> seq2)
        {
            this.IsSorted(seq1);
            this.IsSorted(seq2);

            for (int i = 0; i < seq1.Count; i++)
            {
                if ((i == seq1.Count) || (i == seq2.Count))
                {
                    break;
                }

                this.Assert(seq1[i] == seq2[i], "{0} not equal with {1}.", seq1[i], seq2[i]);
            }
        }

        private void ClearTempSeq()
        {
            this.Assert(this.TempSeq.Count <= 6, "Temp sequence has more than 6 elements.");
            this.TempSeq.Clear();
            this.Assert(this.TempSeq.Count == 0, "Temp sequence is not cleared.");
        }

        private void ProcessUpdateServers(Event e)
        {
            this.Servers = (e as UpdateServers).Servers;
        }
    }
}