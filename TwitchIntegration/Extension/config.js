  var saved = null;
  var saveBtn = document.getElementById('save');
  var statusEl = document.getElementById('status');
  var eventsubSection = document.getElementById('eventsubSection');
  var eventsubStatusEl = document.getElementById('eventsubStatus');
  var authorizeBtn = document.getElementById('authorizeBtn');
  var channelId = null;

  // Twitch.ext.configuration.set() only works once the postMessage handshake with the Twitch
  // client has completed - the button used to be clickable from page load regardless, so
  // clicking Save before onAuthorized fired called set() into a context it silently rejects.
  // It now starts disabled and stays that way until Twitch confirms authorization.
  //
  // This used to also hard-block unless a client-decoded JWT role read exactly "broadcaster" -
  // removed. That check was never verified against a real Twitch session, only synthetic JWTs
  // this code built itself, and it's redundant anyway: Twitch's own backend is what actually
  // enforces who may write the "broadcaster" segment, so a client-side guess can only ever be
  // wrong in a way that blocks someone who's genuinely allowed. If set() gets called by someone
  // who isn't authorized to write it, Twitch's server is where that gets rejected, not here.
  var isAuthorized = false;

  function updateSaveButton() {
    saveBtn.disabled = !(isMock || isAuthorized);
    saveBtn.textContent = saveBtn.disabled ? 'Loading…' : 'Save';
  }

  function applyContent(rawContent) {
    if (!rawContent) return;
    try {
      saved = JSON.parse(rawContent);
      document.getElementById('relayUrl').value = saved.relayUrl || '';
      document.getElementById('streamKey').value = saved.streamKey || '';
      document.getElementById('controlKey').value = saved.controlKey || '';
      document.getElementById('bitsPerToken').value = saved.bitsPerToken || '';
      document.getElementById('pointsPerToken').value = saved.pointsPerToken || '';
      document.getElementById('rewardName').value = saved.rewardName || '';
      document.getElementById('maxTokenBalance').value = saved.maxTokenBalance || '';
    } catch (e) {
      // Malformed/legacy content - leave the form blank rather than fail to load.
    }
  }

  // Local/mock testing (mock/index.html links here with ?mock=1): the real
  // twitch-ext.min.js loads fine even outside an actual Twitch iframe (it's just a public
  // script), so Twitch.ext.configuration existing is NOT a reliable signal - Twitch.ext.configuration.set()
  // would silently go nowhere outside Twitch's real postMessage bridge. Read/write
  // localStorage directly instead, under the same key video_overlay.html's mock fallback
  // reads from, whenever this explicit flag is set.
  var isMock = new URLSearchParams(location.search).get('mock') === '1';
  if (isMock) {
    document.getElementById('mockOnly').style.display = 'block';
    // There's no real broadcaster id or Twitch consent flow to test out here - the mock exists to
    // preview the token fields, not this part.
    eventsubSection.style.display = 'none';
    applyContent(localStorage.getItem('bg2ext_mock_config'));
    updateSaveButton();
  } else {
    Twitch.ext.onAuthorized(function (auth) {
      isAuthorized = true;
      channelId = auth.channelId;
      updateSaveButton();
      checkEventSubStatus();
    });
    // onChanged fires immediately with whatever is already stored, so this also covers the
    // initial load - no separate "read current config" call needed.
    Twitch.ext.configuration.onChanged(function () {
      var cfg = Twitch.ext.configuration.broadcaster;
      applyContent(cfg && cfg.content);
      checkEventSubStatus();
    });

    // Authorizing happens in a separate tab (Twitch's own consent screen can't live in this
    // iframe) - there's no callback into this page when it's done, so re-check whenever the
    // broadcaster comes back to this one.
    window.addEventListener('focus', checkEventSubStatus);
  }

  function relayUrlValue() {
    return document.getElementById('relayUrl').value.trim().replace(/\/+$/, '');
  }

  var eventsubCheckToken = 0;

  function checkEventSubStatus() {
    var relayUrl = relayUrlValue();
    if (!relayUrl || !channelId) {
      eventsubStatusEl.textContent = 'Enter your Relay Server URL above to check.';
      authorizeBtn.style.display = 'none';
      return;
    }

    var thisCheck = ++eventsubCheckToken;
    eventsubStatusEl.textContent = 'Checking authorization status…';
    authorizeBtn.style.display = 'none';

    fetch(relayUrl + '/api/eventsub-status/' + encodeURIComponent(channelId))
      .then(function (r) { return r.ok ? r.json() : Promise.reject(r.status); })
      .then(function (data) {
        if (thisCheck !== eventsubCheckToken) return; // a newer check has since started
        if (data.configured === false) {
          eventsubStatusEl.textContent = 'Channel Points is not set up on this relay deployment.';
          return;
        }
        // Deliveries turned away for a bad signature mean the relay and Twitch disagree about
        // the webhook secret - everything looks healthy from Twitch's side, and redemptions
        // simply stop crediting. Re-authorizing re-creates the subscriptions with the current
        // secret, so the advice is the same button either way.
        var rejected = data.rejectedDeliveries > 0
          ? ' (' + data.rejectedDeliveries + ' signed delivery/deliveries were rejected recently - if redemptions aren’t crediting, re-authorize.)'
          : '';

        if (data.authorized) {
          eventsubStatusEl.textContent = '✅ Channel Points is authorized and active.' + rejected;
          authorizeBtn.style.display = rejected ? '' : 'none';
        } else if (data.redemptionsCredit && !data.refundsClawBack) {
          // Set up before refunds were handled: redemptions still buy tokens, but points handed
          // back out of the request queue leave the tokens they bought in place.
          eventsubStatusEl.textContent = '⚠️ Authorized, but needs re-authorizing to pick up refunds - '
            + 'until you do, refunding a redemption won’t take its tokens back.' + rejected;
          authorizeBtn.style.display = '';
        } else {
          eventsubStatusEl.textContent = '⚠️ Not authorized yet - viewers’ Channel Points redemptions won’t be picked up until you do this.';
          authorizeBtn.style.display = '';
        }
      })
      .catch(function () {
        if (thisCheck !== eventsubCheckToken) return;
        eventsubStatusEl.textContent = 'Could not check status - verify your Relay Server URL is correct.';
      });
  }

  authorizeBtn.addEventListener('click', function () {
    var relayUrl = relayUrlValue();
    if (!relayUrl) return;
    window.open(relayUrl + '/oauth/authorize', '_blank');
    eventsubStatusEl.textContent = 'After approving in the new tab, come back here - this will refresh automatically.';
  });

  document.getElementById('relayUrl').addEventListener('change', checkEventSubStatus);

  saveBtn.addEventListener('click', function () {
    var relayUrl = document.getElementById('relayUrl').value.trim().replace(/\/+$/, '');
    var streamKey = document.getElementById('streamKey').value.trim();

    if (!relayUrl || !streamKey) {
      statusEl.style.color = '#eb0400';
      statusEl.textContent = 'Both fields are required.';
      return;
    }

    // A blank or garbage field means "this currency is off", same as an explicit 0 - not an
    // error, since a streamer setting up only one of the two currencies is the normal case.
    function nonNegativeIntOrZero(raw) {
      var n = parseInt(raw, 10);
      return Number.isFinite(n) && n > 0 ? n : 0;
    }

    var bitsPerToken = nonNegativeIntOrZero(document.getElementById('bitsPerToken').value);
    var pointsPerToken = nonNegativeIntOrZero(document.getElementById('pointsPerToken').value);
    var rewardName = document.getElementById('rewardName').value.trim();
    var maxTokenBalance = nonNegativeIntOrZero(document.getElementById('maxTokenBalance').value);

    if (pointsPerToken > 0 && !rewardName) {
      statusEl.style.color = '#eb0400';
      statusEl.textContent = 'Set a Token Reward Name to match, or Channel Points per token back to 0.';
      return;
    }

    // Built separately per branch rather than filtered out of one shared object: the control
    // key going into the Twitch configuration would hand every viewer the ability to spawn
    // creatures in the streamer's game, so there must be no code path where it can end up there.
    if (isMock) {
      localStorage.setItem('bg2ext_mock_config', JSON.stringify({
        relayUrl: relayUrl,
        streamKey: streamKey,
        controlKey: document.getElementById('controlKey').value.trim(),
        bitsPerToken: bitsPerToken,
        pointsPerToken: pointsPerToken,
        rewardName: rewardName,
        maxTokenBalance: maxTokenBalance
      }));
    } else {
      // Belt and braces: the button is disabled otherwise, but a click queued right before
      // that took effect shouldn't be able to slip a doomed call through.
      if (!isAuthorized) return;
      try {
        Twitch.ext.configuration.set('broadcaster', '1', JSON.stringify({
          relayUrl: relayUrl,
          streamKey: streamKey,
          bitsPerToken: bitsPerToken,
          pointsPerToken: pointsPerToken,
          rewardName: rewardName,
          maxTokenBalance: maxTokenBalance
        }));
      } catch (e) {
        statusEl.style.color = '#eb0400';
        statusEl.textContent = 'Could not save: ' + e.message;
        return;
      }
    }
    statusEl.style.color = '#00c78c';
    statusEl.textContent = 'Saved.';
  });
