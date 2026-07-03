// Kudos Network Solution (KNS)
// NetworkStatsOverlay - in-game diagnostics HUD.
//
// DESIGN LINEAGE (Photon Fusion's stats window, Mirror's NetworkStatistics):
// the single most-used tool during integration. Drop it on any GameObject
// (or next to the KudosNetworkManager), press F3, and you get: role, room,
// tick, object count, per-peer RTT and away state, and per-channel in/out
// rates fed by NetworkStats counters inside the transport.
//
// WHY OnGUI: it works identically in editor, standalone, and on-device Quest
// builds with zero setup (no canvas, no camera, no input module). It is a
// debug tool, not shipping UI - strip it from release builds if you like.

using System.Text;
using Kudos.Network.Transport;
using UnityEngine;

namespace Kudos.Network.Utils
{
    [AddComponentMenu("Kudos/Kudos Network Stats Overlay")]
    public class NetworkStatsOverlay : MonoBehaviour
    {
        [Tooltip("Show/hide the overlay. Toggled by the hotkey below when the legacy input manager is enabled.")]
        public bool Visible = true;

        [Tooltip("Overlay anchor offset from the top-left corner, in pixels.")]
        public Vector2 Offset = new Vector2(10, 10);

        private readonly StringBuilder _sb = new StringBuilder(1024);
        private GUIStyle _style;

        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.F3)) Visible = !Visible;
#endif
            // TODO(integration): if you use the new Input System exclusively,
            // wire your own toggle action to the Visible flag.
        }

        private void OnGUI()
        {
            if (!Visible) return;

            var mgr = KudosNetworkManager.Instance;
            _sb.Length = 0;

            if (mgr == null || string.IsNullOrEmpty(mgr.CurrentRoomId))
            {
                _sb.Append("KNS: offline");
            }
            else
            {
                _sb.Append("KNS ").Append(mgr.IsHost ? "HOST" : "CLIENT")
                   .Append("  room=").Append(mgr.CurrentRoomId)
                   .Append("  tick=").Append(mgr.CurrentTick)
                   .Append("  objects=").Append(mgr.Objects.Count)
                   .Append('\n');

                AppendChannel("Reliable ", KudosChannel.Reliable);
                AppendChannel("StateSync", KudosChannel.StateSync);
                AppendChannel("Voice    ", KudosChannel.Voice);
                _sb.Append("total     in ").Append(NetworkStats.TotalInKbps().ToString("F1"))
                   .Append(" kbps | out ").Append(NetworkStats.TotalOutKbps().ToString("F1"))
                   .Append(" kbps\n");

                _sb.Append('\n').Append("peers (").Append(mgr.Peers.Count).Append("):\n");
                foreach (var kv in mgr.Peers)
                {
                    var p = kv.Value;
                    _sb.Append("  #").Append(p.PlayerId.Value)
                       .Append(' ').Append(string.IsNullOrEmpty(p.DisplayName) ? "(unnamed)" : p.DisplayName);
                    if (p.IsLocal) _sb.Append(" [you]");
                    if (p.IsHost) _sb.Append(" [host]");
                    if (p.IsAway) _sb.Append(" [away]");

                    // RTT is only measurable on links we actually hold: the host
                    // has one per client; a client has one to the host.
                    if (!p.IsLocal && p.ConnectionId >= 0 && mgr.Transport != null)
                    {
                        float rtt = mgr.Transport.GetRttMs(p.ConnectionId);
                        if (rtt >= 0f) _sb.Append("  rtt ").Append(rtt.ToString("F0")).Append(" ms");
                    }
                    _sb.Append('\n');
                }
            }

            _style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                richText = false,
                normal = { textColor = Color.white },
            };

            string text = _sb.ToString();
            var content = new GUIContent(text);
            Vector2 size = _style.CalcSize(content);
            // CalcSize measures a single line's width but not multiline height; be generous.
            var rect = new Rect(Offset.x, Offset.y, Mathf.Max(size.x, 360f), size.y * CountLines(text) + 8f);

            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8), content, _style);
        }

        private void AppendChannel(string label, KudosChannel channel)
        {
            var i = NetworkStats.InRate(channel);
            var o = NetworkStats.OutRate(channel);
            _sb.Append(label)
               .Append(" in ").Append(i.KilobitsPerSecond.ToString("F1")).Append(" kbps/")
               .Append(i.PacketsPerSecond).Append(" pps")
               .Append(" | out ").Append(o.KilobitsPerSecond.ToString("F1")).Append(" kbps/")
               .Append(o.PacketsPerSecond).Append(" pps\n");
        }

        private static int CountLines(string s)
        {
            int n = 1;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') n++;
            return n;
        }
    }
}
