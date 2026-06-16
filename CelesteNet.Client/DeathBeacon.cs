using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Celeste.Mod.CelesteNet.Client
{
    /// <summary>
    /// Reports in-game player deaths to the local Celeste Utils recorder over a
    /// loopback TCP connection. This is DELIBERATELY separate from CelesteNet's
    /// own client/server networking: it opens its own socket, needs no server
    /// connection, and works fully offline. The recorder uses these events to
    /// split a recording into one video per death-to-death attempt.
    ///
    /// Wire protocol (newline-delimited UTF-8 to 127.0.0.1:PORT):
    ///   HELLO|&lt;pid&gt;                              on connect
    ///   PING                                          heartbeat (~1s)
    ///   DIE|&lt;room&gt;|&lt;x&gt;,&lt;y&gt;|&lt;deaths&gt;|&lt;unixMs&gt;   on death
    /// The live connection itself is the heartbeat (connected = game ready).
    ///
    /// This file is injected into the CelesteNet.Client project at build time by
    /// src/server/source.py, which also wires Start()/Stop() into the module's
    /// Load()/Unload(). PORT must match recorder.events.DEFAULT_PORT.
    /// </summary>
    public static class DeathBeacon
    {
        private const string Host = "127.0.0.1";
        private const int Port = 38181; // keep in sync with recorder/events.py DEFAULT_PORT

        private static readonly object Gate = new();
        private static readonly ConcurrentQueue<string> Outbox = new();
        private static Thread Worker;
        private static volatile bool Running;

        public static void Start()
        {
            lock (Gate)
            {
                if (Running)
                    return;
                Running = true;
                Everest.Events.Player.OnDie += OnPlayerDie;
                Worker = new Thread(Loop) { Name = "CelesteUtils DeathBeacon", IsBackground = true };
                Worker.Start();
            }
            Logger.Log(LogLevel.INF, "deathbeacon", "Celeste Utils death beacon started");
        }

        public static void Stop()
        {
            Thread worker;
            lock (Gate)
            {
                if (!Running)
                    return;
                Running = false;
                Everest.Events.Player.OnDie -= OnPlayerDie;
                worker = Worker;
                Worker = null;
            }
            try
            {
                worker?.Join(2000);
            }
            catch
            {
                // ignore — we're shutting down
            }
            Logger.Log(LogLevel.INF, "deathbeacon", "Celeste Utils death beacon stopped");
        }

        private static void OnPlayerDie(global::Celeste.Player player)
        {
            try
            {
                string room = "";
                int deaths = 0;
                global::Celeste.Level level = player?.SceneAs<global::Celeste.Level>();
                if (level?.Session != null)
                {
                    room = level.Session.Level ?? "";
                    deaths = level.Session.Deaths;
                }
                float x = player?.Position.X ?? 0f;
                float y = player?.Position.Y ?? 0f;
                long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                room = room.Replace("|", "/"); // '|' is the field separator
                Outbox.Enqueue(string.Format(
                    CultureInfo.InvariantCulture, "DIE|{0}|{1},{2}|{3}|{4}", room, x, y, deaths, ms));
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.WRN, "deathbeacon", $"death capture failed: {ex.Message}");
            }
        }

        private static void Loop()
        {
            int pid = 0;
            try
            {
                pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch
            {
                // best-effort; pid is informational only
            }

            while (Running)
            {
                try
                {
                    using TcpClient client = new();
                    IAsyncResult connect = client.BeginConnect(Host, Port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(2000) || !client.Connected)
                    {
                        try { client.Close(); } catch { }
                        Sleep(2000); // recorder not listening yet — retry
                        continue;
                    }
                    client.EndConnect(connect);
                    using NetworkStream stream = client.GetStream();
                    Send(stream, "HELLO|" + pid.ToString(CultureInfo.InvariantCulture));

                    long lastPing = 0;
                    while (Running && client.Connected)
                    {
                        bool sent = false;
                        while (Outbox.TryDequeue(out string line))
                        {
                            Send(stream, line);
                            sent = true;
                        }
                        long now = Environment.TickCount64;
                        if (now - lastPing >= 1000)
                        {
                            Send(stream, "PING");
                            lastPing = now;
                        }
                        if (!sent)
                            Sleep(100);
                    }
                }
                catch
                {
                    // connection dropped / recorder gone — pause, then reconnect
                    Sleep(2000);
                }
            }
        }

        private static void Send(NetworkStream stream, string line)
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        private static void Sleep(int ms)
        {
            // Sleep in small slices so Stop() takes effect promptly.
            int slept = 0;
            while (Running && slept < ms)
            {
                Thread.Sleep(50);
                slept += 50;
            }
        }
    }
}
