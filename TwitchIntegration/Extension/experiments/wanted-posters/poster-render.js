  function renderParty(data) {
    var party = (data && data.party) || [];
    if (party.length === 0) {
      contentEl.innerHTML = '<div id="empty">No party data yet.</div>';
      return;
    }
    var html = '';
    for (var i = 0; i < party.length; i++) {
      var m = party[i];
      var meta = [m.race, m['class']].filter(Boolean).join(' \u00b7 ');
      html += '<div class="member">'
        + '<div class="wanted">Wanted</div>'
        + '<div class="name">' + escapeHtml(m.name || '?') + '</div>'
        + '<div class="meta">' + escapeHtml(meta || 'of unknown origin') + '</div>'
        + '<div class="doa">Dead or Alive</div>'
        + '<div class="bounty">'
        +   '<span>Reward <span class="reward">' + bounty(m.currentHp) + '</span></span>'
        +   '<span class="hp">HP ' + (m.currentHp != null ? m.currentHp : '?') + '</span>'
        + '</div>'
        + '</div>';
    }
    contentEl.innerHTML = html;
  }

  // The price on someone's head, scaled off how much of them is left to collect. Pure flavour -
  // nothing reads this back - so it only has to look like a sum a frontier sheriff would post:
  // rounded to something printable, and never zero, because a corpse is still worth hauling in.
  function bounty(hp) {
    if (hp == null || isNaN(Number(hp))) return '$???';
    var amount = Math.max(50, Math.round(Number(hp) * 25 / 50) * 50);
    return '$' + amount.toLocaleString();
  }
