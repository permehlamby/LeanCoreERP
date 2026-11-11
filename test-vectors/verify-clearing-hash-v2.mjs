// Independent second implementation of the canonical form of a clearing row (03, 6.1, hash_version 2).
// It reuses the field encoding of 02, 9.3a. Usage:
//   node verify-clearing-hash-v2.mjs clearing-hash-v2.json          verifies the expected values
//   node verify-clearing-hash-v2.mjs clearing-hash-v2.json --print  prints the vectors with fresh expected values
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';

const T = { ID: 'id', TEXT: 'text', INT: 'int', DATE: 'date', TS: 'ts', AMT: 'amt' };
const f = (name, type, nullable = false) => ({ name, type, nullable });

const CLEARING = [
  f('clearing_id', T.ID), f('clearing_type', T.TEXT), f('clearing_date', T.DATE), f('fiscal_year', T.INT), f('fiscal_period', T.INT),
  f('cleared_at', T.TS), f('account_id', T.ID), f('partner_id', T.ID, true), f('document_id', T.ID, true),
  f('reset_of_clearing_id', T.ID, true), f('clearing_method', T.TEXT), f('matching_ref', T.TEXT, true), f('source_system', T.TEXT),
  f('idempotency_key', T.TEXT), f('command_name', T.TEXT), f('cleared_by', T.TEXT), f('correlation_id', T.TEXT),
];
const ITEM = [
  f('tenant_id', T.ID), f('company_id', T.ID), f('clearing_id', T.ID), f('item_no', T.INT), f('clearing_date', T.DATE),
  f('open_item_document_id', T.ID), f('open_item_line_no', T.INT), f('currency_tc', T.TEXT),
  f('cleared_amount_tc', T.AMT), f('cleared_amount_lc', T.AMT), f('cleared_amount_gc', T.AMT), f('cleared_amount_rc', T.AMT, true),
];

const sha = (buf) => createHash('sha256').update(buf).digest();

// Fixed-scale decimal from a decimal string, using BigInt only (no floating point).
function fixed(str, scale) {
  const m = /^(-?)(\d+)(?:\.(\d+))?$/.exec(String(str));
  if (!m) throw new Error(`not a decimal: ${str}`);
  let [, sign, whole, frac = ''] = m;
  frac = frac.replace(/0+$/, '');
  if (frac.length > scale) throw new Error(`${str} has more than ${scale} fraction digits`);
  frac = frac.padEnd(scale, '0');
  whole = whole.replace(/^0+(?=\d)/, '');
  const zero = /^0+$/.test(whole + frac);
  return (zero ? '' : sign) + whole + '.' + frac;
}

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
    case T.DATE: if (!/^\d{4}-\d{2}-\d{2}$/.test(v)) throw new Error(`bad date ${v}`); s = v; break;
    case T.TS: if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$/.test(v)) throw new Error(`bad timestamp ${v}`); s = v; break;
    case T.AMT: s = fixed(v, 4); break;
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
  return sha(Buffer.from(['genesis', c.tenant_id, c.company_id, 'CLEARING', ''].join('|'), 'utf8'));
}

function compute(c) {
  if (c.items.length === 0) throw new Error('a clearing has at least one item');
  const prev = c.prev_hash === 'GENESIS' ? genesis(c) : Buffer.from(c.prev_hash, 'hex');
  const text = (x) => Buffer.from(String(x), 'utf8');
  const items = [...c.items].sort((a, b) => a.item_no - b.item_no);
  const itemsDigest = sha(Buffer.concat(items.map((i) =>
    field(record(ITEM, { tenant_id: c.tenant_id, company_id: c.company_id, clearing_id: c.clearing.clearing_id, ...i })))));
  const linked = c.document_row_hash ? field(text(c.document_row_hash)) : field(null);
  const row = sha(Buffer.concat([
    field(text('2')), field(text(c.tenant_id)), field(text(c.company_id)), field(text(c.chain_seq)),
    field(text(prev.toString('hex'))), field(record(CLEARING, c.clearing)),
    field(text(itemsDigest.toString('hex'))), linked,
  ]));
  return { genesis: genesis(c).toString('hex'), items_digest: itemsDigest.toString('hex'), row_hash: row.toString('hex') };
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
