// Tests the viewer-side half of a Bits purchase: what happens to a receipt between Twitch taking
// the viewer's Bits and the relay crediting the tokens.
//
// That gap is the only place in the whole flow where a purchase can be lost outright. Twitch
// hands the receipt to the browser once, it exists nowhere else, and posting it is a plain
// fetch() that can fail for all the ordinary reasons. So the rule the tests below enforce is:
// a receipt is written down before anything is attempted, and only dropped once the relay has
// actually said it is settled.
//
// Run with:  node TwitchIntegration/Extension/tests/pending-receipts.test.js
//
// video_overlay.js is loaded into a stub browser rather than a real one - no DOM, no Twitch, no
// network - because none of those are what is under test. What is under test is the bookkeeping.

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const assert = require('assert');

const overlaySource = fs.readFileSync(
  path.join(__dirname, '..', 'video_overlay.js'), 'utf8');

const PENDING_KEY = 'bg2ext_pending_receipts';
const RELAY = 'https://relay.example';
const STREAM_KEY = 'streamkey0001';

// ---- The stub browser ----

function fakeElement() {
  const classes = new Set();
  return {
    textContent: '',
    innerHTML: '',
    value: '',
    hidden: false,
    disabled: false,
    style: {},
    handlers: {},
    classList: {
      add: (c) => classes.add(c),
      remove: (c) => classes.delete(c),
      contains: (c) => classes.has(c),
      toggle: (c) => (classes.has(c) ? (classes.delete(c), false) : (classes.add(c), true))
    },
    addEventListener(event, handler) { this.handlers[event] = handler; },
    appendChild() {}
  };
}

function createHarness(options) {
  options = options || {};

  const elements = {};
  const storage = new Map(options.storage || []);
  const requests = [];
  const twitchCallbacks = {};

  // Answers /api/character and /api/balance plainly; /api/bits-purchase is what each test steers.
  const bitsResponder = options.bitsResponder || (() => ({ ok: true, body: { credited: 5, duplicate: false, balance: 5 } }));

  const sandbox = {
    console,
    JSON, Math, Number, String, Object, Array, Date, Error, isNaN, parseInt, parseFloat,
    encodeURIComponent, URLSearchParams, Promise, setTimeout, clearTimeout,
    setInterval: () => 0,
    clearInterval: () => {},
    location: { search: '' }, // not mock mode: the real viewer path is what we are testing
    localStorage: {
      getItem: (k) => (storage.has(k) ? storage.get(k) : null),
      setItem: (k, v) => storage.set(k, String(v)),
      removeItem: (k) => storage.delete(k)
    },
    document: {
      getElementById: (id) => (elements[id] = elements[id] || fakeElement()),
      createElement: () => fakeElement()
    },
    fetch: (url, init) => {
      requests.push({ url, init });
      if (url.indexOf('/api/bits-purchase/') !== -1) {
        const outcome = bitsResponder(JSON.parse(init.body), requests.length);
        if (outcome.reject) return Promise.reject(new Error('network down'));
        return Promise.resolve({
          ok: outcome.ok,
          status: outcome.ok ? 200 : (outcome.status || 500),
          json: () => Promise.resolve(outcome.body)
        });
      }
      if (url.indexOf('/api/balance/') !== -1) {
        return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ balance: options.serverBalance || 0 }) });
      }
      // /api/character - the party poll, irrelevant here
      return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) });
    }
  };

  sandbox.window = sandbox;
  sandbox.Twitch = {
    ext: {
      configuration: {
        broadcaster: { content: JSON.stringify({ relayUrl: RELAY, streamKey: STREAM_KEY, bitsPerToken: 100 }) },
        onChanged: (cb) => { twitchCallbacks.configuration = cb; }
      },
      onAuthorized: (cb) => { twitchCallbacks.authorized = cb; },
      actions: { requestIdShare: () => {} },
      bits: {
        getProducts: () => Promise.resolve([]),
        useBits: () => {},
        onTransactionComplete: (cb) => { twitchCallbacks.transactionComplete = cb; },
        onTransactionCancelled: (cb) => { twitchCallbacks.transactionCancelled = cb; }
      }
    }
  };

  vm.createContext(sandbox);
  vm.runInContext(overlaySource, sandbox, { filename: 'video_overlay.js' });

  return {
    sandbox,
    elements,
    requests,
    twitchCallbacks,
    pending: () => JSON.parse(storage.get(PENDING_KEY) || '[]'),
    storage,
    // Lets the promise chains inside the overlay settle before assertions run.
    settle: () => new Promise((resolve) => setTimeout(resolve, 0)).then(
      () => new Promise((resolve) => setTimeout(resolve, 0)))
  };
}

function configure(h) {
  h.twitchCallbacks.configuration();
}

function authorize(h, opts) {
  opts = opts || {};
  h.twitchCallbacks.authorized({
    token: opts.token || 'fake.jwt.token',
    userId: 'userId' in opts ? opts.userId : '99001'
  });
}

function purchase(h, receipt) {
  h.twitchCallbacks.transactionComplete({ transactionReceipt: receipt });
}

function bitsRequests(h) {
  return h.requests.filter((r) => r.url.indexOf('/api/bits-purchase/') !== -1);
}

// ---- Tests ----

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('a completed purchase is written down before it is posted', async () => {
  let seenPendingAtPostTime = null;
  const h = createHarness({
    bitsResponder: () => {
      seenPendingAtPostTime = h.pending();
      return { ok: true, body: { credited: 5, duplicate: false, balance: 5 } };
    }
  });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');

  // Already stored by the time the request goes out - so a crash mid-flight still leaves it.
  assert.deepStrictEqual(seenPendingAtPostTime, ['receipt-A']);
  await h.settle();
});

