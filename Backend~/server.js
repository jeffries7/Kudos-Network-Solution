// Kudos Network Solution (KNS) - "Nexus" backend
// WebRTC signaling relay + room registry with atomic fill-or-create.
//
// This is intentionally tiny (one file, one dependency). It holds NO game
// state and relays NO game traffic - after WebRTC connects, all gameplay and
// voice flows peer-to-peer. Nexus only:
//   1. relays SDP offers/answers + ICE candidates between peers
//   2. answers "join-or-create" with an atomic room assignment
//   3. tracks room occupancy via host heartbeats
//   4. re-homes rooms during host migration ("claim-host")
//
// Atomicity is free: Node's event loop is single-threaded, so join-or-create
// decisions can never interleave.
//
// Run:  npm install && npm start        (listens on ws://0.0.0.0:8787)
// Prod: put it behind nginx/caddy for wss:// TLS termination, and run a
//       coturn server for TURN relay (see package README).

const { WebSocketServer } = require('ws');

const PORT = process.env.PORT || 8787;
const ROOM_TTL_MS = 30_000;         // room evicted if no host heartbeat for this long
const HEARTBEAT_SWEEP_MS = 10_000;

/** peerId -> WebSocket */
const peers = new Map();

/** roomId -> { roomId, sceneKey, hostPeerId, playerCount, maxPlayers, lastSeen } */
const rooms = new Map();

let nextRoomNumber = 1;

const wss = new WebSocketServer({ port: PORT });
console.log(`[Nexus] listening on :${PORT}`);

wss.on('connection', (ws) => {
  let peerId = null;

  ws.on('message', (raw) => {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }

    switch (msg.type) {
      // ---------------------------------------------------------- identity
      case 'hello':
        peerId = msg.from;
        peers.set(peerId, ws);
        break;

      // ---------------------------------------------------------- fill-or-create
      case 'join-or-create': {
        const { sceneKey, maxPlayers } = msg;

        // Find any live room hosting this scene with space (atomic - see header).
        let target = null;
        for (const room of rooms.values()) {
          if (room.sceneKey === sceneKey &&
              room.playerCount < room.maxPlayers &&
              peers.has(room.hostPeerId)) {
            target = room;
            break;
          }
        }

        if (target) {
          // Optimistic bump so simultaneous joiners spread across rooms correctly;
          // corrected by the next host heartbeat.
          target.playerCount += 1;
          send(ws, {
            type: 'room-assigned', roomId: target.roomId, sceneKey,
            isHost: false, hostPeerId: target.hostPeerId,
          });
        } else {
          const roomId = `room-${sceneKey}-${nextRoomNumber++}`;
          rooms.set(roomId, {
            roomId, sceneKey, hostPeerId: peerId,
            playerCount: 1, maxPlayers: maxPlayers || 32,
            lastSeen: Date.now(),
          });
          send(ws, { type: 'room-assigned', roomId, sceneKey, isHost: true, hostPeerId: peerId });
          console.log(`[Nexus] created ${roomId}, host ${peerId}`);
        }
        break;
      }

      // ---------------------------------------------------------- reconnect grace
      // A dropped client asks to re-enter a SPECIFIC room. If the room and its
      // host are both alive, this was just their link blipping -> hand back the
      // host so they can resume. If not, the host died -> error, and the client
      // falls through to host migration.
      case 'rejoin-room': {
        const room = rooms.get(msg.roomId);
        if (room && peers.has(room.hostPeerId)) {
          send(ws, {
            type: 'room-assigned', roomId: room.roomId, sceneKey: room.sceneKey,
            isHost: false, hostPeerId: room.hostPeerId,
          });
        } else {
          send(ws, { type: 'error', error: 'room-gone' });
        }
        break;
      }

      // ---------------------------------------------------------- WebRTC signaling relay
      case 'offer':
      case 'answer':
      case 'ice': {
        const targetWs = peers.get(msg.to);
        if (targetWs) send(targetWs, msg);   // relay verbatim; Nexus never inspects SDP
        break;
      }

      // ---------------------------------------------------------- occupancy
      case 'heartbeat': {
        const room = rooms.get(msg.roomId);
        if (room && room.hostPeerId === peerId) {
          room.playerCount = msg.maxPlayers;  // field reused as current count
          room.lastSeen = Date.now();
        }
        break;
      }

      case 'leave': {
        const room = rooms.get(msg.roomId);
        if (room && room.hostPeerId === peerId) {
          rooms.delete(msg.roomId);
          console.log(`[Nexus] host left, deleted ${msg.roomId} (migration may re-claim)`);
        }
        break;
      }

      // ---------------------------------------------------------- host migration
      case 'claim-host': {
        // Migration winner re-homes the room (or recreates it if already evicted).
        const room = rooms.get(msg.roomId);
        if (room) {
          room.hostPeerId = peerId;
          room.lastSeen = Date.now();
        } else {
          rooms.set(msg.roomId, {
            roomId: msg.roomId, sceneKey: msg.sceneKey, hostPeerId: peerId,
            playerCount: 1, maxPlayers: 32, lastSeen: Date.now(),
          });
        }
        console.log(`[Nexus] ${msg.roomId} claimed by new host ${peerId}`);
        break;
      }
    }
  });

  ws.on('close', () => {
    if (!peerId) return;
    peers.delete(peerId);
    // Rooms hosted by this peer are NOT deleted immediately: the KNS clients run
    // host migration and the winner will 'claim-host'. The TTL sweep cleans up
    // rooms whose migration never completed.
  });
});

// Evict rooms whose host stopped heartbeating and was never re-claimed.
setInterval(() => {
  const now = Date.now();
  for (const [roomId, room] of rooms) {
    if (now - room.lastSeen > ROOM_TTL_MS && !peers.has(room.hostPeerId)) {
      rooms.delete(roomId);
      console.log(`[Nexus] evicted stale ${roomId}`);
    }
  }
}, HEARTBEAT_SWEEP_MS);

function send(ws, obj) {
  if (ws.readyState === 1) ws.send(JSON.stringify(obj));
}
