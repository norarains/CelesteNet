using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.CelesteNet.Server.Chat {
    /// <summary>
    /// "/dummy" test bots. Each real player can spawn ONE static dummy of themselves — a
    /// fake networked player cloned from their current position + skin — which the server
    /// broadcasts to everyone (including players who join later) so it renders through the
    /// exact same ghost path as a real remote player. "/dummy" again removes it.
    ///
    /// Design: the dummy is a pure BROADCAST PHANTOM. It is deliberately NOT registered in
    /// Server.Data and has no CelesteNetPlayerSession, so it can never break the server's
    /// session/player iteration. Clients register + render it from the broadcast packets
    /// like any other player; removal broadcasts a dead DataPlayerInfo (empty FullName),
    /// which makes every client drop the ghost + player-list entry.
    /// </summary>
    public sealed class DummyContext : IDisposable {

        // Dummy IDs live in a high range so they never collide with the server's real,
        // sequentially assigned session IDs.
        public const uint IDBase = 0xDED00000;

        public readonly ChatModule Chat;
        private CelesteNetServer Server => Chat.Server;

        private readonly ConcurrentDictionary<uint, DummyPlayer> ByOwner = new();
        private long _NextID = IDBase;
        private readonly Timer KeepAlive;

        public DummyContext(ChatModule chat) {
            Chat = chat;
            Server.OnSessionStart += OnSessionStart;
            // A real idle player keeps sending frames; we re-broadcast each dummy's frame on
            // a slow cadence so it (a) survives the client's async state-bind create race and
            // (b) appears for players who walk into the dummy's room after it was spawned.
            KeepAlive = new Timer(_ => { try { Resend(); } catch { /* best effort */ } }, null, 1000, 1000);
        }

        public void Dispose() {
            Server.OnSessionStart -= OnSessionStart;
            KeepAlive.Dispose();
            foreach (uint owner in new List<uint>(ByOwner.Keys))
                Remove(owner);
        }

        public bool Has(uint ownerID) => ByOwner.ContainsKey(ownerID);

        private sealed class DummyPlayer {
            public uint ID;
            public DataPlayerInfo Info = null!;
            public DataPlayerGraphics Graphics = null!;
            public DataPlayerState State = null!;
            public DataPlayerFrame Frame = null!;
        }

        /// <summary>Spawn the caller's dummy from a freshly captured frame. Returns a chat reply.</summary>
        public string Spawn(CelesteNetPlayerSession owner, DataPlayerFrame snapshot) {
            if (ByOwner.ContainsKey(owner.SessionID))
                return "You already have a dummy. /dummy to remove it.";

            DataPlayerInfo? ownerInfo = owner.PlayerInfo;
            if (ownerInfo == null)
                return "Couldn't read your player info.";
            if (!Server.Data.TryGetBoundRef(ownerInfo, out DataPlayerGraphics? ownerGfx) || ownerGfx == null)
                return "Couldn't read your character yet — move around and try again.";
            if (!Server.Data.TryGetBoundRef(ownerInfo, out DataPlayerState? ownerState) || ownerState == null || ownerState.SID.IsNullOrEmpty())
                return "Enter a level first, then /dummy.";

            uint id = (uint) Interlocked.Increment(ref _NextID);

            DataPlayerInfo info = new() {
                ID = id,
                Name = ownerInfo.Name,
                FullName = $"{ownerInfo.Name}-dummy",
                NameColor = ownerInfo.NameColor,
                Prefix = ownerInfo.Prefix,
            };
            info.UpdateDisplayName(false);

            // Clone the owner's skin (graphics) and room (state), retargeted to the dummy.
            DataPlayerGraphics graphics = new() {
                Player = info,
                Depth = ownerGfx.Depth,
                SpriteMode = ownerGfx.SpriteMode,
                SpriteRate = ownerGfx.SpriteRate,
                SpriteAnimations = ownerGfx.SpriteAnimations,
                HairCount = ownerGfx.HairCount,
                HairStepPerSegment = ownerGfx.HairStepPerSegment,
                HairStepInFacingPerSegment = ownerGfx.HairStepInFacingPerSegment,
                HairStepApproach = ownerGfx.HairStepApproach,
                HairStepYSinePerSegment = ownerGfx.HairStepYSinePerSegment,
                HairScales = ownerGfx.HairScales,
                HairTextures = ownerGfx.HairTextures,
            };

            DataPlayerState state = new() {
                Player = info,
                SID = ownerState.SID,
                Mode = ownerState.Mode,
                Level = ownerState.Level,
                Idle = false,
                Interactive = ownerState.Interactive,
            };

            // Static snapshot: capture the pose but stand still (Speed 0, no followers/holding).
            DataPlayerFrame frame = new() {
                Player = info,
                Position = snapshot.Position,
                Scale = snapshot.Scale,
                Color = snapshot.Color,
                Facing = snapshot.Facing,
                Speed = Vector2.Zero,
                CurrentAnimationID = snapshot.CurrentAnimationID,
                CurrentAnimationFrame = snapshot.CurrentAnimationFrame,
                HairColors = snapshot.HairColors,
                HairTexture0 = snapshot.HairTexture0,
                HairSimulateMotion = false,
                Followers = Array.Empty<DataPlayerFrame.Entity>(),
                Holding = null,
                DashWasB = null,
                DashDir = null,
                Dead = false,
            };

            DummyPlayer dummy = new() { ID = id, Info = info, Graphics = graphics, State = state, Frame = frame };
            PrepareMeta(dummy);
            ByOwner[owner.SessionID] = dummy;

            // Order matters: the ref (Info) must reach clients before the bound refs.
            Server.Broadcast(info);
            Server.Broadcast(graphics);
            Server.Broadcast(state);
            Server.Broadcast(frame);

            Logger.Log(LogLevel.INF, "dummy", $"{ownerInfo.FullName} (#{owner.SessionID}) spawned dummy #{id}");
            return $"Spawned {info.FullName}. /dummy again to remove it.";
        }

        /// <summary>Remove a given owner's dummy (toggle off, or owner disconnected).</summary>
        public void Remove(uint ownerID) {
            if (!ByOwner.TryRemove(ownerID, out DummyPlayer? dummy))
                return;

            // Empty FullName => dead MetaRef => every client frees the ref, drops the ghost
            // and the player-list entry. No server-side data to clean up (never registered).
            DataPlayerInfo dead = new() { ID = dummy.ID, Name = dummy.Info.Name, FullName = "" };
            dead.Meta = dead.GenerateMeta(Server.Data);
            Server.Broadcast(dead);
            Logger.Log(LogLevel.INF, "dummy", $"Removed dummy #{dummy.ID} (owner #{ownerID})");
        }

        private void OnSessionStart(CelesteNetPlayerSession session) {
            // A late joiner needs the full set for every active dummy.
            foreach (DummyPlayer dummy in ByOwner.Values)
                SendTo(session, dummy);
            // When this player leaves, their dummy (if any) goes with them.
            session.OnEnd += OnSessionEnd;
        }

        private void OnSessionEnd(CelesteNetPlayerSession session, DataPlayerInfo? lastInfo) {
            session.OnEnd -= OnSessionEnd;
            Remove(session.SessionID);
        }

        private void Resend() {
            foreach (DummyPlayer dummy in ByOwner.Values)
                Server.Broadcast(dummy.Frame);
        }

        private void SendTo(CelesteNetPlayerSession session, DummyPlayer dummy) {
            try {
                session.Con.Send(dummy.Info);
                session.Con.Send(dummy.Graphics);
                session.Con.Send(dummy.State);
                session.Con.Send(dummy.Frame);
            } catch (Exception e) {
                Logger.Log(LogLevel.DEV, "dummy", $"Failed sending dummy #{dummy.ID} to #{session.SessionID}: {e.Message}");
            }
        }

        private void PrepareMeta(DummyPlayer dummy) {
            dummy.Info.Meta = dummy.Info.GenerateMeta(Server.Data);
            dummy.Graphics.Meta = dummy.Graphics.GenerateMeta(Server.Data);
            dummy.State.Meta = dummy.State.GenerateMeta(Server.Data);
            dummy.Frame.Meta = dummy.Frame.GenerateMeta(Server.Data);
        }
    }
}
