// A stand-in backend for recording the AutoPay billing screen. NOT Razorpay and not the real API: it answers only what the Billing page
// asks, from a small state machine, and is moved between states by /__stage so a recording can show each one on cue.
// Standard library only.   node mock-server.js [port]
const http = require('http');
const PORT = Number(process.argv[2] || 7099);

const iso = (d) => new Date(d).toISOString();
const now = Date.now();
const DAY = 86400000;
const plans = [
  plan('trial', 'Trial', 0, 0, 0, 0, 50, 0),
  plan('premium', 'Premium', 2407, 433.26, 2840.26, 29, 0, 100),
];
function plan(code, name, price, tax, total, usd, starter, monthly) {
  return {
    id: code, code, name, maxUsers: code === 'trial' ? 1 : 10, maxMessagesPerMonth: 10000, maxCampaigns: code === 'trial' ? 1 : 25,
    maxKnowledgeBaseArticles: code === 'trial' ? 20 : 500, maxLeadDiscoveryBatchSize: code === 'trial' ? 25 : 100, priceMonthlyCents: usd * 100,
    currencyCode: 'INR', currencySymbol: '₹', localPriceAmount: price, starterCredits: starter, monthlyCredits: monthly, features: [],
    taxLines: tax ? [{ name: 'CGST', ratePercent: 9, amount: 216.63 }, { name: 'SGST', ratePercent: 9, amount: 216.63 }] : [], taxLocal: tax, totalLocal: total,
  };
}

const RZP = { keys: false, webhook: false, autoPay: false };
const rzp = () => ({
  keyId: RZP.keys ? 'rzp_test_1DP5mmOlF5G5ag' : '', mode: 'test', hasKeySecret: RZP.keys, keySecretHint: RZP.keys ? '••••1234' : null, hasWebhookSecret: RZP.webhook,
  webhookSecretHint: RZP.webhook ? '••••9876' : null, isConfigured: RZP.keys, webhookPath: '/api/v1/webhooks/razorpay',
  autoPayEnabled: RZP.autoPay, autoPayReady: RZP.autoPay && RZP.keys && RZP.webhook,
});

const S = { stage: 'none', paid: 0, failed: false, cancelled: false, requests: [] };
const periodStart = iso(now), periodEnd = iso(now + 30 * DAY);

function mandate() {
  const m = {
    id: 'm-1', subscriptionId: 'sub_DEMO000001', planId: 'premium', planName: 'Premium', gatewayStatus: S.stage, amount: 2840.26, currencyCode: 'INR',
    billingFrequency: 'Monthly', currentPeriodStartUtc: null, currentPeriodEndUtc: null, nextRenewalUtc: null, autoRenew: false, cancelAtCycleEnd: false,
    cancelledAtUtc: null, paymentMethod: null, authorizationUrl: null, mode: 'test', createdAt: iso(now),
  };
  const running = () => Object.assign(m, { currentPeriodStartUtc: periodStart, currentPeriodEndUtc: periodEnd, paymentMethod: 'upi' });
  switch (S.stage) {
    case 'pending': m.status = 'PendingAuthorization'; m.authorizationUrl = 'http://localhost:' + PORT + '/mock-razorpay'; break;
    case 'payment_pending': m.status = 'PaymentPending'; m.paymentMethod = 'upi'; break;
    case 'active': running(); m.status = 'Active'; m.autoRenew = true; m.nextRenewalUtc = periodEnd; break;
    case 'past_due': running(); m.status = 'PastDue'; m.autoRenew = true; m.nextRenewalUtc = periodEnd; break;
    case 'cancel_scheduled': running(); m.status = 'CancellationScheduled'; m.cancelAtCycleEnd = true; break;
    default: return null;
  }
  return m;
}

function payments() {
  const rows = [];
  if (S.failed) rows.push(pay('pay_DEMO02', 'Failed', 0.5, 'Insufficient balance in the UPI account'));
  if (S.paid) rows.push(pay('pay_DEMO01', 'Succeeded', 0, null));
  return rows;
}
function pay(id, status, daysAgo, reason) {
  return {
    id, planName: 'Premium', amountCents: 2900, currencyCode: 'INR', currencySymbol: '₹', localAmount: 2407, provider: 'Razorpay', paidAtUtc: iso(now - daysAgo * DAY),
    kind: 'Subscription', countryCode: 'IN', taxLocal: 433.26, totalLocal: 2840.26, taxLines: [{ name: 'CGST', ratePercent: 9, amount: 216.63 }, { name: 'SGST', ratePercent: 9, amount: 216.63 }],
    amountInr: 2840.26, status, paymentMethod: 'upi', failureReason: reason, transactionId: id, invoiceId: 'inv_' + id.slice(4),
  };
}

const onPremium = () => S.paid > 0;
const subscription = () => ({
  planId: onPremium() ? 'premium' : 'trial', planName: onPremium() ? 'Premium' : 'Trial', status: 'Active',
  currentPeriodStartUtc: onPremium() ? periodStart : null, currentPeriodEndUtc: onPremium() ? periodEnd : null,
});

