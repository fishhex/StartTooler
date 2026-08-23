#!/usr/bin/env node
/**
 * StartTooler LAN Upload Server — Routes smoke test (v0.14)
 *
 * 跑全部路由(happy path + 负面用例)。
 *
 * 用法:
 *   1. 启动 StartTooler 的 UploadServer 模式
 *   2. 编辑下方 CONFIG 或用环境变量覆盖
 *   3. node upload-server.test.js
 *
 * 环境变量(可选,优先于 CONFIG):
 *   HOST, PORT, SECRET, PROJECT, SAMPLE_FILE
 */

'use strict';

const fs = require('fs');
const path = require('path');

const CONFIG = {
  host: process.env.HOST || '127.0.0.1',
  port: parseInt(process.env.PORT || '8080', 10),
  secret: process.env.SECRET || 'replace_me_with_32_char_hex',
  project: process.env.PROJECT || 'my-project',
  // 用来做 multipart 上传测试的本地文件;不存在就跳过上传用例
  sampleFile: process.env.SAMPLE_FILE || path.join(__dirname, 'sample.jpg'),
};

const BASE = `http://${CONFIG.host}:${CONFIG.port}`;

// ---------- helpers ----------

let passed = 0;
let failed = 0;
const results = [];

function log(...args) {
  console.log(...args);
}

function assert(name, cond, detail = '') {
  if (cond) {
    passed++;
    log(`  ✓ ${name}`);
    results.push({ name, ok: true });
  } else {
    failed++;
    log(`  ✗ ${name}${detail ? ' — ' + detail : ''}`);
    results.push({ name, ok: false, detail });
  }
}

async function request(method, urlPath, { headers = {}, body, raw = false } = {}) {
  const url = urlPath.startsWith('http') ? urlPath : BASE + urlPath;
  const res = await fetch(url, { method, headers });
  const text = await res.text();
  let json = null;
  if (!raw) {
    try { json = JSON.parse(text); } catch { /* not json */ }
  }
  return { status: res.status, headers: res.headers, text, json };
}

function buildMultipart(filePath, filename, contentType) {
  const boundary = '----WebKitFormBoundary7MA4YWxkTrZu0gW';
  const fileBuf = fs.readFileSync(filePath);
  const head = Buffer.from(
    `--${boundary}\r\n` +
    `Content-Disposition: form-data; name="files"; filename="${filename}"\r\n` +
    `Content-Type: ${contentType}\r\n\r\n`
  );
  const tail = Buffer.from(`\r\n--${boundary}--\r\n`);
  return {
    body: Buffer.concat([head, fileBuf, tail]),
    contentType: `multipart/form-data; boundary=${boundary}`,
  };
}

// ---------- cases ----------

async function case1_health() {
  log('\n[1] GET /api/v1/health');
  const r = await request('GET', '/api/v1/health');
  assert('status 200', r.status === 200, `got ${r.status}`);
  assert('ok=true', r.json?.ok === true);
  assert('has secret', typeof r.json?.secret === 'string' && r.json.secret.length >= 16);
  assert('has port', typeof r.json?.port === 'number');
  return r.json;
}

async function case2_listProjects() {
  log('\n[2] GET /api/v1/projects?k=...');
  const r = await request('GET', `/api/v1/projects?k=${CONFIG.secret}`);
  assert('status 200', r.status === 200, `got ${r.status}: ${r.text}`);
  assert('items is array', Array.isArray(r.json?.items));
  return r.json;
}

async function case3_uploadToProject() {
  log('\n[3] POST /api/v1/projects/{name}/upload');
  if (!fs.existsSync(CONFIG.sampleFile)) {
    log(`  ! skip: sample file not found at ${CONFIG.sampleFile}`);
    return null;
  }
  const { body, contentType } = buildMultipart(
    CONFIG.sampleFile,
    path.basename(CONFIG.sampleFile),
    'image/jpeg'
  );
  const r = await request('POST', `/api/v1/projects/${CONFIG.project}/upload?k=${CONFIG.secret}`, {
    headers: { 'Content-Type': contentType, 'Content-Length': body.length },
    body,
  });
  assert('status 200', r.status === 200, `got ${r.status}: ${r.text}`);
  assert('success=true', r.json?.success === true);
  assert('count>=1', (r.json?.count ?? 0) >= 1);
  return r.json;
}

async function case4_uploadPage() {
  log('\n[4] GET /upload (H5 page)');
  const r = await request('GET', '/upload', { raw: true });
  assert('status 200', r.status === 200, `got ${r.status}`);
  assert('content-type html', /^text\/html/i.test(r.headers.get('content-type') || ''));
}

