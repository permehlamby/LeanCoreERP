// Independent second implementation of the canonical form of a period_event row (04, 3.5a, hash_version 2).
// It reuses the field encoding of 02, 9.3a. Usage:
//   node verify-period-event-hash-v2.mjs period-event-hash-v2.json          verifies the expected values
//   node verify-period-event-hash-v2.mjs period-event-hash-v2.json --print  prints the vectors with fresh expected values
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const T = { ID: 'id', TEXT: 'text', INT: 'int', TS: 'ts' };
const f = (name, type, nullable = false) => ({ name, type, nullable });

const BODY = [
  f('event_id', T.ID), f('fiscal_year', T.INT), f('period_no', T.INT, true), f('event_type', T.TEXT),
  f('from_status', T.TEXT, true), f('to_status', T.TEXT), f('reason_code', T.TEXT, true), f('reason_text', T.TEXT, true),
  f('authorisation', T.TEXT, true), f('document_id', T.ID, true), f('event_at', T.TS), f('event_by', T.TEXT),
  f('correlation_id', T.TEXT), f('idempotency_key', T.TEXT),
];

const sha = (buf) => createHash('sha256').update(buf).digest();

function encodeValue(spec, v) {
  if (v === null || v === undefined) {
    if (!spec.nullable) throw new Error(`${spec.name} is not nullable`);
    return null;
  }
  let s;
  switch (spec.type) {
    case T.ID: if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(v)) throw new Error(`bad id ${v}`); s = v; break;
    case T.TEXT: s = v; break;
    case T.INT: s = BigInt(v).toString(); break;
    case T.TS: if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$/.test(v)) throw new Error(`bad timestamp ${v}`); s = v; break;
  }
  return Buffer.from(s, 'utf8');
}

function field(bytes) {
  if (bytes === null) return Buffer.from([0xff, 0xff, 0xff, 0xff]);
  const len = Buffer.alloc(4); len.writeUInt32BE(bytes.length);
  return Buffer.concat([len, bytes]);
}

function record(schema, values) {
  for (const k of Object.keys(values)) if (!schema.some((s) => s.name === k)) throw new Error(`unknown column ${k}`);
  return Buffer.concat(schema.map((s) => {
    if (!(s.name in values)) throw new Error(`missing column ${s.name}`);
    return field(encodeValue(s, values[s.name]));
  }));
}

function genesis(c) {
  return sha(Buffer.from(['genesis', c.tenant_id, c.company_id, 'PERIOD_EVENT', ''].join('|'), 'utf8'));
}

function compute(c) {
  const prev = c.prev_hash === 'GENESIS' ? genesis(c) : Buffer.from(c.prev_hash, 'hex');
  const text = (x) => Buffer.from(String(x), 'utf8');
  const row = sha(Buffer.concat([
    field(text('2')), field(text(c.tenant_id)), field(text(c.company_id)), field(text(c.event_seq)),
    field(text(prev.toString('hex'))), field(record(BODY, c.event)),
  ]));
  return { genesis: genesis(c).toString('hex'), row_hash: row.toString('hex') };
}

const [, , file, flag] = process.argv;
const vectors = JSON.parse(readFileSync(file, 'utf8'));
let failed = 0;
for (const c of vectors.cases) {
  const got = compute(c);
  if (flag === '--print') { c.expected = got; continue; }
  const ok = JSON.stringify(got) === JSON.stringify(c.expected);
  console.log(`${ok ? 'PASS' : 'FAIL'} ${c.name}`);
  if (!ok) { failed++; console.log(' got     ', JSON.stringify(got)); console.log(' expected', JSON.stringify(c.expected)); }
}
if (flag === '--print') console.log(JSON.stringify(vectors, null, 2));
process.exit(failed ? 1 : 0);
