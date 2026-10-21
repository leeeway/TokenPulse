import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = (process.argv[2] || 'http://127.0.0.1:4345').replace(/\/$/, '');
const output = process.env.LWBLOG_QA_OUTPUT || path.resolve('dist/lwblog-qa');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
const errors = [];
const routes = ['/', '/projects/', ...['tokenpulse', 'cardpulse', 'daily-trend-radar', 'logmind'].map(id => `/projects/${id}/`)];
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const prefix of ['', '/en']) {
      for (const route of routes) {
        const url = base + prefix + route;
        const response = await page.goto(url, { waitUntil: 'networkidle' });
        assert.equal(response.status(), 200, url);
        assert.equal(await page.locator('html').getAttribute('lang'), prefix ? 'en' : 'zh-CN');
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Horizontal overflow: ${url} at ${width}`);
        assert.equal(await page.locator('img').evaluateAll(images => images.every(image => image.complete && image.naturalWidth > 0)), true, `Broken image: ${url}`);
        if (route === '/projects/') assert.equal(await page.locator('.project-tile').count(), 4);
        if (route === '/') assert.equal(await page.locator('#work .project-tile').count(), 3);
        if (route === '/projects/tokenpulse/') assert.equal(await page.locator('.download-grid a').count(), 2);
        if (route === '/projects/cardpulse/') assert.equal(await page.locator('.download-grid a').count(), 3);
      }
    }
    console.log(`All Chinese/English routes and images at ${width}px: passed`);
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(base + '/#work', { waitUntil: 'networkidle' });
  await page.locator('#work').scrollIntoViewIfNeeded();
  await page.locator('#work').screenshot({ path: path.join(output, 'home-projects-desktop.png') });
  await page.goto(base + '/projects/', { waitUntil: 'networkidle' });
  await page.screenshot({ path: path.join(output, 'projects-desktop.png'), fullPage: true });
  await page.goto(base + '/projects/tokenpulse/', { waitUntil: 'networkidle' });
  await page.screenshot({ path: path.join(output, 'tokenpulse-desktop.png'), fullPage: true });
  await page.getByRole('link', { name: 'Switch to English', exact: true }).click();
  await page.waitForURL(base + '/en/projects/tokenpulse/');
  assert.equal(await page.locator('html').getAttribute('lang'), 'en');
  await page.setViewportSize({ width: 320, height: 844 });
  await page.goto(base + '/projects/cardpulse/', { waitUntil: 'networkidle' });
  await page.screenshot({ path: path.join(output, 'cardpulse-mobile.png'), fullPage: true });
  await context.close();

  const noJS = await browser.newContext({ javaScriptEnabled: false });
  const staticPage = await noJS.newPage();
  await staticPage.goto(base + '/projects/tokenpulse/');
  assert.equal(await staticPage.locator('.download-grid a').count(), 2);
  assert.ok((await staticPage.locator('h1').innerText()).length > 0);
  await noJS.close();

  const appContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
  const app = await appContext.newPage();
  const appErrors = [];
  app.on('pageerror', error => appErrors.push(error.message));
  const blocked = [];
  app.on('console', message => { if (message.type() === 'error') blocked.push(message.text()); });
  await app.goto(base + '/apps/cardpulse/', { waitUntil: 'networkidle' });
  await app.getByRole('button', { name: '查看示例数据', exact: true }).click();
  await app.getByRole('button', { name: '退出演示数据', exact: true }).waitFor();
  for (const width of [390, 320]) {
    await app.setViewportSize({ width, height: 844 });
    assert.ok(await app.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `App overflow at ${width}`);
  }
  await appContext.close();
  const pdfContext = await browser.newContext();
  const pdf = await pdfContext.newPage();
  pdf.on('pageerror', error => appErrors.push(error.message));
  pdf.on('console', message => { if (message.type() === 'error') blocked.push(message.text()); });
  await pdf.goto(base + '/apps/cardpulse/', { waitUntil: 'networkidle' });
  await pdf.getByLabel('导入银行', { exact: true }).selectOption('hsbc');
  await pdf.locator('input[accept=".pdf,.csv"]').setInputFiles({ name: 'synthetic.pdf', mimeType: 'application/pdf', buffer: makeStatementPdf() });
  await pdf.getByText('synthetic.pdf：已导入 1 笔交易', { exact: false }).waitFor({ timeout: 15000 });
  assert.equal(await pdf.locator('tbody tr').count(), 1);
  assert.deepEqual(appErrors, []);
  assert.deepEqual(blocked, []);
  await pdfContext.close();
  assert.deepEqual(errors, []);
  console.log('Language navigation, JavaScript-disabled content, CardPulse demo, mobile and real PDF worker under CSP: passed');
} finally { await browser.close(); }

function makeStatementPdf() {
  const texts = [[10,780,'STATEMENT DATE 10 JAN 2026 CNY'],[10,770,'6222 XXXX XXXX 1234'],[10,750,'POST DATE TRANS DATE DESCRIPTION AMOUNT'],[230,740,'PREVIOUS BALANCE 100.00'],[10,720,'02 JAN'],[170,720,'02 JAN'],[230,720,'TEST SHOP'],[850,720,'50.00'],[230,700,'CLOSING BALANCE 150.00']];
  const stream = texts.map(([x,y,text]) => `BT /F1 10 Tf ${x} ${y} Td (${text}) Tj ET`).join('\n');
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>','<< /Type /Pages /Kids [3 0 R] /Count 1 >>','<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1000 800] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>','<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',`<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}\nendstream`];
  let out = '%PDF-1.4\n'; const offsets = [0];
  objects.forEach((object,i) => { offsets.push(Buffer.byteLength(out)); out += `${i+1} 0 obj\n${object}\nendobj\n`; });
  const start = Buffer.byteLength(out);
  out += `xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(n => `${String(n).padStart(10,'0')} 00000 n `).join('\n')}\ntrailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${start}\n%%EOF`;
  return Buffer.from(out);
}
