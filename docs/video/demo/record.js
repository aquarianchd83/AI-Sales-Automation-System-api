// Records the real Angular Billing screen, talking to demo/mock-server.js, by driving headless Chrome over the DevTools protocol and
// saving its screencast frames. Needs the `ws` package from the Angular repo's node_modules (already installed there).
//
//   node record.js <angularRepoDir> <outDir> <hold-seconds-json>
//
// The dev server must already be running on :4300 (ng serve with a proxy to the mock) and the mock on :7099.
// Writes <outDir>/frames/*.jpg, <outDir>/frames.ffconcat and <outDir>/marks.json (the time each scene begins, in seconds).
const fs = require('fs');
const path = require('path');
const http = require('http');
const { spawn } = require('child_process');

const [angularDir, outDir, holdsArg] = process.argv.slice(2);
const WebSocket = require(path.join(angularDir, 'node_modules', 'ws'));
const holds = JSON.parse(holdsArg || '{}'); // scene name -> seconds the scene must last (its narration)
const APP = 'http://localhost:4300';
const MOCK = 'http://localhost:7099';
const CHROME = [process.env['ProgramFiles'] + '\\Google\\Chrome\\Application\\chrome.exe', process.env['ProgramFiles(x86)'] + '\\Microsoft\\Edge\\Application\\msedge.exe'].find(fs.existsSync);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const get = (url) => new Promise((ok, no) => http.get(url, (r) => { let b = ''; r.on('data', (c) => (b += c)); r.on('end', () => ok(b)); }).on('error', no));

