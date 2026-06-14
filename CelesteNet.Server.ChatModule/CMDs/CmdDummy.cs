using System.Collections.Generic;
using Celeste.Mod.CelesteNet.DataTypes;

namespace Celeste.Mod.CelesteNet.Server.Chat.Cmd {
    public class CmdDummy : ChatCmd {

        public override string Info => "Spawn or remove a static dummy of yourself that everyone can see (for testing).";

        public override void Run(CmdEnv env, List<ICmdArg>? args) {
            CelesteNetPlayerSession? self = env.Session;
            if (self == null || env.Player == null)
                throw new CommandRunException("You must be in-game to use /dummy.");

            // Toggle off if one already exists.
            if (Chat.Dummies.Has(self.SessionID)) {
                Chat.Dummies.Remove(self.SessionID);
                env.Send("Removed your dummy.");
                return;
            }

            // Snapshot the caller's current frame (position + pose), exactly like /tp does,
            // then spawn the dummy from it.
            DataChat? msg = env.Send("Spawning your dummy…");
            self.WaitFor<DataPlayerFrame>(400,
                (con, frame) => {
                    string result = Chat.Dummies.Spawn(self, frame);
                    if (msg != null) {
                        msg.Text = result;
                        Chat.ForceSend(msg);
                    }
                    return true;
                },
                () => {
                    if (msg != null) {
                        msg.Text = "Couldn't read your position — try again.";
                        Chat.ForceSend(msg);
                    }
                }
            );
        }

    }
}
