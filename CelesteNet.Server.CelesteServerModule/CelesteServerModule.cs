using System;
using System.IO;
using Celeste.Mod.CelesteNet;
using Celeste.Mod.CelesteNet.DataTypes;
using Celeste.Mod.CelesteNet.Server;
using Celeste.Mod.CelesteNet.Server.Chat;

namespace Celeste.Mod.CelesteNet.Server.CelesteServer {
    public class CelesteServerSettings : CelesteNetServerModuleSettings {
        public string LogPath { get; set; } = "log.txt";
    }

    public class CelesteServerModule : CelesteNetServerModule<CelesteServerSettings> {
        private readonly object LogLock = new();
        private ChatModule? Chat;

        public override void Start() {
            base.Start();
            WriteEvent("server_start", "CelesteNet server started");

            Server.OnSessionStart += OnSessionStart;
            Server.OnDisconnect += OnDisconnect;

            if (Server.TryGet(out ChatModule? chat)) {
                Chat = chat;
                Chat.OnReceive += OnChatReceive;
                Chat.OnApplyFilter += OnChatFilter;
            }
        }

        public override void Dispose() {
            if (Chat != null) {
                Chat.OnReceive -= OnChatReceive;
                Chat.OnApplyFilter -= OnChatFilter;
                Chat = null;
            }

            Server.OnSessionStart -= OnSessionStart;
            Server.OnDisconnect -= OnDisconnect;

            using (Server.ConLock.R())
                foreach (CelesteNetPlayerSession session in Server.Sessions)
                    session.OnEnd -= OnSessionEnd;

            WriteEvent("server_stop", "CelesteNet server stopped");
            base.Dispose();
        }

        private void OnSessionStart(CelesteNetPlayerSession session) {
            session.OnEnd += OnSessionEnd;
            DataPlayerInfo? info = session.PlayerInfo;
            WriteEvent("login", $"{Clean(info?.FullName ?? session.Name)} uid={Clean(session.UID)} tcp={Ping(session)} udp={UdpPing(session)}");
        }

        private void OnSessionEnd(CelesteNetPlayerSession session, DataPlayerInfo? lastPlayerInfo) {
            session.OnEnd -= OnSessionEnd;
            WriteEvent("logout", $"{Clean(lastPlayerInfo?.FullName ?? session.Name)} uid={Clean(session.UID)}");
        }

        private void OnDisconnect(CelesteNetServer server, CelesteNetConnection con, CelesteNetPlayerSession? session) {
            if (session == null)
                WriteEvent("connection_close", Clean(con.ToString()));
        }

        private void OnChatReceive(ChatModule chat, DataChat msg) {
            if (string.IsNullOrEmpty(msg.Text))
                return;

            string player = Clean(msg.Player?.FullName ?? "server");
            string scope = msg.Targets == null ? "global" : "private";
            // DESIGN INVARIANT: be careful not to break this. The chat channel is end-to-end
            // TLS-encrypted; the on-disk event log keeps who/when/scope for moderation but MUST
            // redact the message body so chat is never persisted in plaintext at rest.
            WriteEvent("chat", $"{player} [{scope}] [redacted len={msg.Text.Length}]");
        }

        private void OnChatFilter(ChatModule chat, FilterDecision decision) {
            // Redacted: keep the filter decision/cause/player but never the offending message text.
            WriteEvent("chat_filter", $"{decision.Handling} {decision.Cause} player={Clean(decision.playerName)} textlen={decision.chatText?.Length ?? 0}");
        }

        private static int? Ping(CelesteNetPlayerSession session)
            => session.Con is ConPlusTCPUDPConnection con ? con.TCPPingMs : null;

        private static int? UdpPing(CelesteNetPlayerSession session)
            => session.Con is ConPlusTCPUDPConnection con ? con.UDPPingMs : null;

        private void WriteEvent(string kind, string message) {
            try {
                string path = Path.GetFullPath(Settings.LogPath);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string line = $"{DateTimeOffset.UtcNow:O}\t{kind}\t{Clean(message)}{Environment.NewLine}";
                lock (LogLock)
                    File.AppendAllText(path, line);
            } catch (Exception e) {
                Logger.Log(LogLevel.WRN, "celeste-server-log", $"Failed writing event log: {e.Message}");
            }
        }

        private static string Clean(string? value)
            => (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
    }
}
