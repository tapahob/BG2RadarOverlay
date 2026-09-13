  var relayUrl = null;
  var streamKey = null;
  var controlKey = null;
  var pollTimer = null;

  // Set from config.html's saved bitsPerToken/pointsPerToken/rewardName - what one summon token
  // costs in each currency, and which Custom Reward sells them for Channel Points. 0 means that
  // currency is off.
  var tokenPrice = { bits: 0, points: 0, rewardName: '' };

  var iconEl = document.getElementById('icon');
  var panelEl = document.getElementById('panel');
  var contentEl = document.getElementById('content');

  iconEl.addEventListener('click', function () {
    panelEl.classList.toggle('open');
  });

  // The same "Relay Server URL" also gets typed into the Radar app's Options tab, which needs
  // the opposite scheme for its WebSocket connection (see TwitchRelayClient.normalizeToWebSocketScheme
  // on that side) - two identically-labelled fields wanting different prefixes for the same host
  // is an easy way to get one of them wrong. fetch() only accepts http(s), so normalize here
  // rather than have a pasted "wss://" fail with an opaque "Failed to fetch".
  function normalizeToHttpScheme(url) {
    if (/^wss:\/\//i.test(url)) return 'https://' + url.slice(6);
    if (/^ws:\/\//i.test(url)) return 'http://' + url.slice(5);
    return url;
  }

  function applyConfig(rawContent) {
    if (!rawContent) return;
    try {
      var parsed = JSON.parse(rawContent);
      relayUrl = normalizeToHttpScheme(parsed.relayUrl || '');
      streamKey = parsed.streamKey;
      // Only ever present in the local mock's localStorage - see config.html. A real viewer's
      // configuration cannot carry this, so the Summon button stays a preview for them.
      controlKey = parsed.controlKey || null;
      tokenPrice = {
        bits: Number(parsed.bitsPerToken) || 0,
        points: Number(parsed.pointsPerToken) || 0,
        rewardName: parsed.rewardName || ''
      };
      startPolling();
      updateSendState();
    } catch (e) {
      // Not configured yet (or content is malformed) - stay in the "waiting for stream
      // data" state instead of throwing.
    }
  }

  // mock/index.html loads this file as `video_overlay.html?mock=1`. The real
  // twitch-ext.min.js loads fine even outside an actual Twitch iframe (it's just a public
  // script) and Twitch.ext.configuration.onChanged still exists as a function - it just
  // never fires outside Twitch's real postMessage bridge - so that alone isn't a reliable
  // "are we really in Twitch" signal. Use the explicit flag instead.
  var isMock = new URLSearchParams(location.search).get('mock') === '1';
  if (!isMock && window.Twitch && Twitch.ext && Twitch.ext.configuration) {
    Twitch.ext.configuration.onChanged(function () {
      var cfg = Twitch.ext.configuration.broadcaster;
      applyConfig(cfg && cfg.content);
    });
  } else {
    applyConfig(localStorage.getItem('bg2ext_mock_config'));
    window.addEventListener('storage', function (e) {
      if (e.key === 'bg2ext_mock_config') applyConfig(e.newValue);
    });
  }

  // ---- Viewer identity ----
  //
  // Only real viewers reach this - the mock (controlKey set) never gets a real onAuthorized call,
  // and doesn't need one: it spends nothing, it dispatches directly with the control key the
  // streamer pasted in herself. Everything token-related below (balance, buying, spending) is
  // gated on authToken/viewerHasIdentity, which stay null/false forever in mock mode - the old
  // direct-dispatch path in the Summon handler is what actually runs there instead.
  var authToken = null;
  var viewerHasIdentity = false;

  if (!isMock && window.Twitch && Twitch.ext && typeof Twitch.ext.onAuthorized === 'function') {
    Twitch.ext.onAuthorized(function (auth) {
      authToken = auth.token;
      // requestIdShare() causes onAuthorized to fire again with userId now populated - this
      // handler re-running is what picks that up, not a separate callback.
      viewerHasIdentity = !!auth.userId;
      if (viewerHasIdentity) fetchBalance();
      renderSummonState();
    });
  }

  var shareIdentityEl = document.getElementById('shareIdentity');
  var shareIdentityBtn = document.getElementById('shareIdentityBtn');
  shareIdentityBtn.addEventListener('click', function () {
    if (window.Twitch && Twitch.ext && Twitch.ext.actions && typeof Twitch.ext.actions.requestIdShare === 'function') {
      Twitch.ext.actions.requestIdShare();
    }
  });

  function startPolling() {
    if (pollTimer) clearInterval(pollTimer);
    fetchStatus();
    pollTimer = setInterval(fetchStatus, 5000);
  }

  function fetchStatus() {
    if (!relayUrl || !streamKey) return;
    fetch(relayUrl + '/api/character/' + encodeURIComponent(streamKey))
      .then(function (res) {
        if (!res.ok) throw new Error('not live');
        return res.json();
      })
      .then(renderSnapshot)
      .catch(function () {
        contentEl.innerHTML = '<div id="offline">Streamer is offline or hasn\'t opened the game yet.</div>';
        renderPacks([]);
      });
  }

  function renderSnapshot(data) {
    renderParty(data);
    renderPacks((data && data.packs) || []);
  }

  function renderParty(data) {
    var party = (data && data.party) || [];
    if (party.length === 0) {
      contentEl.innerHTML = '<div id="empty">No party data yet.</div>';
      return;
    }
    var html = '';
    for (var i = 0; i < party.length; i++) {
      var m = party[i];
      html += '<div class="member">'
        + '<div class="name">' + escapeHtml(m.name || '?') + '</div>'
        + '<div class="meta">' + escapeHtml(m.race || '') + ' ' + escapeHtml(m['class'] || '') + '</div>'
        + '<div class="hp">HP: ' + (m.currentHp != null ? m.currentHp : '?') + '</div>'
        + '</div>';
    }
    contentEl.innerHTML = html;
  }

  // ---- Summon section ----
  //
  // The tiles are the streamer's own packs, straight out of the snapshot the overlay pushes -
  // not a list hardcoded here. What a pack contains stays on the overlay side; a viewer only
  // ever names an id, so nothing about the streamer's creature list has to be trusted from the
  // browser. Cost is in *tokens* - what a token itself costs in Bits/Channel Points is set
  // separately in config.html (tokenPrice, above).
  var packs = [];
  var selected = {};
  var balance = 0;

  var packsEl = document.getElementById('packs');
  var messageEl = document.getElementById('message');
  var counterEl = document.getElementById('counter');
  var sendEl = document.getElementById('send');
  var sendResultEl = document.getElementById('sendResult');
  var totalEl = document.getElementById('total');
  var balanceEl = document.getElementById('balance');
  var buyTokensEl = document.getElementById('buyTokens');
  var buyTokensNeedEl = document.getElementById('buyTokensNeed');
  var bitsProductsEl = document.getElementById('bitsProducts');
  var pointsInstructionsEl = document.getElementById('pointsInstructions');

  // Rebuilding the tiles on every 5s poll would fight the viewer for their own selection
  // (and restart the CSS hover state mid-click), so redraw only when the list actually
  // changed - which includes a pack becoming available as the party levels.
  var renderedSignature = null;

  function signatureOf(list) {
    var parts = [];
    for (var i = 0; i < list.length; i++) {
      var p = list[i];
      parts.push(p.id + '|' + p.name + '|' + p.from + '-' + p.to + '|' + p.cost + '|' + (p.available ? 1 : 0));
    }
    return parts.join(';');
  }

  // No per-pack artwork exists - the streamer types a name, not an icon - so the tile shows
  // initials. Deterministic, and it keeps the square tile shape the layout is built around.
  function initialsOf(name) {
    var words = String(name || '').split(/\s+/);
    var out = '';
    for (var i = 0; i < words.length && out.length < 2; i++) {
      if (words[i].length > 0) out += words[i].charAt(0).toUpperCase();
    }
    return out || '?';
  }

  function renderPacks(list) {
    var signature = signatureOf(list);
    if (signature === renderedSignature) return;
    renderedSignature = signature;
    packs = list;

    // Drop selections for packs that no longer exist or have gone out of band, so the Summon
    // button can't be sending an id the streamer deleted while the panel sat open.
    var stillValid = {};
    for (var i = 0; i < list.length; i++) {
      if (list[i].available && selected[list[i].id]) stillValid[list[i].id] = true;
    }
    selected = stillValid;

    packsEl.innerHTML = '';

    if (list.length === 0) {
      var empty = document.createElement('div');
      empty.id = 'packsEmpty';
      empty.textContent = 'No summon packs set up yet.';
      packsEl.appendChild(empty);
      updateSendState();
      return;
    }

    for (var j = 0; j < list.length; j++) {
      (function (pack) {
        var el = document.createElement('div');
        el.className = 'pack' + (pack.available ? '' : ' unavailable');
        el.setAttribute('data-pack', pack.id);
        el.title = pack.available
          ? pack.name
          : pack.name + ' - unlocks at level ' + pack.from;
        el.innerHTML = '<div class="glyph">' + escapeHtml(initialsOf(pack.name)) + '</div>'
                     + '<div class="caption">' + escapeHtml(pack.name) + '</div>'
                     + '<div class="band">Lv ' + pack.from + '-' + pack.to + '</div>'
                     + '<div class="cost">' + costLabel(pack.cost) + '</div>';
        if (pack.available) {
          el.addEventListener('click', function () { togglePack(pack.id); });
        }
        packsEl.appendChild(el);
      })(list[j]);
    }

    applySelectionClasses();
    updateSendState();
  }

  function togglePack(id) {
    if (selected[id]) delete selected[id];
    else selected[id] = true;
    applySelectionClasses();
    updateSendState();
  }

  function applySelectionClasses() {
    var tiles = packsEl.getElementsByClassName('pack');
    for (var i = 0; i < tiles.length; i++) {
      var tile = tiles[i];
      var id = tile.getAttribute('data-pack');
      var base = tile.className.indexOf('unavailable') >= 0 ? 'pack unavailable' : 'pack';
      tile.className = selected[id] ? base + ' selected' : base;
    }
  }

  function selectedIds() {
    var ids = [];
    for (var i = 0; i < packs.length; i++) {
      if (selected[packs[i].id]) ids.push(packs[i].id);
    }
    return ids;
  }

  // A missing or zero cost is "free" rather than "0 tokens" - a streamer who never set prices
  // shouldn't have every tile shouting a number at viewers.
  function costLabel(cost) {
    var value = Number(cost) || 0;
    return value > 0 ? value.toLocaleString() + ' tok' : 'free';
  }

  function selectedTotal() {
    var total = 0;
    for (var i = 0; i < packs.length; i++) {
      if (selected[packs[i].id]) total += Number(packs[i].cost) || 0;
    }
    return total;
  }

  // ---- Balance ----

  function fetchBalance() {
    if (!relayUrl || !streamKey || !authToken) return;
    fetch(relayUrl + '/api/balance/' + encodeURIComponent(streamKey), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ authToken: authToken })
    })
      .then(function (res) { return res.ok ? res.json() : null; })
      .then(function (data) {
        if (data && typeof data.balance === 'number') balance = data.balance;
        renderSummonState();
      })
      .catch(function () { /* stays at last known balance */ });
  }

  // ---- Buying tokens ----

  var bitsProductsCache = null;

  function loadBitsProducts() {
    if (tokenPrice.bits <= 0 || !window.Twitch || !Twitch.ext || !Twitch.ext.bits) {
      bitsProductsEl.innerHTML = '';
      return;
    }
    if (bitsProductsCache) {
      renderBitsProducts(bitsProductsCache);
      return;
    }
    Twitch.ext.bits.getProducts().then(function (products) {
      bitsProductsCache = products || [];
      renderBitsProducts(bitsProductsCache);
    }).catch(function () { bitsProductsEl.innerHTML = ''; });
  }

  function renderBitsProducts(products) {
    bitsProductsEl.innerHTML = '';
    for (var i = 0; i < products.length; i++) {
      (function (product) {
        var amount = Number(product.cost && product.cost.amount) || 0;
        if (amount <= 0) return;
        var tokens = Math.floor(amount / tokenPrice.bits);
        if (tokens <= 0) return;

        var btn = document.createElement('button');
        btn.className = 'bitsProduct';
        btn.textContent = tokens + ' tok for ' + amount.toLocaleString() + ' Bits';
        btn.addEventListener('click', function () {
          Twitch.ext.bits.useBits(product.sku);
        });
        bitsProductsEl.appendChild(btn);
      })(products[i]);
    }
  }

  // Registered once, globally - Twitch calls this whenever any Bits transaction on this
  // extension completes, not per-useBits() call. Every product here exists to buy tokens, so
  // any completed transaction is treated as one.
  if (!isMock && window.Twitch && Twitch.ext && Twitch.ext.bits && typeof Twitch.ext.bits.onTransactionComplete === 'function') {
    Twitch.ext.bits.onTransactionComplete(function (transaction) {
      if (!relayUrl || !streamKey || !transaction || !transaction.transactionReceipt) return;
      sendResultEl.textContent = 'Payment received, crediting tokens…';
      fetch(relayUrl + '/api/bits-purchase/' + encodeURIComponent(streamKey), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ receipts: [transaction.transactionReceipt] })
      })
        .then(function (res) { return res.json(); })
        .then(function (data) {
          if (typeof data.balance === 'number') balance = data.balance;
          sendResultEl.textContent = 'Bought ' + (data.credited || 0) + ' token(s).';
          renderSummonState();
        })
        .catch(function () {
          sendResultEl.textContent = 'Paid, but the relay did not confirm the credit - check your balance.';
        });
    });
  }

  function renderBuyTokens(shortfall) {
    if (shortfall <= 0 || (tokenPrice.bits <= 0 && tokenPrice.points <= 0)) {
      buyTokensEl.hidden = true;
      return;
    }
    buyTokensEl.hidden = false;
    buyTokensNeedEl.textContent = 'Need ' + shortfall.toLocaleString() + ' more token(s).';

    if (tokenPrice.bits > 0) {
      loadBitsProducts();
    } else {
      bitsProductsEl.innerHTML = '';
    }

    if (tokenPrice.points > 0 && tokenPrice.rewardName) {
      pointsInstructionsEl.textContent = 'Or redeem "' + tokenPrice.rewardName + '" on the Channel Points panel ('
        + tokenPrice.points.toLocaleString() + ' points per token).';
    } else {
      pointsInstructionsEl.textContent = '';
    }
  }

  // ---- Putting it together ----

  function updateSendState() {
    renderSummonState();
  }

  function renderSummonState() {
    var count = selectedIds().length;
    var total = selectedTotal();

    totalEl.textContent = count === 0
      ? ''
      : (total > 0 ? 'Selected: ' + total.toLocaleString() + ' tok' : 'Selected: free');

    // Mock/streamer testing: no balance involved, the control key alone authorizes a direct
    // dispatch, so none of the token UI below applies.
    if (isMock) {
      shareIdentityEl.hidden = true;
      buyTokensEl.hidden = true;

      if (controlKey) {
        balanceEl.textContent = '';
        sendEl.disabled = count === 0;
        sendEl.textContent = count === 0 ? 'Pick a pack' : (count === 1 ? 'Summon' : 'Summon ' + count + ' packs');
        return;
      }

      // No control key pasted in: this branch used to double as "preview what a real viewer
      // sees" - it can't anymore. A real viewer's Summon button now depends on
      // Twitch.ext.onAuthorized/bits, neither of which ever fires outside an actual Twitch
      // session, so there is nothing meaningful left to preview here without one.
      balanceEl.textContent = '';
      sendEl.disabled = true;
      sendEl.textContent = count === 0 ? 'Pick a pack' : 'Preview only';
      if (count > 0) {
        sendResultEl.textContent = 'Paste a Control Key above to test-summon, or open this in a real Twitch session (Local Test) to try the token flow.';
      }
      return;
    }

    if (!authToken) {
      // onAuthorized hasn't fired yet - too early to know anything about identity or balance.
      sendEl.disabled = true;
      sendEl.textContent = 'Loading…';
      return;
    }

    if (!viewerHasIdentity) {
      shareIdentityEl.hidden = false;
      packsEl.style.opacity = '0.5';
      packsEl.style.pointerEvents = 'none';
      balanceEl.textContent = '';
      buyTokensEl.hidden = true;
      sendEl.disabled = true;
      sendEl.textContent = 'Share identity to summon';
      return;
    }

    shareIdentityEl.hidden = true;
    packsEl.style.opacity = '';
    packsEl.style.pointerEvents = '';
    balanceEl.textContent = 'Balance: ' + balance.toLocaleString() + ' tok';

    if (count === 0) {
      buyTokensEl.hidden = true;
      sendEl.disabled = true;
      sendEl.textContent = 'Pick a pack';
      return;
    }

    var shortfall = total - balance;
    renderBuyTokens(shortfall);

    if (shortfall > 0) {
      sendEl.disabled = true;
      sendEl.textContent = 'Not enough tokens';
      return;
    }

    sendEl.disabled = false;
    sendEl.textContent = count === 1 ? 'Summon' : 'Summon ' + count + ' packs';
  }

  messageEl.addEventListener('input', function () {
    counterEl.textContent = messageEl.value.length + '/80';
  });

  sendEl.addEventListener('click', function () {
    var ids = selectedIds();
    if (ids.length === 0) return;

    // Trimmed and stripped of control characters here only so the UI shows what would be sent.
    // This is presentation, not enforcement: the relay re-validates the text (printable ASCII,
    // no quotes, 95 chars) before it can reach the game, and the overlay cuts it again at the
    // mailbox.
    var message = '';
    for (var i = 0; i < messageEl.value.length; i++) {
      var code = messageEl.value.charCodeAt(i);
      if (code >= 32 && code !== 127) message += messageEl.value.charAt(i);
    }
    message = message.trim();

    var command = { type: 'summon', packs: ids };
    if (message) command.message = message;

    if (controlKey) {
      // The local mock, testing as the streamer - unchanged: no token balance involved, this is
      // the control key authorizing a direct dispatch the way a real viewer never can.
      sendResultEl.textContent = 'Sending' + String.fromCharCode(8230);
      fetch(relayUrl + '/api/command/' + encodeURIComponent(controlKey), {
        method: 'POST',
        mode: 'no-cors',
        headers: { 'Content-Type': 'text/plain' },
        body: JSON.stringify(command)
      }).then(function () {
        sendResultEl.textContent = 'Sent: ' + JSON.stringify(command);
        messageEl.value = '';
        counterEl.textContent = '0/80';
      }).catch(function (err) {
        sendResultEl.textContent = 'Could not reach the relay: ' + err;
      });
      return;
    }

    if (!authToken || !viewerHasIdentity) return; // button is disabled in this state anyway

    command.authToken = authToken;
    sendResultEl.textContent = 'Summoning' + String.fromCharCode(8230);

    fetch(relayUrl + '/api/summon/' + encodeURIComponent(streamKey), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(command)
    })
      .then(function (res) { return res.json().then(function (data) { return { ok: res.ok, data: data }; }); })
      .then(function (result) {
        if (result.ok) {
          balance = result.data.balance;
          sendResultEl.textContent = 'Summoned! ' + balance.toLocaleString() + ' token(s) left.';
          messageEl.value = '';
          counterEl.textContent = '0/80';
          selected = {};
          applySelectionClasses();
        } else if (result.data && result.data.error === 'insufficient_balance') {
          balance = result.data.balance;
          sendResultEl.textContent = 'Not enough tokens - buy some more below.';
        } else {
          sendResultEl.textContent = 'Could not summon - try again in a moment.';
        }
        renderSummonState();
      })
      .catch(function (err) {
        sendResultEl.textContent = 'Could not reach the relay: ' + err;
      });
  });

  // Draw the empty state up front; the first successful poll replaces it with the streamer's
  // real packs.
  renderPacks([]);
  renderSummonState();

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
