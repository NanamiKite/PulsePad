// SPDX-License-Identifier: GPL-3.0-only
// node tools/serve-web.mjs [port] — serves the same /PulsePad/ prefix as GitHub Pages.
import {createServer} from 'node:http';
import {readFile, stat} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import path from 'node:path';

export const webRoot = fileURLToPath(new URL('../docs/', import.meta.url));
const types = {'.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml'};

export function createWebServer() {
  return createServer(async (request, response) => {
    if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405).end(); return; }
    try {
      const url = new URL(request.url, 'http://localhost');
      if (url.pathname === '/' || url.pathname === '/PulsePad') {
        response.writeHead(302, {Location: '/PulsePad/'}).end(); return;
      }
      if (!url.pathname.startsWith('/PulsePad/')) { response.writeHead(404).end(); return; }
      const relative = decodeURIComponent(url.pathname.slice('/PulsePad/'.length)) || 'index.html';
      const target = path.resolve(webRoot, relative);
      const within = path.relative(webRoot, target);
      if (within.startsWith('..') || path.isAbsolute(within) || relative.includes('\0') || relative.includes('\\')) {
        response.writeHead(403).end(); return;
      }
      if (!(await stat(target)).isFile()) { response.writeHead(404).end(); return; }
      const data = await readFile(target);
      response.writeHead(200, {'Content-Type': types[path.extname(target)] || 'text/plain; charset=utf-8',
        'Content-Length': data.length, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff'});
      response.end(request.method === 'HEAD' ? undefined : data);
    } catch { response.writeHead(404).end(); }
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(process.argv[2] || 4173);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Port must be 1–65535');
  const server = createWebServer();
  server.on('error', error => { console.error(error.message); process.exitCode = 1; });
  server.listen(port, '127.0.0.1', () => console.log(`PulsePad: http://localhost:${port}/PulsePad/  (Ctrl+C to stop)`));
}