async function main() {
  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(path.join(outDir, 'frames'), { recursive: true });
  const profile = fs.mkdtempSync(path.join(require('os').tmpdir(), 'autopay-chrome-'));
  const chrome = spawn(CHROME, ['--headless=new', '--disable-gpu', '--remote-debugging-port=9333', '--user-data-dir=' + profile, '--window-size=1600,900',
    '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', 'about:blank'], { stdio: 'ignore' });
  try {
    let targets;
    for (let i = 0; i < 40; i++) { try { targets = JSON.parse(await get('http://127.0.0.1:9333/json')); break; } catch (e) { await sleep(250); } }
    const page = targets.find((t) => t.type === 'page');
    const ws = new WebSocket(page.webSocketDebuggerUrl, { perMessageDeflate: false });
    await new Promise((r) => ws.on('open', r));

    let id = 0; const pending = new Map(); const frames = [];
    ws.on('message', (raw) => {
      const m = JSON.parse(raw);
      if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }
      if (m.method === 'Page.screencastFrame') {
        const n = frames.length;
        fs.writeFileSync(path.join(outDir, 'frames', String(n).padStart(6, '0') + '.jpg'), Buffer.from(m.params.data, 'base64'));
        frames.push(m.params.metadata.timestamp);
        send('Page.screencastFrameAck', { sessionId: m.params.sessionId });
      }
    });
    const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
    const ev = async (expr) => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 400)); return r.result.result.value; };

    await send('Page.enable'); await send('Runtime.enable');
    await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 900, deviceScaleFactor: 1, mobile: false });

    // A signed-in tenant Admin. The app only reads this token's claims; nothing here is verified by the mock.
    const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
    const claims = {
      'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': 'u-1', 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress': 'admin@acme.example',
      'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name': 'Asha Admin', 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': 'Admin', exp: 4102444800, tenant_id: 't-1',
    };
    const token = b64({ alg: 'none', typ: 'JWT' }) + '.' + b64(claims) + '.x';
    const user = { id: 'u-1', fullName: 'Asha Admin', email: 'admin@acme.example', phoneNumber: null, isActive: true, roles: ['Admin'], createdAt: '', lastLoginAt: null, emailConfirmed: true, phoneNumberConfirmed: true };

    await get(MOCK + '/api/v1/__stage?to=none');
    await send('Page.navigate', { url: APP + '/login' });
    await sleep(2500);
    await ev(`localStorage.setItem('wsa.accessToken', ${JSON.stringify(token)}); localStorage.setItem('wsa.refreshToken','r'); localStorage.setItem('wsa.user', ${JSON.stringify(JSON.stringify(user))}); true`);

    // The label that stays on screen for the whole recording, so nobody mistakes it for the real thing.
    const banner = `(()=>{const d=document.createElement('div');d.id='demo-banner';d.textContent='DEMO RECORDING - the real Angular screen talking to a stand-in backend. No real Razorpay, no real payment.';
      d.style.cssText='position:fixed;left:0;right:0;bottom:0;z-index:99999;background:#b45309;color:#fff;font:600 15px Segoe UI,Arial;text-align:center;padding:7px';document.body.appendChild(d)})()`;
    const ring = (sel) => `(()=>{const e=${sel};if(!e)return false;e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();const d=document.createElement('div');
      d.style.cssText='position:fixed;z-index:99998;pointer-events:none;border:4px solid #ef4444;border-radius:10px;left:'+(r.left-6)+'px;top:'+(r.top-6)+'px;width:'+(r.width+12)+'px;height:'+(r.height+12)+'px;transition:opacity .4s';
      document.body.appendChild(d);setTimeout(()=>d.style.opacity=0,900);setTimeout(()=>d.remove(),1400);return true})()`;
    const byText = (scope, text) => `Array.from(document.querySelectorAll('${scope}')).find(b=>b.textContent.replace(/\\s+/g,' ').trim().includes(${JSON.stringify(text)}))`;
    const click = async (scope, text) => {
      for (let i = 0; i < 40; i++) { if (await ev(ring(byText(scope, text)))) break; await sleep(250); }
      await sleep(1100);
      if (!(await ev(`(()=>{const e=${byText(scope, text)};if(!e)return false;e.click();return true})()`))) throw new Error('no button: ' + text);
    };
    const waitFor = async (js, label) => { for (let i = 0; i < 80; i++) { if (await ev(js)) return; await sleep(250); } throw new Error('timeout: ' + label); };
    const stage = (to) => get(MOCK + '/api/v1/__stage?to=' + to);
    const hasText = (t) => `document.body.innerText.includes(${JSON.stringify(t)})`;

    await send('Page.startScreencast', { format: 'jpeg', quality: 85, everyNthFrame: 1 });
    const t0 = Date.now();
    const marks = {};
    const mark = (name) => { marks[name] = (Date.now() - t0) / 1000; };
    // Holds a scene until its narration has had time to finish, counted from the scene's mark.
    const hold = async (name) => { const need = (holds[name] || 3) * 1000 - (Date.now() - t0 - marks[name] * 1000); if (need > 0) await sleep(need); };

    await send('Page.navigate', { url: APP + '/billing' });
    await ev(banner).catch(() => {});
    await waitFor(`document.querySelectorAll('.plan-card').length>0`, 'plans');
    await ev(banner);
    mark('plans'); await hold('plans');

    mark('subscribe');
    await click('.plan-card button', 'Subscribe now'); await sleep(1500);
    await waitFor(`!!document.querySelector('mat-dialog-container')`, 'dialog');
    await hold('subscribe');
    await click('mat-dialog-container button', 'Continue to Razorpay');
    await waitFor(hasText('Pending authorization'), 'pending card');

    mark('pending'); await hold('pending');

    mark('confirmed');
    await stage('payment_pending');
    await waitFor(hasText('Payment pending'), 'payment pending (poll)');
    await sleep(2500);
    await stage('active');
    await waitFor(`!!document.querySelector('.mandate-card') && document.querySelector('.mandate-status').textContent.trim()==='Active'`, 'active (poll)');
    await hold('confirmed');

    mark('history'); await sleep(500);
    await ev(`document.querySelector('.payment-history-card').scrollIntoView({block:'center'});true`);
    await hold('history');

    mark('invoices');
    await ev(`window.scrollTo(0,0);true`); await sleep(600);
    await click('.mandate-card button', 'Invoices'); await sleep(900);
    await hold('invoices');

    mark('failure');
    await stage('past_due');
    // A payment failed at Razorpay; the tenant opens the page again and the server reports it.
    await ev(`location.reload()`); await sleep(3000);
    await waitFor(`!!document.querySelector('.mandate-notice--problem')`, 'past due notice');
    await ev(banner);
    await hold('failure');

    mark('cancel');
    // The tenant's balance was topped up and Razorpay's retry went through: the mandate is running again.
    await stage('active');
    await ev(`location.reload()`); await sleep(3000); await waitFor(`!!document.querySelector('.mandate-card')`, 'mandate after reload'); await ev(banner);
    await waitFor(`document.querySelector('.mandate-status').textContent.trim()==='Active'`, 'active again');
    await click('.mandate-card button', 'Cancel automatic payment'); await sleep(1200);
    await waitFor(`!!document.querySelector('mat-dialog-container')`, 'cancel dialog');
    await sleep(2200);
    await click('mat-dialog-container button', 'Cancel automatic payment');
    await waitFor(hasText('Cancellation scheduled'), 'cancellation scheduled');
    await hold('cancel');
    mark('end');

    await send('Page.stopScreencast');
    fs.writeFileSync(path.join(outDir, 'marks.json'), JSON.stringify(marks, null, 1));
    // Frames arrive only when the picture changes, so each lasts until the next one.
    const base = frames[0]; let list = 'ffconcat version 1.0\n';
    for (let i = 0; i < frames.length; i++) {
      const dur = i + 1 < frames.length ? frames[i + 1] - frames[i] : 0.5;
      list += `file frames/${String(i).padStart(6, '0')}.jpg\nduration ${Math.max(dur, 0.001).toFixed(3)}\n`;
    }
    list += `file frames/${String(frames.length - 1).padStart(6, '0')}.jpg\n`;
    fs.writeFileSync(path.join(outDir, 'frames.ffconcat'), list);
    fs.writeFileSync(path.join(outDir, 'meta.json'), JSON.stringify({ firstFrameEpoch: base, frames: frames.length, t0Epoch: t0 / 1000 }));
    // Marks are relative to when the screencast started; frame timestamps are absolute. Convert so the two agree.
    console.log('frames', frames.length, 'marks', JSON.stringify(marks));
    ws.close();
  } finally {
    chrome.kill();
    setTimeout(() => { try { fs.rmSync(profile, { recursive: true, force: true }); } catch (e) {} }, 1500);
  }
}
main().catch((e) => { console.error(e); process.exit(1); });