test('a confirmed credit clears the receipt and updates the balance', async () => {
  const h = createHarness({
    bitsResponder: () => ({ ok: true, body: { credited: 5, duplicate: false, balance: 5 } })
  });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();

  assert.deepStrictEqual(h.pending(), []);
  assert.strictEqual(h.elements.balance.textContent, 'Balance: 5 tok');
  assert.match(h.elements.sendResult.textContent, /Bought 5 token/);
});

test('a receipt the relay never confirmed is kept for later', async () => {
  const h = createHarness({ bitsResponder: () => ({ reject: true }) });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();

  assert.deepStrictEqual(h.pending(), ['receipt-A'], 'the purchase must not be forgotten');
  assert.match(h.elements.sendResult.textContent, /retry/i);
});

test('a rejected response keeps the receipt rather than swallowing it', async () => {
  const h = createHarness({ bitsResponder: () => ({ ok: false, status: 503, body: {} }) });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();

  assert.deepStrictEqual(h.pending(), ['receipt-A']);
});

test('a receipt the relay says it already credited is settled, not retried forever', async () => {
  const h = createHarness({
    bitsResponder: () => ({ ok: true, body: { credited: 0, duplicate: true, balance: 12 } })
  });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();

  assert.deepStrictEqual(h.pending(), [], 'a duplicate is as settled as a fresh credit');
  assert.strictEqual(h.elements.balance.textContent, 'Balance: 12 tok');
});

test('a purchase left over from a previous session is retried once identity arrives', async () => {
  const h = createHarness({
    storage: [[PENDING_KEY, JSON.stringify(['receipt-from-yesterday'])]],
    bitsResponder: () => ({ ok: true, body: { credited: 3, duplicate: false, balance: 3 } })
  });
  configure(h);
  authorize(h); // no purchase this session at all
  await h.settle();

  const posted = bitsRequests(h);
  assert.strictEqual(posted.length, 1);
  assert.deepStrictEqual(JSON.parse(posted[0].init.body).receipts, ['receipt-from-yesterday']);
  assert.deepStrictEqual(h.pending(), []);
});

test('a purchase that failed is retried when the panel is opened again', async () => {
  let failFirst = true;
  const h = createHarness({
    bitsResponder: () => {
      if (failFirst) { failFirst = false; return { reject: true }; }
      return { ok: true, body: { credited: 5, duplicate: false, balance: 5 } };
    }
  });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();
  assert.deepStrictEqual(h.pending(), ['receipt-A']);

  h.elements.icon.handlers.click(); // open the panel
  await h.settle();

  assert.deepStrictEqual(h.pending(), [], 'reopening the panel should have settled it');
  assert.strictEqual(bitsRequests(h).length, 2);
});

test('every unsettled receipt goes up together, not just the newest', async () => {
  const h = createHarness({
    storage: [[PENDING_KEY, JSON.stringify(['receipt-old'])]],
    bitsResponder: () => ({ reject: true })
  });
  configure(h);
  authorize(h);
  await h.settle();
  purchase(h, 'receipt-new');
  await h.settle();

  const lastBody = JSON.parse(bitsRequests(h).pop().init.body);
  assert.deepStrictEqual(lastBody.receipts, ['receipt-old', 'receipt-new']);
  assert.deepStrictEqual(h.pending(), ['receipt-old', 'receipt-new']);
});

test('the identity token is sent with the receipts, to bind them to this channel', async () => {
  const h = createHarness({
    bitsResponder: () => ({ ok: true, body: { credited: 5, duplicate: false, balance: 5 } })
  });
  configure(h);
  authorize(h, { token: 'the.viewer.token' });
  purchase(h, 'receipt-A');
  await h.settle();

  const body = JSON.parse(bitsRequests(h)[0].init.body);
  assert.strictEqual(body.authToken, 'the.viewer.token');
});

test('a purchase made before identity arrives is still kept and sent later', async () => {
  const h = createHarness({
    bitsResponder: () => ({ ok: true, body: { credited: 5, duplicate: false, balance: 5 } })
  });
  configure(h);
  purchase(h, 'receipt-early'); // onAuthorized has not fired yet - no token to send with
  assert.deepStrictEqual(h.pending(), ['receipt-early']);
  assert.strictEqual(bitsRequests(h).length, 0);

  authorize(h);
  await h.settle();

  assert.strictEqual(bitsRequests(h).length, 1);
  assert.deepStrictEqual(h.pending(), []);
});

test('a duplicate receipt is not stored twice', async () => {
  const h = createHarness({ bitsResponder: () => ({ reject: true }) });
  configure(h);
  authorize(h);
  purchase(h, 'receipt-A');
  await h.settle();
  purchase(h, 'receipt-A');
  await h.settle();

  assert.deepStrictEqual(h.pending(), ['receipt-A']);
});

test('a cancelled purchase says so and stores nothing', async () => {
  const h = createHarness();
  configure(h);
  authorize(h);
  h.twitchCallbacks.transactionCancelled();

  assert.deepStrictEqual(h.pending(), []);
  assert.match(h.elements.sendResult.textContent, /cancel/i);
});

// ---- Runner ----

(async () => {
  let failed = 0;
  for (const { name, fn } of tests) {
    try {
      await fn();
      console.log('  ok    ' + name);
    } catch (error) {
      failed++;
      console.log('  FAIL  ' + name);
      console.log('        ' + (error && error.message));
    }
  }
  console.log('');
  console.log(failed === 0
    ? tests.length + ' passed'
    : failed + ' of ' + tests.length + ' failed');
  process.exit(failed === 0 ? 0 : 1);
})();
