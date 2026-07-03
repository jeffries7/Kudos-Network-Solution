// Kudos Network Solution (KNS)
// Browser-side WebRTC + WebSocket implementation for WebGL builds.
// Counterpart of Runtime/Transport/WebGL/WebGlBridge.cs

mergeInto(LibraryManager.library, {

  // ------------------------------------------------------------ shared state
  $KNS: {
    peers: {},        // handle -> { pc, channels: {0:dc,1:dc,2:dc}, openCount, cb }
    sockets: {},
    nextHandle: 1,
    str: function (ptr) { return UTF8ToString(ptr); },
    alloc: function (str) {
      var size = lengthBytesUTF8(str) + 1;
      var ptr = _malloc(size);
      stringToUTF8(str, ptr, size);
      return ptr;
    },
    callStr: function (cb, handle, str) {
      var ptr = KNS.alloc(str);
      {{{ makeDynCall('vii', 'cb') }}}(handle, ptr);
      _free(ptr);
    }
  },

  // ------------------------------------------------------------ RTCPeerConnection
  KNS_RtcCreate__deps: ['$KNS'],
  KNS_RtcCreate: function (configPtr, onOpen, onClose, onIce, onMsg, onSdp) {
    var cfg = JSON.parse(KNS.str(configPtr));
    var iceServers = [];
    (cfg.stunUrls || []).forEach(function (u) { iceServers.push({ urls: u }); });
    if (cfg.turnUrl) iceServers.push({ urls: cfg.turnUrl, username: cfg.turnUsername, credential: cfg.turnCredential });

    var handle = KNS.nextHandle++;
    var pc = new RTCPeerConnection({ iceServers: iceServers });
    var peer = { pc: pc, channels: {}, openCount: 0, expected: 3,
                 cb: { onOpen: onOpen, onClose: onClose, onIce: onIce, onMsg: onMsg, onSdp: onSdp } };
    KNS.peers[handle] = peer;

    pc.onicecandidate = function (e) {
      if (e.candidate) KNS.callStr(onIce, handle, JSON.stringify(e.candidate));
    };
    pc.onconnectionstatechange = function () {
      if (pc.connectionState === 'failed' || pc.connectionState === 'closed' ||
          pc.connectionState === 'disconnected')
        {{{ makeDynCall('vi', 'onClose') }}}(handle);
    };
    // Host side: channels arrive from the initiator.
    pc.ondatachannel = function (e) { KNS._attach(handle, e.channel); };
    return handle;
  },

  $KNS__postset: 'KNS._attach = function(handle, dc) {\n' +
    '  var peer = KNS.peers[handle]; if (!peer) return;\n' +
    '  var ch = parseInt(dc.label, 10);\n' +
    '  dc.binaryType = "arraybuffer";\n' +
    '  peer.channels[ch] = dc;\n' +
    '  dc.onopen = function() {\n' +
    '    peer.openCount++;\n' +
    '    if (peer.openCount === peer.expected) dynCall_vi(peer.cb.onOpen, handle);\n' +
    '  };\n' +
    '  dc.onmessage = function(e) {\n' +
    '    var bytes = new Uint8Array(e.data);\n' +
    '    var ptr = _malloc(bytes.length);\n' +
    '    HEAPU8.set(bytes, ptr);\n' +
    '    dynCall_viiii(peer.cb.onMsg, handle, ch, ptr, bytes.length);\n' +
    '    _free(ptr);\n' +
    '  };\n' +
    '};',

  KNS_RtcCreateChannel__deps: ['$KNS'],
  KNS_RtcCreateChannel: function (handle, channel, ordered, maxRetransmits) {
    var peer = KNS.peers[handle]; if (!peer) return;
    var opts = { ordered: !!ordered };
    if (maxRetransmits >= 0) opts.maxRetransmits = maxRetransmits;
    var dc = peer.pc.createDataChannel(String(channel), opts);
    KNS._attach(handle, dc);
  },

  KNS_RtcCreateOffer__deps: ['$KNS'],
  KNS_RtcCreateOffer: function (handle) {
    var peer = KNS.peers[handle]; if (!peer) return;
    peer.pc.createOffer().then(function (offer) {
      return peer.pc.setLocalDescription(offer).then(function () {
        KNS.callStr(peer.cb.onSdp, handle, JSON.stringify(peer.pc.localDescription));
      });
    });
  },

  KNS_RtcCreateAnswer__deps: ['$KNS'],
  KNS_RtcCreateAnswer: function (handle) {
    var peer = KNS.peers[handle]; if (!peer) return;
    peer.pc.createAnswer().then(function (answer) {
      return peer.pc.setLocalDescription(answer).then(function () {
        KNS.callStr(peer.cb.onSdp, handle, JSON.stringify(peer.pc.localDescription));
      });
    });
  },

  KNS_RtcSetRemote__deps: ['$KNS'],
  KNS_RtcSetRemote: function (handle, sdpPtr) {
    var peer = KNS.peers[handle]; if (!peer) return;
    peer.pc.setRemoteDescription(JSON.parse(KNS.str(sdpPtr)));
  },

  KNS_RtcAddIce__deps: ['$KNS'],
  KNS_RtcAddIce: function (handle, jsonPtr) {
    var peer = KNS.peers[handle]; if (!peer) return;
    peer.pc.addIceCandidate(JSON.parse(KNS.str(jsonPtr)));
  },

  KNS_RtcSend__deps: ['$KNS'],
  KNS_RtcSend: function (handle, channel, dataPtr, length) {
    var peer = KNS.peers[handle]; if (!peer) return;
    var dc = peer.channels[channel];
    if (dc && dc.readyState === 'open')
      dc.send(HEAPU8.subarray(dataPtr, dataPtr + length));
  },

  KNS_RtcClose__deps: ['$KNS'],
  KNS_RtcClose: function (handle) {
    var peer = KNS.peers[handle];
    if (peer) { peer.pc.close(); delete KNS.peers[handle]; }
  },

  KNS_RtcGetRtt__deps: ['$KNS'],
  KNS_RtcGetRtt: function (handle) {
    var peer = KNS.peers[handle];
    return peer && peer.rtt ? peer.rtt : -1;
    // Production: poll pc.getStats() periodically and cache currentRoundTripTime.
  },

  // ------------------------------------------------------------ WebSocket (signaling)
  KNS_WsConnect__deps: ['$KNS'],
  KNS_WsConnect: function (urlPtr, onOpen, onMsg, onClose) {
    var handle = KNS.nextHandle++;
    var ws = new WebSocket(KNS.str(urlPtr));
    KNS.sockets[handle] = ws;
    ws.onopen = function () { {{{ makeDynCall('v', 'onOpen') }}}(); };
    ws.onmessage = function (e) {
      var ptr = KNS.alloc(e.data);
      {{{ makeDynCall('vi', 'onMsg') }}}(ptr);
      _free(ptr);
    };
    ws.onclose = function (e) {
      var ptr = KNS.alloc(e.reason || 'closed');
      {{{ makeDynCall('vi', 'onClose') }}}(ptr);
      _free(ptr);
    };
    return handle;
  },

  KNS_WsSend__deps: ['$KNS'],
  KNS_WsSend: function (handle, textPtr) {
    var ws = KNS.sockets[handle];
    if (ws && ws.readyState === 1) ws.send(KNS.str(textPtr));
  },

  KNS_WsClose__deps: ['$KNS'],
  KNS_WsClose: function (handle) {
    var ws = KNS.sockets[handle];
    if (ws) { ws.close(); delete KNS.sockets[handle]; }
  }
});