async function case5_legacyUpload() {
  log('\n[5] POST /upload (H5 legacy)');
  if (!fs.existsSync(CONFIG.sampleFile)) {
    log(`  ! skip: sample file not found at ${CONFIG.sampleFile}`);
    return;
  }
  const { body, contentType } = buildMultipart(
    CONFIG.sampleFile,
    `legacy-${path.basename(CONFIG.sampleFile)}`,
    'image/jpeg'
  );
  const r = await request('POST', '/upload', {
    headers: { 'Content-Type': contentType, 'Content-Length': body.length },
    body,
  });
  assert('status 200', r.status === 200, `got ${r.status}: ${r.text}`);
  assert('success=true', r.json?.success === true);
}

async function case6_negative() {
  log('\n[6] Negative cases');

  // 6.1 wrong secret
  const r1 = await request('GET', '/api/v1/projects?k=wrong_secret');
  assert('6.1 wrong secret → 401', r1.status === 401, `got ${r1.status}`);

  // 6.2 X-Key header
  const r2 = await request('GET', '/api/v1/projects', {
    headers: { 'X-Key': CONFIG.secret },
  });
  assert('6.2 X-Key header → 200', r2.status === 200, `got ${r2.status}`);

  // 6.3 method mismatch
  const r3 = await request('DELETE', `/api/v1/projects?k=${CONFIG.secret}`);
  assert('6.3 method mismatch → 405', r3.status === 405, `got ${r3.status}`);

  // 6.4 unknown path
  const r4 = await request('GET', '/api/v1/nope');
  assert('6.4 unknown path → 404', r4.status === 404, `got ${r4.status}`);

  // 6.5 project not found
  if (fs.existsSync(CONFIG.sampleFile)) {
    const { body, contentType } = buildMultipart(
      CONFIG.sampleFile,
      path.basename(CONFIG.sampleFile),
      'image/jpeg'
    );
    const r5 = await request('POST', '/api/v1/projects/no-such-project/upload?k=' + CONFIG.secret, {
      headers: { 'Content-Type': contentType, 'Content-Length': body.length },
      body,
    });
    assert('6.5 unknown project → 404', r5.status === 404, `got ${r5.status}`);
  }

  // 6.6 unsupported extension — 用纯文本构造一段 multipart
  const boundary = '----WebKitFormBoundary7MA4YWxkTrZu0gW';
  const badBody = Buffer.from(
    `--${boundary}\r\n` +
    `Content-Disposition: form-data; name="files"; filename="bad.txt"\r\n` +
    `Content-Type: text/plain\r\n\r\nhello\r\n` +
    `--${boundary}--\r\n`
  );
  const r6 = await request(
    'POST',
    `/api/v1/projects/${CONFIG.project}/upload?k=${CONFIG.secret}`,
    {
      headers: {
        'Content-Type': `multipart/form-data; boundary=${boundary}`,
        'Content-Length': badBody.length,
      },
      body: badBody,
    }
  );
  assert('6.6 unsupported ext → 200 with failed[]', r6.status === 200, `got ${r6.status}`);
  assert('6.6 failed array non-empty', (r6.json?.failed?.length ?? 0) >= 1);
}

// ---------- main ----------

(async () => {
  log(`StartTooler upload server smoke test`);
  log(`  target: ${BASE}`);
  log(`  project: ${CONFIG.project}`);
  log(`  secret: ${CONFIG.secret === 'replace_me_with_32_char_hex' ? '(NOT SET — fill CONFIG.secret first)' : CONFIG.secret.slice(0, 6) + '…'}`);
  log(`  sample file: ${CONFIG.sampleFile}`);

  try {
    await case1_health();
  } catch (e) {
    failed++;
    log(`  ✗ [1] health unreachable — ${e.message}`);
    log('\nServer 不可达,提前终止。请确认 UploadServer 已启动且 host/port 正确。');
    printSummary();
    process.exit(1);
  }

  try { await case2_listProjects(); } catch (e) { failed++; log(`  ✗ [2] ${e.message}`); }
  try { await case3_uploadToProject(); } catch (e) { failed++; log(`  ✗ [3] ${e.message}`); }
  try { await case4_uploadPage(); } catch (e) { failed++; log(`  ✗ [4] ${e.message}`); }
  try { await case5_legacyUpload(); } catch (e) { failed++; log(`  ✗ [5] ${e.message}`); }
  try { await case6_negative(); } catch (e) { failed++; log(`  ✗ [6] ${e.message}`); }

  printSummary();
  process.exit(failed === 0 ? 0 : 1);
})();

function printSummary() {
  log(`\n========== summary ==========`);
  log(`passed: ${passed}`);
  log(`failed: ${failed}`);
  log(`total : ${passed + failed}`);
}