const json = (res, code, body) => {
  res.writeHead(code, { 'content-type': 'application/json' });
  res.end(body === undefined ? '' : JSON.stringify(body));
};

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  const p = url.pathname.replace(/^\/api\/v1/, '');
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    S.requests.push(req.method + ' ' + p);
    if (p === '/__stage') {
      const to = url.searchParams.get('to');
      S.stage = to;
      if (to === 'none') { S.paid = 0; S.failed = false; }
      if (to === 'active') S.paid = 1;
      if (to === 'past_due') S.failed = true;
      return json(res, 200, { stage: S.stage });
    }
    if (p === '/__requests') return json(res, 200, S.requests);
    if (p === '/mock-razorpay') {
      res.writeHead(200, { 'content-type': 'text/html' });
      return res.end('<h1>Stand-in for the Razorpay authorisation page</h1><p>Not Razorpay. Used only for the demo recording.</p>');
    }

    // ---- what the Billing page asks
    if (p === '/billing/plans') return json(res, 200, plans);
    if (p === '/billing/subscription') return json(res, 200, subscription());
    if (p === '/billing/payments') return json(res, 200, payments());
    if (p === '/billing/capabilities') return json(res, 200, { refundRequestsEnabled: false });
    if (p === '/billing/refund-requests') return json(res, 200, []);
    if (p === '/billing/quota') return json(res, 200, [{ quotaType: 6, balance: 50, capacity: 50, grants: [{ id: 'g', origin: 3, unitsGranted: 50, unitsRemaining: 50, expiresAtUtc: '' }] }]);
    if (p === '/billing/autopay/availability') return json(res, 200, { available: true, reason: null, mode: 'test' });
    if (p === '/billing/autopay/subscription') {
      const m = mandate();
      return m ? json(res, 200, m) : json(res, 204);
    }
    if (p === '/billing/autopay/checkout' && req.method === 'POST') {
      S.stage = 'pending';
      const m = mandate();
      return json(res, 200, { id: m.id, subscriptionId: m.subscriptionId, status: 'PendingAuthorization', authorizationUrl: m.authorizationUrl, keyId: 'rzp_test_demo', mode: 'test', amountMinor: 284026, currency: 'INR', planName: 'Premium' });
    }
    if (/^\/billing\/autopay\/subscriptions\/[^/]+\/refresh$/.test(p)) return json(res, 200, mandate());
    if (/^\/billing\/autopay\/subscriptions\/[^/]+$/.test(p)) return json(res, 200, mandate());
    if (/^\/billing\/autopay\/subscriptions\/[^/]+\/cancel$/.test(p)) {
      const atEnd = JSON.parse(body || '{}').cancelAtCycleEnd !== false;
      S.stage = atEnd ? 'cancel_scheduled' : 'cancelled';
      return json(res, 200, mandate() || Object.assign(Object.assign({}, { id: 'm-1' }), { status: 'Cancelled' }));
    }
    if (/^\/billing\/autopay\/subscriptions\/[^/]+\/invoices$/.test(p)) {
      return json(res, 200, [{
        invoiceId: 'inv_DEMO01', invoiceNumber: null, status: 'paid', amount: 2840.26, amountPaid: 2840.26, currencyCode: 'INR', issuedAtUtc: periodStart, paidAtUtc: periodStart,
        periodStartUtc: periodStart, periodEndUtc: periodEnd, transactionId: 'pay_DEMO01', url: 'http://localhost:' + PORT + '/mock-razorpay',
      }]);
    }

    // ---- the platform Razorpay page (only to look at the AutoPay switch)
    if (p === '/platform/razorpay/settings') return json(res, 200, rzp());
    if (p === '/platform/razorpay/orders' || p === '/platform/razorpay/webhook-events') return json(res, 200, []);
    if (p === '/platform/razorpay/autopay' && req.method === 'PUT') {
      const enabled = JSON.parse(body || '{}').enabled === true;
      if (enabled && !RZP.keys) return json(res, 409, { title: 'Save the Razorpay key id and key secret first.' });
      RZP.autoPay = enabled;
      return json(res, 200, rzp());
    }
    if (p === '/__rzp') { RZP.keys = url.searchParams.get('keys') === '1'; RZP.webhook = url.searchParams.get('webhook') === '1'; RZP.autoPay = false; return json(res, 200, rzp()); }

    // ---- the page shell
    if (p === '/billing/entitlements') return json(res, 200, { planCode: onPremium() ? 'premium' : 'trial', planName: onPremium() ? 'Premium' : 'Trial', features: [], balance: onPremium() ? 150 : 50, costs: [] });
    if (p === '/billing/notifications') return json(res, 200, []);
    if (p === '/onboarding') return json(res, 200, { isCompleted: true, progressPercent: 100, currentStepKey: null, completedAt: iso(now - 30 * DAY), steps: [] });
    if (p === '/tenant-profile' || p === '/account/profile' || p === '/content-studio/status') return json(res, 200, {});
    if (p === '/announcements/active') return json(res, 200, []);
    if (/^\/(auth|hubs)/.test(p)) return json(res, 404, { title: 'not mocked' });
    if (req.method === 'GET') return json(res, 200, []);
    return json(res, 200, {});
  });
}).listen(PORT, () => console.log('mock backend on ' + PORT));
