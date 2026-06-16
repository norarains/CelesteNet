using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Celeste.Mod.CelesteNet.Client.Components;
using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.CelesteNet.Client.Entities {
    [Tracked]
    public class Ghost : Actor, ITickReceiver {

        public CelesteNetClientContext? Context;

        public DataPlayerInfo PlayerInfo;
        public volatile DataPlayerGraphics? PlayerGraphics;

        public float Alpha = 0.875f;

        public Vector2 Speed;

        public PlayerSprite Sprite;
        public PlayerHair Hair;
        public Leader Leader;
        public Holdable Holdable;
        private bool HoldableAdded;

        public GhostNameTag? NameTag;
        public GhostEmote? IdleTag;

        public Color[] HairColors = new[] { Color.White };

        public bool? DashWasB;
        public Vector2? DashDir;

        public bool Dead;

        public List<GhostFollower> Followers = new();
        public GhostEntity? Holding;

        public bool Interactive;

        public float GrabCooldown = 0f;
        public const float GrabCooldownMax = CelesteNetMainComponent.GrabCooldownMax;
        // Head-stomp contact edge-detection, so the boop/dust/shake feedback fires ONCE per
        // contact (on enter), not every overlapping frame. PlayerCollider only reports "currently
        // overlapping" (no OnExit), so OnPlayer sets `stompContactThisFrame` while it overlaps and
        // Update() rolls it into `wasStompContact` + clears it — a frame with no OnPlayer call
        // means the player left the head (exit).
        private bool stompContactThisFrame = false;
        private bool wasStompContact = false;
        public byte GrabStrength;
        protected DataPlayerGrabPlayer? GrabPacket;

        // TODO Revert this to Queue<> once MonoKickstart weirdness is fixed
        protected List<Action<Ghost>> UpdateQueue = new();
        protected bool IsUpdating;

        public const int MaxHairLength = 30;

        public Ghost(CelesteNetClientContext? context, DataPlayerInfo playerInfo, PlayerSpriteMode spriteMode)
            : base(Vector2.Zero) {
            Context = context;
            PlayerInfo = playerInfo;

            Depth = 0;

            RetryPlayerSprite:
            try {
                Sprite = new(spriteMode | (PlayerSpriteMode) (1 << 31));
            } catch {
                if (spriteMode != PlayerSpriteMode.Madeline) {
                    spriteMode = PlayerSpriteMode.Madeline;
                    goto RetryPlayerSprite;
                }
                throw;
            }

            Add(Hair = new(Sprite));
            Add(Sprite);
            Hair.Color = Player.NormalHairColor;
            Add(Leader = new(new(0f, -8f)));
            Holdable = new() {
                OnPickup = OnPickup,
                OnCarry = OnCarry,
                OnRelease = OnRelease
            };

            Collidable = true;
            Collider = new Hitbox(8f, 11f, -4f, -11f);
            // The OnPlayer (bounce/super) check gets its OWN slightly taller hitbox — 5px
            // higher on top than the entity collider — so a stomp/super off a head has a bit
            // of extra fault tolerance above. PlayerCollider.Check swaps this in only for the
            // check, so grab (Holdable) + every other ghost collision still use the entity
            // Collider above, unchanged. (Puffer does the same: its PlayerCollider hitbox is
            // bigger than its entity collider.) `Top` in OnPlayer stays the entity top, so the
            // bounce condition's lower bound is unchanged — this only widens the window upward.
            Add(new PlayerCollider(OnPlayer, new Hitbox(8f, 16f, -4f, -16f)));

            NameTag = new(this, "");
            NameTag.Alpha = 0.85f;

            OpacityAdjustAlpha();

            Dead = false;
            AllowPushing = false;
            SquishCallback = null;

            Tag = Tags.Persistent | Tags.PauseUpdate | Tags.TransitionUpdate;
        }

        public override void Added(Scene scene) {
            base.Added(scene);

            Hair.Start();
            if (NameTag != null)
                Scene.Add(NameTag);
        }

        public override void Removed(Scene scene) {
            base.Removed(scene);

            NameTag?.RemoveSelf();
        }

        public void OnPickup() {
            GrabStrength = (byte) Calc.Random.Next(0, DataPlayerGrabPlayer.MaxGrabStrength + 1);
        }

        public void OnPlayer(Player player) {
            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.InGame.Interactions || Context?.Main?.GrabbedBy == this)
                return;

            // Contact from above that isn't moving UP — including mid-dash and a flat dash.
            if (player.Speed.Y >= 0f && player.Bottom <= Top + 3f) {

                bool justLanded = !wasStompContact;   // rising edge: first frame of this contact
                stompContactThisFrame = true;

                if (player.DashAttacking) {
                    // Treat the head as flat GROUND for the dash (no bounce): give the dash back
                    // and keep granting on-ground status (a coyote window) so the player can
                    // super / hyper / jump / re-dash off it with normal inputs — no pre-buffered
                    // jump needed. Celeste's super then fires from its own DashUpdate (it gates on
                    // |DashDir.Y| < 0.1 + jumpGraceTimer > 0), with exact vanilla speeds, and
                    // super vs hyper vs reverse all resolve from the player's duck/Facing/input.
                    player.RefillDash();
                    CelesteNetClientUtils.GrantJumpGrace(player);

                    // 斜下冲 fix: a down-diagonal dash never supers/hypers in the air, because the
                    // super gate above only accepts near-horizontal dashes. On flat ground Celeste
                    // converts a down-dash into a horizontal crouch-dash at dash start (Player
                    // DashCoroutine, when onGround). We have no ground, so replicate that conversion
                    // here on contact — the (now horizontal, ducking) dash + jump produces a hyper,
                    // exactly like off a floor.
                    if (player.DashDir.X != 0f && player.DashDir.Y > 0f && player.Speed.Y > 0f) {
                        player.DashDir.X = Math.Sign(player.DashDir.X);
                        player.DashDir.Y = 0f;
                        player.Speed.Y = 0f;
                        player.Speed.X *= 1.2f;
                        player.Ducking = true;
                    }
                } else {
                    // Not dashing: the original Puffer-style bounce.
                    player.Bounce(Top + 2f);
                }

                // Feedback once on contact-enter (not every overlapping frame).
                if (justLanded) {
                    Dust.Burst(player.BottomCenter, -1.57079637f, 8);
                    (Scene as Level)?.DirectionalShake(Vector2.UnitY, 0.05f);
                    Input.Rumble(RumbleStrength.Light, RumbleLength.Medium);
                    player.Play("event:/game/general/thing_booped");
                }

            } else if (player.StateMachine.State != Player.StDash &&
                player.StateMachine.State != Player.StRedDash &&
                player.StateMachine.State != Player.StDreamDash &&
                player.StateMachine.State != Player.StBirdDashTutorial &&
                player.Speed.Y <= 0f && Bottom <= player.Top + 5f) {
                player.Speed.Y = Math.Max(player.Speed.Y, 16f);
            }
        }

        public void OnCarry(Vector2 position) {
            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.InGame.Interactions || IdleTag != null)
                return;

            Position = position;
            Collidable = false;

            CelesteNetClient? client = Context?.Client;
            if (PlayerInfo == null || client == null)
                return;

            GrabPacket = new DataPlayerGrabPlayer {
                Player = client.PlayerInfo,
                Grabbing = PlayerInfo,
                Position = position,
                Force = null,
                GrabStrength = GrabStrength
            };
        }

        public void OnRelease(Vector2 force) {
            Collidable = true;

            if (!Interactive || GrabCooldown > 0f || !CelesteNetClientModule.Settings.InGame.Interactions || IdleTag != null)
                return;

            CelesteNetClient? client = Context?.Client;
            if (PlayerInfo == null || client == null)
                return;

            GrabPacket = new DataPlayerGrabPlayer {
                Player = client.PlayerInfo,
                Grabbing = PlayerInfo,
                Position = Position,
                Force = force,
                GrabStrength = 0
            };
        }

        public override void Update() {
            lock (UpdateQueue) {
                IsUpdating = true;
                for (int i = 0; i < UpdateQueue.Count; i++)
                    UpdateQueue[i](this);
                UpdateQueue.Clear();
                IsUpdating = false;
            }

            if (string.IsNullOrEmpty(NameTag?.Name) && Active) {
                RemoveSelf();
                return;
            }

            bool holdable = Interactive && GrabCooldown <= 0f && CelesteNetClientModule.Settings.InGame.Interactions && IdleTag == null;

            GrabCooldown -= Engine.RawDeltaTime;
            if (GrabCooldown < 0f)
                GrabCooldown = 0f;
            // Roll the per-frame stomp-contact flag: a frame where OnPlayer didn't run means the
            // player is no longer on the head (collision exit), so the next contact re-fires the FX.
            wasStompContact = stompContactThisFrame;
            stompContactThisFrame = false;

            if (!holdable && Holdable.Holder != null) {
                Collidable = false;
                Holdable.Release(Vector2.Zero);
                Collidable = true;
            }

            if (!HoldableAdded && holdable) {
                HoldableAdded = true;
                Add(Holdable);
            } else if (HoldableAdded && !holdable && !Holdable.IsHeld) {
                HoldableAdded = false;
                Remove(Holdable);
            }
            OpacityAdjustAlpha();

            if (NameTag != null && NameTag.Scene != Scene)
                Scene.Add(NameTag);

            Visible = !Dead && CelesteNetClientModule.Settings.InGame.OtherPlayerOpacity != 0;

            base.Update();

            if (Scene is not Level level)
                return;

            if (!(level.GetUpdateHair() ?? true) || level.Overlay is PauseUpdateOverlay)
                Hair.AfterUpdate();

            foreach (GhostFollower gf in Followers)
                if (gf.Scene != level)
                    level.Add(gf);

            if (Holding != null && Holding.Scene != level)
                level.Add(Holding);

            // TODO: Get rid of this, sync particles separately!
            if (CelesteNetClientModule.Settings.InGame.OtherPlayerOpacity != 0 && DashWasB != null && DashDir != null && level != null && Speed != Vector2.Zero && level.OnRawInterval(0.02f))
                level.ParticlesFG.Emit(DashWasB.Value ? Player.P_DashB : Player.P_DashA, Center + Calc.Random.Range(Vector2.One * -2f, Vector2.One * 2f), DashDir.Value.Angle());
        }

        private void OpacityAdjustAlpha() {
            if (CelesteNetClientModule.Settings.InGame.OtherPlayerOpacity == 0) {
                Alpha = 0f;
            } else {
                Alpha = CelesteNetClientModule.Settings.OtherPlayerAlpha;
            }
            if (Hair != null)
                Hair.Alpha = Alpha;
        }

        public void Tick() {
            if (GrabPacket != null) {
                CelesteNetClient? client = Context?.Client;
                if (client != null) {
                    try {
                        client.Send(GrabPacket);
                    } catch (Exception e) {
                        Logger.Log(LogLevel.INF, "client-ghost", $"Error sending grab packet: {e}");
                        Context?.DisposeSafe();
                    }
                }
            }
            GrabPacket = null;
        }

        public void RunOnUpdate(Action<Ghost> action, bool wait = false) {
            bool updating;
            lock (UpdateQueue)
                updating = IsUpdating;
            if (updating) {
                action(this);
                return;
            }

            using ManualResetEvent? waiter = wait ? new ManualResetEvent(false) : null;
            if (wait) {
                Action<Ghost> real = action;
                action = g => {
                    try {
                        real(g);
                    } finally {
                        waiter?.Set();
                    }
                };
            }

            lock (UpdateQueue)
                UpdateQueue.Add(action);

            if (wait)
                WaitHandle.WaitAny(new WaitHandle[] { waiter! });
        }

        public void UpdateGraphics(DataPlayerGraphics graphics) {
            if (graphics.HairCount == 0) {
                graphics.HairCount = 1;
                graphics.HairScales = new[] { Vector2.One };
                graphics.HairTextures = new[] { "characters/player/hair00" };
            } else if (graphics.HairCount > MaxHairLength) {
                graphics.HairCount = MaxHairLength;
            }

            PlayerGraphics = graphics;

            Depth = graphics.Depth + 1;
            Sprite.Rate = graphics.SpriteRate;

            Sprite.HairCount = graphics.HairCount;
            Hair.StepPerSegment = graphics.HairStepPerSegment;
            Hair.StepInFacingPerSegment = graphics.HairStepInFacingPerSegment;
            Hair.StepApproach = graphics.HairStepApproach;
            Hair.StepYSinePerSegment = graphics.HairStepYSinePerSegment;
            if (graphics.HairCount > Hair.Nodes.Count)
                Hair.Nodes.Capacity = graphics.HairCount;
            while (Hair.Nodes.Count < graphics.HairCount)
                Hair.Nodes.Add(Hair.Nodes.LastOrDefault());
            while (Hair.Nodes.Count > graphics.HairCount)
                Hair.Nodes.RemoveAt(Hair.Nodes.Count - 1);
            Hair.Alpha = Alpha;
        }

        public void UpdateAnimation(int animationID, int animationFrame) {
            if (PlayerGraphics == null)
                return;

            if (animationID < 0 || PlayerGraphics.SpriteAnimations.Length <= animationID)
                return;

            string strID = PlayerGraphics.SpriteAnimations[animationID];
            if (strID != null && Sprite.Animations.ContainsKey(strID)) {
                if (Sprite.CurrentAnimationID != strID)
                    Sprite.Play(strID);
                Sprite.SetAnimationFrame(animationFrame);
            }
        }

        public void UpdateGeneric(Vector2 pos, Vector2 scale, Color color, Facings facing, Vector2 speed) {
            if (Holdable.Holder == null)
                Position = pos;
            if (scale.X > 0.0f) {
                scale.X = Calc.Clamp(scale.X, 0.5f, 1.5f);
            } else {
                scale.X = Calc.Clamp(scale.X, -0.5f, -1.5f);
            }
            if (scale.Y > 0.0f) {
                scale.Y = Calc.Clamp(scale.Y, 0.5f, 1.5f);
            } else {
                scale.Y = Calc.Clamp(scale.Y, -0.5f, -1.5f);
            }
            Sprite.Scale = scale;
            Sprite.Scale.X *= (float) facing;
            Sprite.Color = color * Alpha;
            Hair.Facing = facing;
            Speed = speed;
        }

        public void UpdateHair(Facings facing, Color[] colors, string texture0, bool simulateMotion) {
            if (PlayerGraphics == null)
                return;

            if (colors.Length <= 0)
                colors = new[] { Color.White };
            if (PlayerGraphics.HairCount < colors.Length)
                Array.Resize(ref colors, PlayerGraphics.HairCount);

            Hair.Facing = facing;
            HairColors = colors;
            Hair.Alpha = Alpha;
            PlayerGraphics.HairTextures[0] = texture0;
            Hair.SimulateMotion = simulateMotion;
        }

        public void UpdateDash(bool? wasB, Vector2? dir) {
            DashWasB = wasB;
            DashDir = dir;
        }

        public void UpdateDead(bool dead) {
            if (!Dead && dead)
                HandleDeath();
            Dead = dead;
        }

        public void UpdateFollowers(DataPlayerFrame.Entity[] followers) {
            for (int i = 0; i < followers.Length; i++) {
                DataPlayerFrame.Entity f = followers[i];
                GhostFollower gf;
                if (i >= Followers.Count) {
                    gf = new(this);
                    gf.Position = Position + Leader.Position;
                    Followers.Add(gf);
                } else {
                    gf = Followers[i];
                }
                gf.UpdateSprite(f.Scale, f.Depth, f.Color, f.SpriteRate, f.SpriteJustify, f.SpriteID, f.CurrentAnimationID, f.CurrentAnimationFrame);
            }

            while (Followers.Count > followers.Length) {
                GhostFollower gf = Followers[Followers.Count - 1];
                gf.Ghost = null;
                Followers.RemoveAt(Followers.Count - 1);
            }
        }

        public void UpdateHolding(DataPlayerFrame.Entity? h) {
            if (h == null) {
                if (Holding != null)
                    Holding.Ghost = null;
                Holding = null;
                return;
            }

            if (Holding == null)
                Holding = new(this);
            Holding.Position = h.Position;
            Holding.UpdateSprite(h.Scale, h.Depth, h.Color, h.SpriteRate, h.SpriteJustify, h.SpriteID, h.CurrentAnimationID, h.CurrentAnimationFrame);
        }


        public void HandleDeath() {
            if (Scene is not Level level ||
                level.Paused || level.Overlay != null ||
                CelesteNetClientModule.Settings.InGame.OtherPlayerOpacity == 0)
                return;
            level.Add(new GhostDeadBody(this, Vector2.Zero, Alpha));
        }

    }
}
