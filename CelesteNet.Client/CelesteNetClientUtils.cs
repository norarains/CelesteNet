using MC = Mono.Cecil;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Utils;
using MonoMod.ModInterop;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Collections;

namespace Celeste.Mod.CelesteNet.Client {
    public static class CelesteNetClientUtils {

        static CelesteNetClientUtils() {
            typeof(MotionSmoothingImports).ModInterop();
        }

        [ModImportName("MotionSmoothing")]
        private static class MotionSmoothingImports {
            public static Func<Vector2> GetFractionalCameraOffset = () => Vector2.Zero;
        }

        private static Vector2 GetMotionSmoothingCameraOffset() {
            try {
                return MotionSmoothingImports.GetFractionalCameraOffset();
            } catch {
                return Vector2.Zero;
            }
        }

        public static float GetScreenScale(this Level level)
            => level.Zoom * ((320f - level.ScreenPadding * 2f) / 320f);

        public static Vector2 WorldToScreen(this Level level, Vector2 pos) {
            Camera cam = level.Camera;
            if (cam == null)
                return pos;

            pos += GetMotionSmoothingCameraOffset();
            pos -= cam.Position;

            Vector2 size = new(320f, 180f);
            Vector2 sizeScaled = size / level.ZoomTarget;
            Vector2 offs = level.ZoomTarget != 1f ? (level.ZoomFocusPoint - sizeScaled / 2f) / (size - sizeScaled) * size : Vector2.Zero;
            float scale = level.GetScreenScale();

            pos += new Vector2(level.ScreenPadding, level.ScreenPadding * 0.5625f);

            pos -= offs;
            pos *= scale;
            pos += offs;

            pos *= 6f; // 1920 / 320

            if (SaveData.Instance?.Assists.MirrorMode ?? false)
                pos.X = 1920f - pos.X;

            return pos;
        }

        public static bool GetClampedScreenPos(Vector2 worldPos, Level level, out Vector2 outPos, float marginX, float marginY, float offsetX = 0f, float offsetY = 0f) {
            return GetClampedScreenPos(worldPos, level, out outPos, marginX, marginY, marginX, marginY, offsetX, offsetY);
        }

        public static bool GetClampedScreenPos(Vector2 worldPos, Level level, out Vector2 outPos, float marginLeft, float marginTop, float marginRight, float marginBottom, float offsetX = 0f, float offsetY = 0f) {
            if (level == null) {
                outPos = Vector2.Zero;
                return false;
            }

            worldPos.X += offsetX;
            worldPos.Y += offsetY;

            Vector2 posScreen = level.WorldToScreen(worldPos);
            outPos = posScreen.Clamp(
                marginLeft, marginTop,
                1920f - marginRight, 1080f - marginBottom
            );
            return outPos.Equals(posScreen);
        }

        private readonly static FieldInfo? f_Player_wasDashB =
            typeof(Player).GetField("wasDashB", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool? GetWasDashB(this Player self)
            => (bool?) f_Player_wasDashB?.GetValue(self);

        private readonly static FieldInfo? f_Player_jumpGraceTimer =
            typeof(Player).GetField("jumpGraceTimer", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Give the player a coyote-time grace window — the gate Celeste's DashUpdate uses
        /// to allow a super/hyper dash jump. Used so dashing onto another player's head can launch a
        /// real super/hyper (Celeste resolves super vs hyper vs reverse from duck/Facing/input),
        /// instead of only the auto-jump bounce. Defaults to Player.JumpGraceTime (0.1s).</summary>
        public static void GrantJumpGrace(this Player self, float time = 0.1f)
            => f_Player_jumpGraceTimer?.SetValue(self, time);

        private readonly static FieldInfo? f_Level_updateHair =
            typeof(Level).GetField("updateHair", BindingFlags.NonPublic | BindingFlags.Instance);

        public static bool? GetUpdateHair(this Level self)
            => (bool?) f_Level_updateHair?.GetValue(self);

        private readonly static FieldInfo? f_TrailManager_shapshots =
            typeof(TrailManager).GetField("snapshots", BindingFlags.NonPublic | BindingFlags.Instance);

        public static TrailManager.Snapshot[]? GetSnapshots(this TrailManager self)
            => (TrailManager.Snapshot[]?) f_TrailManager_shapshots?.GetValue(self);

        // easier ways to check these properties on nullable ButtonBindings without null-related issues...
        public static bool Check(this ButtonBinding? binding) => binding?.Check == true;
        public static bool Pressed(this ButtonBinding? binding) => binding?.Pressed == true;
        public static bool Released(this ButtonBinding? binding) => binding?.Released == true;
        public static bool Repeating(this ButtonBinding? binding) => binding?.Repeating == true;

    }
}
