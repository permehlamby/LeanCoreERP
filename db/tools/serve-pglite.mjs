// Starts an in-process PostgreSQL (PGlite) with the migrations and the dev seed applied and serves it on the
// PostgreSQL wire protocol, so the C# code and its tests can connect with Npgsql without any installation.
//   node serve-pglite.mjs [port]      default port 5433, host 127.0.0.1 only
// There is no authentication: the process is a local test double and must never be exposed. Connect as any user.
// PGlite is one session at a time, so connections are served in turn. State lives in memory and vanishes on exit.
import { PGlite } from '@electric-sql/pglite';
import { btree_gist } from '@electric-sql/pglite/contrib/btree_gist';
import { PGLiteSocketServer } from '@electric-sql/pglite-socket';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const port = Number(process.argv[2] ?? 5433);

const db = new PGlite({ extensions: { btree_gist } });
const files = readdirSync(join(root, 'migrations')).filter((f) => f.endsWith('.sql')).sort().map((f) => join(root, 'migrations', f));
files.push(join(root, 'seed', 'dev_seed.sql'));
for (const f of files) await db.exec(readFileSync(f, 'utf8'));
// The seed sets a session tenant. A server must start without one.
await db.exec("select set_config('app.tenant_id', '', false)");

const server = new PGLiteSocketServer({ db, port, host: '127.0.0.1', maxConnections: 8 });
await server.start();
console.log(`READY pglite on 127.0.0.1:${port}`);
process.on('SIGINT', async () => { await server.stop(); await db.close(); process.exit(0); });
process.on('SIGTERM', async () => { await server.stop(); await db.close(); process.exit(0); });
